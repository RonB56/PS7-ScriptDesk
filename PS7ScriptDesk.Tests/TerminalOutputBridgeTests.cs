using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputBridgeTests
{
    [Fact]
    public void ResizeBarrier_CancelReturnsBufferedOutputForNonDestructiveRecovery()
    {
        var barrier = new TerminalResizeOutputBarrier(
            maximumBufferedCharacters: 128,
            maximumBufferedChunks: 8,
            maximumDuration: TimeSpan.FromSeconds(2));

        Assert.True(barrier.Begin(3, 7, 11, 120, 40).Accepted);
        Assert.Equal(
            TerminalResizeBarrierCaptureStatus.Buffered,
            barrier.Capture(3, 7, "ConPTY", "numbered-output").Status);

        var cancelled = barrier.Cancel();

        Assert.Equal(1, cancelled.BufferedChunks);
        var output = Assert.Single(cancelled.ReleasedOutput);
        Assert.Equal(7, output.TerminalSessionGeneration);
        Assert.Equal("numbered-output", output.Data);
        Assert.False(barrier.IsActive);
    }

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
    public void FlowController_ReplaysPreXtermOutputOnceAndDropsPriorGenerationOnReplacement()
    {
        var controller = new TerminalOutputFlowController(maximumPendingCharacters: 64, maximumBatchCharacters: 64);

        controller.ActivateGeneration(4);
        Assert.Equal(5, controller.Enqueue(4, "warm\n").AcceptedCharacters);
        Assert.Null(controller.TryBeginDelivery());

        Assert.True(controller.SetRendererReady());
        var replay = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal(4, replay.Generation);
        Assert.Equal("warm\n", replay.Data);
        Assert.False(controller.Acknowledge(4, replay.Sequence));
        Assert.Null(controller.TryBeginDelivery());

        controller.ActivateGeneration(5);
        Assert.Equal(4, controller.Enqueue(5, "new\n").AcceptedCharacters);
        Assert.True(controller.TryBeginDelivery() is { Generation: 5, Data: "new\n" });
    }

    [Fact]
    public void FlowController_RestoresDeliveryAfterRendererReplacement()
    {
        var controller = new TerminalOutputFlowController(maximumPendingCharacters: 64, maximumBatchCharacters: 64);

        controller.ActivateGeneration(1);
        controller.Enqueue(1, "before\n");
        var unavailable = controller.MarkRendererUnavailable();
        Assert.Equal(7, unavailable.DiscardedCharacters);
        Assert.Equal(7, controller.Enqueue(1, "during\n").DroppedCharacters);

        controller.RestoreRenderer();
        Assert.Equal(6, controller.Enqueue(1, "after\n").AcceptedCharacters);
        Assert.True(controller.SetRendererReady());
        var batch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("after\n", batch.Data);
    }

    [Fact]
    public void PersistentOutputHistory_IsSessionOwnedAndBounded()
    {
        var history = new TerminalPersistentOutputHistory(maximumCharacters: 5);

        history.ActivateGeneration(3);
        Assert.True(history.Append(3, "abc"));
        Assert.True(history.Append(3, "def"));
        Assert.Equal(5, history.CharacterCount);
        Assert.Equal(new[] { "bc", "def" }, history.Snapshot(3));

        history.ActivateGeneration(4);
        Assert.Empty(history.Snapshot(3));
        Assert.Empty(history.Snapshot(4));
        Assert.True(history.Append(4, "new"));
        Assert.Equal(new[] { "new" }, history.Snapshot(4));
    }

    [Fact]
    public void PersistentOutputHistory_PreservesCompletedNumberedRunAcrossGeometryOnlyToggle()
    {
        var history = new TerminalPersistentOutputHistory(maximumCharacters: 32 * 1024);
        const int sessionGeneration = 1;
        var completedRun = string.Join(
            "\r\n",
            new[] { "COMPLETION_TEST_START" }
                .Concat(Enumerable.Range(1, 50).Select(number => $"COMPLETION_TEST {number:D3}"))
                .Append("COMPLETION_TEST_END")
                .Append("PS> "));

        history.ActivateGeneration(sessionGeneration);
        Assert.True(history.Append(sessionGeneration, completedRun));

        // A Problems open/close and resize are geometry-only from the persistent
        // history boundary; they must not activate a new session or rewrite bytes.
        var afterProblemsToggle = string.Concat(history.Snapshot(sessionGeneration));

        Assert.Equal(completedRun, afterProblemsToggle);
        Assert.Equal(1, CountOccurrences(afterProblemsToggle, "COMPLETION_TEST_START"));
        Assert.Equal(1, CountOccurrences(afterProblemsToggle, "COMPLETION_TEST_END"));
        for (var number = 1; number <= 50; number++)
        {
            var marker = $"COMPLETION_TEST {number:D3}";
            Assert.Equal(1, CountOccurrences(afterProblemsToggle, marker));
            if (number < 50)
            {
                Assert.True(
                    afterProblemsToggle.IndexOf(marker, StringComparison.Ordinal)
                    < afterProblemsToggle.IndexOf($"COMPLETION_TEST {number + 1:D3}", StringComparison.Ordinal));
            }
        }
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(50)]
    public void RendererRecoveryStateMachine_RemainsReusableAcrossRepeatedCycles(int cycleCount)
    {
        var state = new TerminalRendererRecoveryStateMachine();

        for (var cycle = 1; cycle <= cycleCount; cycle++)
        {
            var rendererGeneration = cycle;
            state.RendererCreated(rendererGeneration);
            if (cycle == 1)
            {
                Assert.False(state.RendererReady(rendererGeneration));
            }
            else
            {
                Assert.True(state.ReplacementPending);
                Assert.True(state.RendererReady(rendererGeneration));
                Assert.True(state.ReplayPending);
                Assert.True(state.ReplayCompleted(rendererGeneration));
                Assert.False(state.ReplacementPending);
                Assert.False(state.ReplayPending);
            }

            state.RendererRetired(rendererGeneration);
            Assert.True(state.ReplacementPending);
            Assert.False(state.RendererReady(rendererGeneration - 1));
        }

        Assert.Equal(cycleCount, state.RendererGeneration);
        Assert.Equal(Math.Max(0, cycleCount), state.Cycle);
    }

    [Fact]
    public void RendererRecoveryStateMachine_RejectsStaleReadyAndAcceptsCurrentRenderer()
    {
        var state = new TerminalRendererRecoveryStateMachine();
        state.RendererCreated(1);
        Assert.False(state.RendererReady(1));
        state.RendererRetired(1);
        state.RendererCreated(2);

        Assert.False(state.RendererReady(1));
        Assert.True(state.RendererReady(2));
        Assert.True(state.ReplayCompleted(2));
        Assert.False(state.ReplayCompleted(1));
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
