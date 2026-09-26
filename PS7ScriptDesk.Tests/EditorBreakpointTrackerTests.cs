using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class EditorBreakpointTrackerTests
{
    [Fact]
    public void InsertedLinesAboveBreakpointMoveLogicalBreakpointWithStatement()
    {
        var tab = CreateTab("before\ntarget\nafter");
        tab.ToggleBreakpoint(2);
        var document = new TextDocument(tab.Content);
        using var tracker = new EditorBreakpointTracker(document, tab);

        document.Insert(0, "one\ntwo\nthree\n");

        Assert.Equal(new[] { 5 }, tab.BreakpointLineNumbers);
        Assert.Equal(new[] { 5 }, tracker.CurrentLineNumbers);
    }

    [Fact]
    public void DeletedLinesAboveBreakpointMoveLogicalBreakpointWithStatement()
    {
        var tab = CreateTab("one\ntwo\ntarget\nafter");
        tab.ToggleBreakpoint(3);
        var document = new TextDocument(tab.Content);
        using var tracker = new EditorBreakpointTracker(document, tab);

        document.Remove(0, "one\ntwo\n".Length);

        Assert.Equal(new[] { 1 }, tab.BreakpointLineNumbers);
        Assert.Equal(new[] { 1 }, tracker.CurrentLineNumbers);
    }

    [Fact]
    public void MultipleBreakpointsAndDisabledStateTrackIndependently()
    {
        var tab = CreateTab("a\nb\nc\nd\ne");
        tab.ToggleBreakpoint(2);
        tab.ToggleBreakpoint(4);
        tab.SetBreakpointEnabled(4, false);
        var document = new TextDocument(tab.Content);
        using var tracker = new EditorBreakpointTracker(document, tab);

        document.Insert(0, "inserted\n");

        Assert.Equal(new[] { 3, 5 }, tab.BreakpointLineNumbers);
        Assert.True(tab.IsBreakpointEnabled(3));
        Assert.False(tab.IsBreakpointEnabled(5));
    }

    [Fact]
    public void InsertingAtBreakpointLineStartKeepsAnchorWithOriginalStatement()
    {
        var tab = CreateTab("target\nafter");
        tab.ToggleBreakpoint(1);
        var document = new TextDocument(tab.Content);
        using var tracker = new EditorBreakpointTracker(document, tab);

        document.Insert(0, "prefix ");

        Assert.Equal(new[] { 1 }, tab.BreakpointLineNumbers);
        Assert.Equal(new[] { 1 }, tracker.CurrentLineNumbers);
    }

    private static EditorTabViewModel CreateTab(string content)
        => new("unsaved.ps1", content);
}
