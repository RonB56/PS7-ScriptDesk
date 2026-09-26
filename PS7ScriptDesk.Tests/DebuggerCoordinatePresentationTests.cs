using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

public sealed class DebuggerCoordinatePresentationTests
{
    [Fact]
    public void ExactBreakpointKeepsCurrentEditorLinePresentation()
    {
        Assert.Equal(
            "Breakpoint hit at line 12.",
            DebuggerCoordinatePresentation.FormatBreakpointHit(12, DebugSourceMappingStatus.Exact));
        Assert.Equal("Breakpoint hit — line 12", DebuggerCoordinatePresentation.FormatBreakpointStatus(12, DebugSourceMappingStatus.Exact));
    }

    [Fact]
    public void RevisionMismatchLabelsRuntimeCoordinateAndExplainsRecovery()
    {
        var text = DebuggerCoordinatePresentation.FormatBreakpointHit(5, DebugSourceMappingStatus.RevisionMismatch);

        Assert.Contains("execution snapshot line 5", text, StringComparison.Ordinal);
        Assert.Contains("editor has changed", text, StringComparison.Ordinal);
        Assert.Contains("restart debugging", text, StringComparison.Ordinal);
        Assert.Equal("Execution line 5 (older revision)", DebuggerCoordinatePresentation.FormatCallStackLine(5, DebugSourceMappingStatus.RevisionMismatch));
        Assert.Equal("script.ps1 (execution snapshot)", DebuggerCoordinatePresentation.FormatCallStackScript("script.ps1", DebugSourceMappingStatus.RevisionMismatch));
    }

    [Fact]
    public void RevisionMismatchDoesNotPresentAStaleEditorLineOrEnableNavigation()
    {
        var debuggerEvent = new DebuggerEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            1,
            DebuggerEventCategory.Breakpoint,
            DebuggerEventSeverity.Information,
            "Debugger",
            "Breakpoint hit at line 5.",
            "C:\\Temp\\debug-snapshot.ps1",
            5,
            isNavigable: true);

        var presented = debuggerEvent.WithPresentation(
            DebuggerCoordinatePresentation.FormatBreakpointHit(5, DebugSourceMappingStatus.RevisionMismatch),
            isNavigable: false);

        Assert.Contains("execution snapshot line 5", presented.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("line 12", presented.DisplayText, StringComparison.Ordinal);
        Assert.False(presented.IsNavigable);
    }
}
