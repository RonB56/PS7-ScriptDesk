using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputBridgeTests
{
    [Fact]
    public void FlowController_DefersOutputUntilRendererIsReady_ThenPreservesChunkOrder()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 64,
            maximumBatchCharacters: 16);

        controller.ActivateGeneration(1);
        Assert.False(controller.Enqueue(1, "first").ScheduleFlush);
        Assert.False(controller.Enqueue(1, "\x1b[32msecond\x1b[0m").ScheduleFlush);
        Assert.True(controller.SetRendererReady());

        var firstBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("first\x1b[32msecond", firstBatch.Data);
        Assert.Null(controller.TryBeginDelivery());
        Assert.True(controller.Acknowledge(1, firstBatch.Sequence));

        var secondBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("\x1b[0m", secondBatch.Data);
        Assert.False(controller.Acknowledge(1, secondBatch.Sequence));
        Assert.Null(controller.TryBeginDelivery());
    }

    [Fact]
    public void FlowController_BoundsPendingOutputAndDropsOnlyOverloadChunks()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 8,
            maximumBatchCharacters: 4);

        controller.ActivateGeneration(1);
        Assert.False(controller.Enqueue(1, "abcd").ScheduleFlush);
        Assert.True(controller.SetRendererReady());
        var firstBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());

        var queued = controller.Enqueue(1, "efgh");
        var dropped = controller.Enqueue(1, "ij");

        Assert.False(queued.ScheduleFlush);
        Assert.Equal(0, queued.DroppedCharacters);
        Assert.Equal(2, dropped.DroppedCharacters);
        Assert.Equal(8, dropped.PendingCharacters);

        Assert.True(controller.Acknowledge(1, firstBatch.Sequence));
        var secondBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("efgh", secondBatch.Data);
        Assert.False(controller.Acknowledge(1, firstBatch.Sequence));
        Assert.False(controller.Acknowledge(1, secondBatch.Sequence));
    }

    [Fact]
    public void FlowController_SplitsLargeChunkWithoutReorderingLaterChunks()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 32,
            maximumBatchCharacters: 4);

        controller.ActivateGeneration(1);
        controller.Enqueue(1, "abcdef");
        controller.Enqueue(1, "gh");
        Assert.True(controller.SetRendererReady());

        var first = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("abcd", first.Data);
        Assert.True(controller.Acknowledge(1, first.Sequence));

        var second = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("efgh", second.Data);
        Assert.False(controller.Acknowledge(1, second.Sequence));
    }

    [Fact]
    public void WebMessageSerializer_Base64RoundTripsUnicodeAndIncludesAcknowledgementSequence()
    {
        const string terminalData = "αβ\r\n\x1b[31mred\x1b[0m";

        var json = TerminalWebMessageSerializer.SerializeOutput(7, 42, terminalData);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("output_b64", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("generation").GetInt32());
        Assert.Equal(42, root.GetProperty("sequence").GetInt64());
        var encoded = root.GetProperty("data").GetString();
        Assert.NotNull(encoded);
        Assert.Equal(
            terminalData,
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void TerminalOutputControlClassifier_ClassifiesRepresentativeControls()
    {
        const string terminalData =
            "\r\n\u001b[A\u001b[2B\u001b[3C\u001b[4D\u001b[10;20H\u001b[2K\u001b[J\u001b[s\u001b[u" +
            "\u001b[L\u001b[M\u001b[S\u001b[T\u001b[31m\u001b]0;title\aX";

        var summary = TerminalOutputControlClassifier.Summarize(terminalData);

        Assert.Equal(1, summary.CarriageReturnCount);
        Assert.Equal(1, summary.LineFeedCount);
        Assert.Equal(1, summary.CarriageReturnLineFeedPairCount);
        Assert.Equal(15, summary.EscapeCount);
        Assert.Equal(14, summary.CsiCount);
        Assert.Equal(1, summary.CsiCursorUpCount);
        Assert.Equal(1, summary.CsiCursorDownCount);
        Assert.Equal(1, summary.CsiCursorForwardCount);
        Assert.Equal(1, summary.CsiCursorBackwardCount);
        Assert.Equal(1, summary.CsiCursorPositionCount);
        Assert.Equal(1, summary.CsiEraseLineCount);
        Assert.Equal(1, summary.CsiEraseDisplayCount);
        Assert.Equal(1, summary.CsiSaveCursorCount);
        Assert.Equal(1, summary.CsiRestoreCursorCount);
        Assert.Equal(1, summary.CsiInsertLineCount);
        Assert.Equal(1, summary.CsiDeleteLineCount);
        Assert.Equal(1, summary.CsiScrollUpCount);
        Assert.Equal(1, summary.CsiScrollDownCount);
        Assert.Equal(1, summary.CsiSgrCount);
        Assert.Equal(1, summary.OscCount);
        Assert.Equal(1, summary.PrintableCharacterCount);
        Assert.Contains("CSI_CursorDown=1", summary.ToDiagnosticString(), StringComparison.Ordinal);
        Assert.Contains("OSC=1", summary.ToDiagnosticString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WebMessageSerializer_DiagnosticMetadataDoesNotAlterPayloadOrExposePlainText()
    {
        const string terminalData = "secret-command\r\n\u001b[2B";
        var controlSummary = TerminalOutputControlClassifier.Summarize(terminalData).ToDiagnosticString();

        var json = TerminalWebMessageSerializer.SerializeOutput(
            7,
            42,
            terminalData,
            rendererGeneration: 3,
            submissionId: 99,
            resizeAdjacent: true,
            resizeGeneration: 123,
            resizeElapsedMilliseconds: 12.5,
            hostControlSummary: controlSummary);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.DoesNotContain("secret-command", json, StringComparison.Ordinal);
        Assert.Equal(3, root.GetProperty("rendererGeneration").GetInt32());
        Assert.Equal(99, root.GetProperty("submissionId").GetInt64());
        Assert.True(root.GetProperty("resizeAdjacent").GetBoolean());
        Assert.Equal(123, root.GetProperty("resizeGeneration").GetInt64());
        Assert.Equal(12.5, root.GetProperty("resizeElapsedMilliseconds").GetDouble());
        Assert.Equal(terminalData.Length, root.GetProperty("outputCharacterLength").GetInt32());
        Assert.True(root.GetProperty("contentOmitted").GetBoolean());
        Assert.Contains("CR=1", root.GetProperty("hostControlSummary").GetString(), StringComparison.Ordinal);
        Assert.Contains("LF=1", root.GetProperty("hostControlSummary").GetString(), StringComparison.Ordinal);
        Assert.Contains("CSI_CursorDown=1", root.GetProperty("hostControlSummary").GetString(), StringComparison.Ordinal);

        var encoded = root.GetProperty("data").GetString();
        Assert.NotNull(encoded);
        Assert.Equal(
            terminalData,
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void WebMessageSerializer_ResizeCommitCarriesGeometryWithoutTerminalOutputPayload()
    {
        var json = TerminalWebMessageSerializer.SerializeResizeCommit(
            rendererGeneration: 17,
            terminalSessionGeneration: 9,
            resizeGeneration: 42,
            columns: 132,
            rows: 34);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("resize_commit", root.GetProperty("type").GetString());
        Assert.Equal(17, root.GetProperty("rendererGeneration").GetInt32());
        Assert.Equal(9, root.GetProperty("terminalSessionGeneration").GetInt32());
        Assert.Equal(42, root.GetProperty("resizeGeneration").GetInt64());
        Assert.Equal(132, root.GetProperty("cols").GetInt32());
        Assert.Equal(34, root.GetProperty("rows").GetInt32());
        Assert.False(root.TryGetProperty("data", out _));
    }

    [Fact]
    public void ResizeOutputBarrier_BuffersInOrderAndReleasesExactlyOnceForExactAck()
    {
        var barrier = new TerminalResizeOutputBarrier(
            maximumBufferedCharacters: 32,
            maximumBufferedChunks: 4,
            maximumDuration: TimeSpan.FromSeconds(1));
        var startedAt = DateTimeOffset.UtcNow;

        Assert.True(barrier.Begin(7, 11, 3, 120, 30, startedAt).Accepted);
        Assert.Equal(
            TerminalResizeBarrierCaptureStatus.Buffered,
            barrier.Capture(7, 11, "ConPTY", "first", startedAt).Status);
        Assert.Equal(
            TerminalResizeBarrierCaptureStatus.Buffered,
            barrier.Capture(7, 11, "ConPTY", "\x1b[2Jsecond", startedAt).Status);

        var stale = barrier.Acknowledge(7, 11, 2, 120, 30);
        Assert.False(stale.Accepted);
        Assert.True(barrier.IsActive);

        var acknowledged = barrier.Acknowledge(7, 11, 3, 120, 30);
        Assert.True(acknowledged.Accepted);
        Assert.Equal("first\x1b[2Jsecond", string.Concat(acknowledged.ReleasedOutput.Select(item => item.Data)));
        Assert.Equal(15, acknowledged.BufferedCharacters);
        Assert.False(barrier.IsActive);

        var duplicate = barrier.Acknowledge(7, 11, 3, 120, 30);
        Assert.False(duplicate.Accepted);
        Assert.Empty(duplicate.ReleasedOutput);
    }

    [Fact]
    public void ResizeOutputBarrier_EnforcesCharacterAndChunkBoundsWithoutBlocking()
    {
        var barrier = new TerminalResizeOutputBarrier(
            maximumBufferedCharacters: 5,
            maximumBufferedChunks: 2,
            maximumDuration: TimeSpan.FromSeconds(1));
        var startedAt = DateTimeOffset.UtcNow;

        Assert.True(barrier.Begin(1, 2, 1, 80, 24, startedAt).Accepted);
        Assert.Equal(TerminalResizeBarrierCaptureStatus.Buffered, barrier.Capture(1, 2, "ConPTY", "123", startedAt).Status);
        Assert.Equal(TerminalResizeBarrierCaptureStatus.Buffered, barrier.Capture(1, 2, "ConPTY", "45", startedAt).Status);
        var overflow = barrier.Capture(1, 2, "ConPTY", "6", startedAt);

        Assert.Equal(TerminalResizeBarrierCaptureStatus.BoundedLimitExceeded, overflow.Status);
        Assert.Equal(5, overflow.TotalBufferedCharacters);
        Assert.True(barrier.Expire(startedAt.AddSeconds(2)).Expired);
    }

    [Fact]
    public void FlowController_ReplacementDiscardsPriorGenerationAndRejectsStaleAcknowledgements()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 8,
            maximumBatchCharacters: 4);

        controller.ActivateGeneration(10);
        controller.Enqueue(10, "abcd");
        Assert.True(controller.SetRendererReady());
        var oldBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        controller.Enqueue(10, "efgh");

        var replacement = controller.ActivateGeneration(11);

        Assert.Equal(8, replacement.DiscardedCharacters);
        Assert.Null(controller.TryBeginDelivery());
        Assert.False(controller.Acknowledge(10, oldBatch.Sequence));
        Assert.Equal(3, controller.Enqueue(10, "old").RejectedStaleCharacters);

        Assert.True(controller.Enqueue(11, "new").ScheduleFlush);
        var currentBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal(11, currentBatch.Generation);
        Assert.Equal("new", currentBatch.Data);
        Assert.False(controller.Acknowledge(10, currentBatch.Sequence));
        Assert.False(controller.Acknowledge(11, oldBatch.Sequence));
        Assert.False(controller.Acknowledge(11, currentBatch.Sequence));
        Assert.Null(controller.TryBeginDelivery());
    }

    [Fact]
    public void FlowController_RendererUnavailableDiscardsQueuedAndInFlightOutputWithoutAllowingReactivation()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 16,
            maximumBatchCharacters: 4);

        controller.ActivateGeneration(3);
        controller.Enqueue(3, "abcd");
        Assert.True(controller.SetRendererReady());
        var inFlightBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        controller.Enqueue(3, "ef");

        var unavailable = controller.MarkRendererUnavailable();

        Assert.Equal(6, unavailable.DiscardedCharacters);
        Assert.False(controller.SetRendererReady());
        Assert.Null(controller.TryBeginDelivery());
        Assert.False(controller.Acknowledge(3, inFlightBatch.Sequence));

        var laterOutput = controller.Enqueue(3, "later");
        Assert.Equal(5, laterOutput.DroppedCharacters);
        Assert.Equal(0, laterOutput.AcceptedCharacters);

        controller.ActivateGeneration(4);
        Assert.False(controller.SetRendererReady());
        Assert.Null(controller.TryBeginDelivery());
    }
}
