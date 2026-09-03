namespace PS7ScriptDesk.Tests;

public sealed class CommandPaletteStyleTests
{
    [Fact]
    public void PaletteDefinesExplicitReadableTextForSelectedAndUnselectedRows()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "Editor", "CommandPaletteWindow.cs"));
        Assert.Contains("ItemContainerStyle", source, StringComparison.Ordinal);
        Assert.Contains("TextElement.ForegroundProperty", source, StringComparison.Ordinal);
        Assert.Contains("TextBlock.ForegroundProperty", source, StringComparison.Ordinal);
        Assert.Contains("ListBoxItem.IsSelectedProperty", source, StringComparison.Ordinal);
        Assert.Contains("Theme.Text.Primary", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
