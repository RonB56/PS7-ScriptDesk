using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class FoldedRegionStateSnapshotTests
{
    [Fact]
    public void FoldedStateMatchesAfterLinesAreInsertedBeforeTheRegion()
    {
        const string original = "function Keep-Me {\n    Write-Host 'one'\n}";
        const string edited = "# inserted before the function\nfunction Keep-Me {\n    Write-Host 'one'\n}";
        var oldRange = new FoldingRange(original.IndexOf('{'), original.Length, "function Keep-Me");
        var newRange = new FoldingRange(edited.IndexOf('{'), edited.Length, "function Keep-Me");

        var state = FoldedRegionStateSnapshot.Capture(
            new TextDocument(original), [(oldRange, true)]);

        Assert.Equal([newRange], state.FindFoldedRanges(new TextDocument(edited), [newRange]));
    }

    [Fact]
    public void FoldedStateSurvivesBodyEditsAndKeepsNestedChoicesIndependent()
    {
        const string original = "function Outer {\n    if ($true) {\n        Write-Host 'one'\n    }\n}";
        const string edited = "function Outer {\n    if ($true) {\n        Write-Host 'changed'\n        Write-Host 'two'\n    }\n}";
        var oldOuter = new FoldingRange(original.IndexOf('{'), original.Length, "function Outer");
        var oldInnerStart = original.IndexOf("if ($true)", StringComparison.Ordinal) + "if ($true) ".Length;
        var oldInner = new FoldingRange(oldInnerStart,
            original.IndexOf("    }", oldInnerStart, StringComparison.Ordinal) + 5, "if branch");
        var newOuter = new FoldingRange(edited.IndexOf('{'), edited.Length, "function Outer");
        var newInnerStart = edited.IndexOf("if ($true)", StringComparison.Ordinal) + "if ($true) ".Length;
        var newInner = new FoldingRange(newInnerStart,
            edited.IndexOf("    }", newInnerStart, StringComparison.Ordinal) + 5, "if branch");

        var state = FoldedRegionStateSnapshot.Capture(
            new TextDocument(original), [(oldOuter, false), (oldInner, true)]);

        Assert.Equal([newInner], state.FindFoldedRanges(new TextDocument(edited), [newOuter, newInner]));
    }

    [Fact]
    public void RemovedOrStructurallyChangedRegionsDoNotRestoreOldState()
    {
        const string original = "function Remove-Me {\n    Write-Host 'one'\n}";
        const string changed = "function Changed-Name {\n    Write-Host 'one'\n}";
        var oldRange = new FoldingRange(original.IndexOf('{'), original.Length, "function Remove-Me");
        var changedRange = new FoldingRange(changed.IndexOf('{'), changed.Length, "function Changed-Name");
        var state = FoldedRegionStateSnapshot.Capture(new TextDocument(original), [(oldRange, true)]);

        Assert.Empty(state.FindFoldedRanges(new TextDocument(string.Empty), []));
        Assert.Empty(state.FindFoldedRanges(new TextDocument(changed), [changedRange]));
    }

    [Fact]
    public void StructurallyEquivalentRegionsRemainIndependentByTheirParentPath()
    {
        const string original = "function First {\n    if ($true) {\n        1\n    }\n}\nfunction Second {\n    if ($true) {\n        2\n    }\n}";
        var outer1 = new FoldingRange(original.IndexOf('{'), original.IndexOf("}\nfunction Second", StringComparison.Ordinal) + 1, "function First");
        var inner1Start = original.IndexOf("if ($true)", StringComparison.Ordinal) + "if ($true) ".Length;
        var inner1 = new FoldingRange(inner1Start, original.IndexOf("    }", inner1Start, StringComparison.Ordinal) + 5, "if branch");
        var outer2Start = original.IndexOf("function Second", StringComparison.Ordinal);
        var outer2 = new FoldingRange(original.IndexOf('{', outer2Start), original.Length, "function Second");
        var inner2Start = original.IndexOf("if ($true)", inner1Start, StringComparison.Ordinal) + "if ($true) ".Length;
        var inner2 = new FoldingRange(inner2Start, original.LastIndexOf("    }", StringComparison.Ordinal) + 5, "if branch");

        var state = FoldedRegionStateSnapshot.Capture(
            new TextDocument(original), [(outer1, false), (inner1, true), (outer2, false), (inner2, false)]);

        Assert.Equal([inner1], state.FindFoldedRanges(
            new TextDocument(original), [outer1, inner1, outer2, inner2]));
    }
}
