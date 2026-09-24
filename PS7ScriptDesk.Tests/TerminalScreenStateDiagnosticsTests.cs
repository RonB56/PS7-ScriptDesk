using System.Text.Json;
using System.IO;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalScreenStateDiagnosticsTests
{
    [Fact]
    public void MarkerRowMetadataIsExactAndRetainsNoArbitraryContent()
    {
        var metadata = TerminalScreenStateDiagnostics.CreateMarkerRowMetadata(
            "secret-before-PHASEB_INTERRUPT_TICK-secret-after", 12, 3, 9, 2, false, 7, 44);
        var json = JsonSerializer.Serialize(metadata);

        Assert.Equal("exact", metadata["markerMatchKind"]);
        Assert.Equal(TerminalScreenStateDiagnostics.StableMarkerIdentity, metadata["markerIdentity"]);
        Assert.DoesNotContain("secret-before", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-after", json, StringComparison.Ordinal);
        Assert.Contains("contentOmitted", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NonMarkerRowProducesNoRetainedIdentity()
    {
        var metadata = TerminalScreenStateDiagnostics.CreateMarkerRowMetadata(
            "ordinary terminal content", 1, 1, 0, 0, false, 1, 1);

        Assert.Equal("none", metadata["markerMatchKind"]);
        Assert.Equal(0, metadata["markerPrefixLength"]);
        Assert.Equal(-1, metadata["markerStartColumn"]);
    }

    [Fact]
    public void SplitMarkerSuffixIsRecognizedAsBoundedPartial()
    {
        var metadata = TerminalScreenStateDiagnostics.CreateMarkerRowMetadata(
            "prefix PHASEB_INTER", 4, 4, 8, 1, true, 2, 3);

        Assert.Equal("partial", metadata["markerMatchKind"]);
        Assert.Equal(12, metadata["markerPrefixLength"]);
        Assert.True((bool)metadata["contentOmitted"]!);
    }

    [Fact]
    public void MarkerPresenceCheckDoesNotRetainPayload()
    {
        Assert.True(TerminalScreenStateDiagnostics.ContainsInterruptMarker("x-PHASEB_INTERRUPT_TICK-y"));
        Assert.False(TerminalScreenStateDiagnostics.ContainsInterruptMarker("ordinary"));
        Assert.Equal(64, TerminalScreenStateDiagnostics.MaxInspectedRows);
    }

    [Fact]
    public void MarkerRowsJsonIsNormalizedWithIndividualMarkersAndSequenceGapsVisible()
    {
        var json = "[{\"markerIdentity\":\"completion-start-7b3e\",\"markerMatchKind\":\"exact\",\"markerPrefixLength\":20,\"markerStartColumn\":0,\"markerEndColumn\":20,\"numberedSequence\":null,\"absoluteRow\":4,\"viewportRelativeRow\":4,\"isWrapped\":false},{\"markerIdentity\":\"completion-numbered-c2c\",\"markerMatchKind\":\"exact\",\"markerPrefixLength\":15,\"markerStartColumn\":0,\"markerEndColumn\":15,\"numberedSequence\":1,\"absoluteRow\":5,\"viewportRelativeRow\":5,\"isWrapped\":true},{\"markerIdentity\":\"completion-numbered-c2c\",\"markerMatchKind\":\"exact\",\"markerPrefixLength\":15,\"markerStartColumn\":0,\"markerEndColumn\":15,\"numberedSequence\":13,\"absoluteRow\":17,\"viewportRelativeRow\":17,\"isWrapped\":false},{\"markerIdentity\":\"completion-numbered-c2c\",\"markerMatchKind\":\"exact\",\"markerPrefixLength\":15,\"markerStartColumn\":0,\"markerEndColumn\":15,\"numberedSequence\":14,\"absoluteRow\":18,\"viewportRelativeRow\":18,\"isWrapped\":false},{\"markerIdentity\":\"completion-numbered-c2c\",\"markerMatchKind\":\"exact\",\"markerPrefixLength\":15,\"markerStartColumn\":0,\"markerEndColumn\":15,\"numberedSequence\":50,\"absoluteRow\":54,\"viewportRelativeRow\":54,\"isWrapped\":false},{\"markerIdentity\":\"completion-end-4a91\",\"markerMatchKind\":\"exact\",\"markerPrefixLength\":18,\"markerStartColumn\":0,\"markerEndColumn\":18,\"numberedSequence\":null,\"absoluteRow\":55,\"viewportRelativeRow\":55,\"isWrapped\":false}]";

        var accepted = TerminalMarkerSnapshotDiagnostics.TryNormalize(json, out var normalized, out var summary);

        Assert.True(accepted);
        Assert.Contains("completion-start-7b3e", normalized, StringComparison.Ordinal);
        Assert.Contains("\"NumberedSequence\":1", normalized, StringComparison.Ordinal);
        Assert.Contains("\"NumberedSequence\":13", normalized, StringComparison.Ordinal);
        Assert.Contains("\"NumberedSequence\":14", normalized, StringComparison.Ordinal);
        Assert.Contains("\"NumberedSequence\":50", normalized, StringComparison.Ordinal);
        Assert.Contains("\"AbsoluteRow\":17", normalized, StringComparison.Ordinal);
        Assert.True(summary.HasStart);
        Assert.True(summary.HasEnd);
        Assert.Equal(1, summary.LowestNumberedMarker);
        Assert.Equal(50, summary.HighestNumberedMarker);
        Assert.Equal(4, summary.NumberedMarkerCount);
        Assert.Equal("[1,13,14,50]", summary.NumberedSequenceJson);
        Assert.Equal(6, summary.MarkerRowCount);
    }

    [Fact]
    public void MalformedOrUnboundedMarkerRowsJsonIsRejectedWithoutPayloadRetention()
    {
        Assert.False(TerminalMarkerSnapshotDiagnostics.TryNormalize("not-json", out var malformed, out var malformedSummary));
        Assert.Equal("[]", malformed);
        Assert.Equal(TerminalMarkerSnapshotDiagnostics.MarkerSnapshotSummary.Empty, malformedSummary);
        Assert.False(TerminalMarkerSnapshotDiagnostics.TryNormalize(new string('x', TerminalMarkerSnapshotDiagnostics.MaxJsonLength + 1), out var unbounded, out _));
        Assert.Equal("[]", unbounded);
    }

    [Fact]
    public void TerminalWebViewHelpersAreFunctionScopedForLaterMessageHandlers()
    {
        var source = LoadTerminalControlSource();
        var helperNames = new[]
        {
            "scanMarkerRows",
            "postScreenStateSnapshot",
            "captureViewportAnchor",
            "restoreViewportAnchor"
        };
        var messageHandlerIndex = source.IndexOf("window.chrome.webview.addEventListener", StringComparison.Ordinal);

        Assert.True(messageHandlerIndex >= 0, "The terminal WebView message handler must remain present.");

        foreach (var helperName in helperNames)
        {
            Assert.Equal(1, CountOccurrences(source, $"var {helperName} = function"));
            Assert.DoesNotContain($"function {helperName}(", source, StringComparison.Ordinal);
            Assert.True(
                source.IndexOf($"var {helperName} = function", StringComparison.Ordinal) < messageHandlerIndex,
                $"{helperName} must be initialized before the later WebView message handler can call it.");
        }

        Assert.Contains("postScreenStateSnapshot('marker-write-after'", source, StringComparison.Ordinal);
        Assert.Contains("COMPLETION_TEST_START", source, StringComparison.Ordinal);
        Assert.Contains("COMPLETION_TEST_END", source, StringComparison.Ordinal);
        Assert.Contains("COMPLETION_TEST\\s+(\\d{1,3})", source, StringComparison.Ordinal);
        Assert.Contains("resize-adjacent-output-after", source, StringComparison.Ordinal);
        Assert.Contains("state.markerRowsJson = JSON.stringify(state.markerRows)", source, StringComparison.Ordinal);
        Assert.Contains("markerRowsJsonProperty", source, StringComparison.Ordinal);
        Assert.Contains("TryNormalize(", source, StringComparison.Ordinal);
        Assert.Contains("numberedSequenceJson", source, StringComparison.Ordinal);
        Assert.Contains("csiCommands", source, StringComparison.Ordinal);
        Assert.Contains("cursorDestinations", source, StringComparison.Ordinal);
        Assert.Contains("Xterm.ResizeCommitReceived", source, StringComparison.Ordinal);
        Assert.Contains("Xterm.AfterTermResize", source, StringComparison.Ordinal);
        Assert.Contains("Xterm.ResizeCommitAckPosted", source, StringComparison.Ordinal);
        Assert.Contains("Xterm.RedrawWriteBegin", source, StringComparison.Ordinal);
        Assert.Contains("Xterm.RedrawWriteCallbackComplete", source, StringComparison.Ordinal);
        Assert.Contains("captureViewportAnchor()", source, StringComparison.Ordinal);
        Assert.Contains("restoreViewportAnchor(viewportAnchor", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAnchorIsCapturedBeforeHostCommitAndMatchedToRequestedGeometry()
    {
        var source = LoadTerminalControlSource();

        Assert.Contains("pendingResizeObserverAnchor = captureViewportAnchor();", source, StringComparison.Ordinal);
        Assert.Contains("requestedResizeAnchor = {", source, StringComparison.Ordinal);
        Assert.Contains("requestedResizeAnchor.cols === commitCols", source, StringComparison.Ordinal);
        Assert.Contains("requestedResizeAnchor.rows === commitRows", source, StringComparison.Ordinal);
        Assert.Contains("requestedResizeAnchor.anchor", source, StringComparison.Ordinal);
        Assert.Contains("before-host-resize-commit", source, StringComparison.Ordinal);
        Assert.Contains("after-host-resize-commit", source, StringComparison.Ordinal);
        Assert.Contains("viewportStart - 96", source, StringComparison.Ordinal);
        Assert.Contains("rows.length < 32", source, StringComparison.Ordinal);
    }

    private static string LoadTerminalControlSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new DirectoryNotFoundException("Could not locate TerminalControl.xaml.cs from the test output directory.");
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
