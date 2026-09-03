using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class CommandPalettePresentationTests
{
    [Fact]
    public void ItemExposesSeparateCommandCategoryAndShortcutPresentationFields()
    {
        var command = new EditorCommandDefinition(
            "sort.asc",
            "Sort Lines Ascending",
            "Transform",
            "Ctrl+Alt+A",
            new[] { "sort" },
            () => true,
            () => { });

        var item = new CommandPaletteItem(command);

        Assert.Equal("Sort Lines Ascending", item.DisplayName);
        Assert.Equal("Transform", item.Category);
        Assert.Equal("Ctrl+Alt+A", item.ShortcutText);
    }

    [Fact]
    public void ResultViewportHeightCapsAtTenRowsAndIsZeroForNoResults()
    {
        Assert.Equal(0, GetResultsViewportHeight(0));
        Assert.Equal(60, GetResultsViewportHeight(2));
        Assert.Equal(300, GetResultsViewportHeight(10));
        Assert.Equal(300, GetResultsViewportHeight(100));
    }

    private static double GetResultsViewportHeight(int count) =>
        Math.Min(Math.Max(count, 0), CommandPaletteWindow.MaxVisibleResults) * CommandPaletteWindow.ResultRowHeight;
}
