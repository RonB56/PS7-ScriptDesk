using System.Diagnostics;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class FoldRegionSelectorTests
{
    private static readonly Func<Region, int> Start = static region => region.Start;
    private static readonly Func<Region, int> End = static region => region.End;
    private static readonly Func<Region, int> Header = static region => region.HeaderLine;

    [Fact]
    public void DeclarationLineTakesPriorityOverContainingParent()
    {
        var function = new Region("function", 10, 100, 1);
        var conditional = new Region("if", 30, 70, 3);

        var selected = Select(new[] { function, conditional }, caretOffset: 12, caretLine: 3);

        Assert.Same(conditional, selected);
    }

    [Fact]
    public void NestedBodySelectsInnermostContainingRegion()
    {
        var regions = new[]
        {
            new Region("function", 10, 120, 1),
            new Region("foreach", 30, 100, 3),
            new Region("if", 50, 80, 5)
        };

        Assert.Equal("if", Select(regions, caretOffset: 65, caretLine: 6)?.Name);
        Assert.Equal("foreach", Select(regions, caretOffset: 40, caretLine: 4)?.Name);
    }

    [Fact]
    public void HeaderPrioritySelectsHeaderEvenWhenCaretOffsetPrecedesFoldStart()
    {
        var region = new Region("function", 20, 90, 2);

        Assert.Same(region, Select(new[] { region }, caretOffset: 14, caretLine: 2));
    }

    [Fact]
    public void HeaderMatchDoesNotSelectNestedFoldWhoseStartIsAfterCaret()
    {
        var function = new Region("function", 10, 120, 1);
        var nestedIf = new Region("if", 50, 80, 1);

        Assert.Same(function, Select(new[] { function, nestedIf }, caretOffset: 20, caretLine: 1));
    }

    [Fact]
    public void NoApplicableRegionIsSafeNoOp()
    {
        var regions = new[] { new Region("function", 20, 90, 2) };

        Assert.Null(Select(regions, caretOffset: 100, caretLine: 9));
    }

    [Fact]
    public void RelocatedCaretKeepsSelectingSameFoldAndCollapsedParentWinsAtItsStart()
    {
        var parent = new Region("function", 10, 100, 1);
        var child = new Region("if", 30, 70, 3);
        var regions = new[] { parent, child };

        var collapsed = Select(regions, caretOffset: 45, caretLine: 4);
        Assert.Same(child, collapsed);
        // AvalonEdit relocates the caret to the opening brace, whose line may differ
        // from the associated declaration/header line.
        Assert.Same(child, Select(regions, caretOffset: child.Start, caretLine: 4));
        Assert.Same(parent, Select(regions, caretOffset: parent.Start, caretLine: parent.HeaderLine));
    }

    [Fact]
    public void CaretExactlyAtClosingBoundarySelectsTheRegionThatEndsThere()
    {
        var function = new Region("function", 10, 120, 1);
        var nestedIf = new Region("if", 30, 70, 3);

        Assert.Same(nestedIf, Select(new[] { function, nestedIf }, caretOffset: nestedIf.End, caretLine: 8));
    }

    [Fact]
    public void RegionDirectiveBoundariesResolveToTheirOwnRegion()
    {
        var outer = new Region("#region outer", 0, 100, 1);
        var inner = new Region("#region inner", 20, 60, 3);
        var regions = new[] { outer, inner };

        Assert.Same(inner, Select(regions, caretOffset: 25, caretLine: 3));
        Assert.Same(inner, Select(regions, caretOffset: inner.End, caretLine: 7));
    }

    [Fact]
    public void LookupLatencyScalesWithFoldCountWithoutParsing()
    {
        foreach (var count in new[] { 10, 1_000, 10_000 })
        {
            var regions = Enumerable.Range(0, count)
                .Select(index => new Region($"r{index}", index * 3, index * 3 + count * 4, index + 1))
                .ToArray();
            var elapsed = new List<double>(101);
            for (var iteration = 0; iteration < 101; iteration++)
            {
                var stopwatch = Stopwatch.StartNew();
                _ = Select(regions, caretOffset: count * 2, caretLine: count + 2);
                stopwatch.Stop();
                elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            elapsed.Sort();
            Console.WriteLine($"ctrl-m-region-count={count}; iterations=101; median-ms={elapsed[50]:F4}; p95-ms={elapsed[95]:F4}; max-ms={elapsed[^1]:F4}");
        }
    }

    [Fact]
    public void ActivationUsesImmediateAsyncFoldingAndCtrlMUsesRegisteredShortcut()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var registrationStart = main.IndexOf("private void RegisterEditor(TextEditor editorTextEditor)", StringComparison.Ordinal);
        var registrationEnd = main.IndexOf("private int IncrementEditorRegistrationVersion", registrationStart, StringComparison.Ordinal);
        var registration = main[registrationStart..registrationEnd];
        Assert.Contains("_editorFoldingController.ScheduleFolding(editorTextEditor, debounceMilliseconds: 0)", registration, StringComparison.Ordinal);
        Assert.Contains("editorTextEditor.PreviewKeyDown += EditorTextEditor_PreviewKeyDown", main, StringComparison.Ordinal);
        Assert.Contains("new(\"editor.toggleFold\"", main, StringComparison.Ordinal);
        Assert.Contains("_editorFoldingController.ToggleFoldAtCaret(editor)", main, StringComparison.Ordinal);
    }

    private static Region? Select(IEnumerable<Region> regions, int caretOffset, int caretLine)
        => FoldRegionSelector.Select(regions, caretOffset, caretLine, Start, End, Header);

    private sealed record Region(string Name, int Start, int End, int HeaderLine);
}
