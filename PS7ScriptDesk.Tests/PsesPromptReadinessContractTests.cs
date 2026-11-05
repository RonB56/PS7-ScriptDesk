using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesPromptReadinessContractTests
{
    [Fact]
    public void ForegroundAdmissionDependsOnSuccessfulDapStageAndSessionIdentity_NotStatusFile()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var admission = source.IndexOf("if (status == EditorExecutionStatus.Completed && protocolResult.ResponseReceived)", StringComparison.Ordinal);
        var requested = source.IndexOf("foregroundRequested = true", admission, StringComparison.Ordinal);

        Assert.True(admission >= 0 && requested > admission);
        Assert.DoesNotContain("File.Exists(statusPath)", source[admission..requested], StringComparison.Ordinal);
        Assert.Contains("innerInvocation + \"\\n\"", source, StringComparison.Ordinal);
        Assert.Contains("WriteAllText({quotedStatusPath}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeMarkerIsReadAfterForegroundRequestEvenWhenReadinessIsUnknown()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var markerRead = source.IndexOf("if (foregroundRequested && File.Exists(statusPath))", StringComparison.Ordinal);
        var resultCreation = source.IndexOf("STRUCTURED_RESULT_CREATED", markerRead, StringComparison.Ordinal);

        Assert.True(markerRead >= 0 && resultCreation > markerRead);
        Assert.Contains("if (!foregroundCompleted)", source[..markerRead], StringComparison.Ordinal);
        Assert.Contains("status = EditorExecutionStatus.Unknown", source[..markerRead], StringComparison.Ordinal);
        Assert.Contains("scriptOutcomeVerified = true", source[markerRead..resultCreation], StringComparison.Ordinal);
        Assert.Contains("inputConsumptionVerified", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcatenatedPsesBannerAndPrompt_IsRecognizedAsReadyBoundary()
    {
        var pattern = typeof(PsesOwnedInteractiveSessionService)
            .GetField("PromptBoundaryRegex", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Regex;

        Assert.NotNull(pattern);
        Assert.Matches(pattern!, "=====> PowerShell Editor Services <=====PS C:\\Program Files\\PowerShell>");
    }

    [Fact]
    public void OrdinaryOutputContainingPsText_DoesNotBecomeReadyBoundary()
    {
        var pattern = typeof(PsesOwnedInteractiveSessionService)
            .GetField("PromptBoundaryRegex", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Regex;

        Assert.NotNull(pattern);
        Assert.DoesNotMatch(pattern!, "status=PS not a prompt>");
    }

    [Theory]
    [InlineData("PS C:\\>")]
    [InlineData("4\r\nPS C:\\>")]
    [InlineData("\u001b[32mPS C:\\>\u001b[0m")]
    public void DriveRootPrompt_IsRecognizedAsReadyBoundary(string output)
    {
        var pattern = typeof(PsesOwnedInteractiveSessionService)
            .GetField("PromptBoundaryRegex", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Regex;

        Assert.NotNull(pattern);
        var normalized = Regex.Replace(output, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        Assert.Matches(pattern!, normalized);
    }

    [Fact]
    public void ProgressCursorMoveToColumnOne_QualifiesFollowingPromptBoundary()
    {
        var observe = typeof(PsesOwnedInteractiveSessionService)
            .GetMethod("ObservePromptBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observe);
        using var service = new PsesOwnedInteractiveSessionService();

        Assert.True((bool)observe!.Invoke(service, ["R38-NATIVE-OK\u001b[40;1H\u001b[?25hPS C:\\>", 0])!);
    }

    [Fact]
    public void PsTextWithoutCursorBoundary_DoesNotBecomePrompt()
    {
        var observe = typeof(PsesOwnedInteractiveSessionService)
            .GetMethod("ObservePromptBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observe);
        using var service = new PsesOwnedInteractiveSessionService();

        Assert.False((bool)observe!.Invoke(service, ["R38-NATIVE-OKPS C:\\>", 0])!);
    }

    [Fact]
    public void AnsiDecoratedPromptSplitAcrossConPtyReads_IsRecognized()
    {
        var observe = typeof(PsesOwnedInteractiveSessionService)
            .GetMethod("ObservePromptBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observe);
        using var service = new PsesOwnedInteractiveSessionService();

        Assert.False((bool)observe!.Invoke(service, ["\u001b[32mPS C:\\", 0])!);
        Assert.True((bool)observe.Invoke(service, [">\u001b[0m", 0])!);
    }

    [Fact]
    public void CarriageReturnAndSplitCursorReposition_PreservePromptBoundary()
    {
        var observe = typeof(PsesOwnedInteractiveSessionService)
            .GetMethod("ObservePromptBoundary", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observe);
        using var service = new PsesOwnedInteractiveSessionService();

        Assert.False((bool)observe!.Invoke(service, ["progress 100%\r\u001b[12;", 0])!);
        Assert.True((bool)observe.Invoke(service, ["1HPS C:\\>", 0])!);
    }

    [Fact]
    public void PromptBoundaryBeforeStatusCannotCompleteReadinessWithoutAnotherPostStatusPrompt()
    {
        var signal = new PromptRestorationSignal(5);

        signal.ObservePrompt(5, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true);
        Assert.False(signal.Snapshot().Completed);
        signal.ObserveStatusFileVisible();
        Assert.False(signal.Snapshot().Completed);
        signal.ObservePrompt(5, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true);
        Assert.True(signal.Snapshot().Completed);
    }

    [Theory]
    [InlineData(new byte[] { 0x1b, (byte)'[', (byte)'4', (byte)'0', (byte)';', (byte)'1', (byte)'H' }, 1)]
    [InlineData(new byte[] { 0x1b, (byte)'[', (byte)'4', (byte)'0', (byte)';' }, 2)]
    public void PromptFrameDiagnostics_ReconstructsCsiFrameBoundaries(byte[] firstFrame, int expectedFrames)
    {
        var diagnostics = new PromptRecoveryFrameDiagnostics(capacity: 8);
        var first = diagnostics.Record(firstFrame, 3, Guid.NewGuid(), false, false, "no-prompt-candidate", true, false);
        if (expectedFrames > 1)
        {
            var second = diagnostics.Record(new byte[] { (byte)'0', (byte)';', (byte)'1', (byte)'H' }, 3, null,
                false, false, "no-prompt-candidate", true, false);
            Assert.True(second.CsiSpannedFrames);
            Assert.True(second.CsiEnded);
            Assert.Equal("CSI-CursorPosition", second.SequenceTypes);
            Assert.Equal("1B5B34303B303B3148", second.ControlHex);
        }
        else
        {
            Assert.True(first.CsiEnded);
            Assert.Equal("CSI-CursorPosition", first.SequenceTypes);
        }
        Assert.Equal(expectedFrames, diagnostics.Snapshot().Frames.Length);
    }

    [Fact]
    public void PromptFrameDiagnostics_EvictsOldestRecordWhenBounded()
    {
        var diagnostics = new PromptRecoveryFrameDiagnostics(capacity: 2);
        diagnostics.Record(new byte[] { 65 }, 1, null, false, false, "none", true, false);
        diagnostics.Record(new byte[] { 66 }, 1, null, false, false, "none", true, false);
        diagnostics.Record(new byte[] { 67 }, 1, null, false, false, "none", true, false);
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(2, snapshot.Frames.Length);
        Assert.Equal(1, snapshot.Evicted);
        Assert.Equal(2, snapshot.Capacity);
        Assert.Equal(3, snapshot.TotalRecorded);
        Assert.Equal(new long[] { 2, 3 }, snapshot.Frames.Select(frame => frame.Sequence));
    }

    [Fact]
    public void DeveloperDiagnostics_SerializesBoundedFrameMetadataAsStructuredRecords()
    {
        var sanitize = typeof(DeveloperDiagnostics).GetMethod("SanitizeValue", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sanitize);
        IReadOnlyDictionary<string, object?>[] frames =
        [
            new Dictionary<string, object?> { ["sequence"] = 17L, ["generation"] = 1, ["requestId"] = "request-a", ["sequenceTypes"] = "CSI-CursorPosition", ["controlHex"] = "1B5B34303B3148" },
            new Dictionary<string, object?> { ["sequence"] = 18L, ["generation"] = 1, ["requestId"] = "request-a", ["sequenceTypes"] = "", ["controlHex"] = null }
        ];

        var sanitized = sanitize!.Invoke(null, [frames]);
        var json = JsonSerializer.Serialize(sanitized);
        using var document = JsonDocument.Parse(json);
        var records = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, records.Length);
        Assert.Equal(17, records[0].GetProperty("sequence").GetInt64());
        Assert.Equal(18, records[1].GetProperty("sequence").GetInt64());
        Assert.Equal("request-a", records[0].GetProperty("requestId").GetString());
        Assert.Equal("CSI-CursorPosition", records[0].GetProperty("sequenceTypes").GetString());
        Assert.Equal("1B5B34303B3148", records[0].GetProperty("controlHex").GetString());
        Assert.DoesNotContain("PromptFrameDiagnostic[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptFrameDiagnostics_ReconstructsEveryCsiSplitIncludingThreeFrames()
    {
        var sequence = new byte[] { 0x1b, (byte)'[', (byte)'1', (byte)'2', (byte)';', (byte)'3', (byte)'4', (byte)'H' };
        for (var firstSplit = 1; firstSplit < sequence.Length; firstSplit++)
        {
            var diagnostics = new PromptRecoveryFrameDiagnostics(capacity: 8);
            diagnostics.Record(sequence.AsSpan(0, firstSplit), 1, null, false, false, "none", true, false);
            var last = diagnostics.Record(sequence.AsSpan(firstSplit), 1, null, false, false, "none", true, false);
            Assert.True(last.CsiSpannedFrames);
            Assert.True(last.CsiEnded);
            Assert.Equal("CSI-CursorPosition", last.SequenceTypes);
            Assert.Equal(Convert.ToHexString(sequence), last.ControlHex);
        }

        var threeFrame = new PromptRecoveryFrameDiagnostics(capacity: 8);
        threeFrame.Record(sequence.AsSpan(0, 1), 1, null, false, false, "none", true, false);
        threeFrame.Record(sequence.AsSpan(1, 4), 1, null, false, false, "none", true, false);
        var completed = threeFrame.Record(sequence.AsSpan(5), 1, null, false, false, "none", true, false);
        Assert.True(completed.CsiSpannedFrames);
        Assert.True(completed.CsiEnded);
        Assert.Equal(Convert.ToHexString(sequence), completed.ControlHex);
    }
}
