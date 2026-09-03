using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorCommandRegistryTests
{
    private static EditorCommandDefinition Command(string id, string name, params string[] keywords) =>
        new(id, name, "Test", string.Empty, keywords, () => true, () => { });

    [Fact]
    public void RegistryRejectsDuplicateIdsAndSearchesDeterministically()
    {
        Assert.Throws<ArgumentException>(() => new EditorCommandRegistry(new[] { Command("a", "One"), Command("a", "Two") }));
        var registry = new EditorCommandRegistry(new[]
        {
            Command("b", "Duplicate Line Down", "copy"),
            Command("a", "Toggle Line Comment", "comment")
        });

        Assert.Equal("Toggle Line Comment", registry.Search("COMMENT").Single().DisplayName);
        Assert.Equal("Duplicate Line Down", registry.Search("copy").Single().DisplayName);
        Assert.Empty(registry.Search("missing"));
        Assert.Equal(new[] { "Duplicate Line Down", "Toggle Line Comment" }, registry.Search(string.Empty).Select(command => command.DisplayName));
    }

    [Fact]
    public void SearchSortReturnsBothVisibleSortCommands()
    {
        var registry = new EditorCommandRegistry(new[]
        {
            Command("sort.asc", "Sort Lines Ascending", "sort"),
            Command("sort.desc", "Sort Lines Descending", "sort")
        });

        Assert.Equal(
            new[] { "Sort Lines Ascending", "Sort Lines Descending" },
            registry.Search("sort").Select(command => command.DisplayName));
    }
}
