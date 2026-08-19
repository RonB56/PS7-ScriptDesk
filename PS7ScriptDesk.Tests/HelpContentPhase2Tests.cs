using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Tests;

public sealed class HelpContentPhase2Tests
{
    private static readonly string[] Phase2TopicKeys =
    {
        "App.AdministratorMode",
        "Console.FocusAndRecovery",
        "Debug.OutputAndSessions",
        "Files.ExternalChanges",
        "Help.Shortcuts",
        "Help.GettingStarted"
    };

    [Fact]
    public void Phase2Topics_ExistHaveCategoriesAndAreBrowsable()
    {
        var categoryKeys = HelpTopicCatalog.GetCategories()
            .Select(category => category.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var browsableTopicKeys = HelpTopicCatalog.GetCategories()
            .SelectMany(category => HelpTopicCatalog.GetTopicsForCategory(category.Key))
            .Select(topic => topic.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in Phase2TopicKeys)
        {
            var topic = HelpTopicCatalog.Get(key);
            Assert.Contains(topic.CategoryKey, categoryKeys);
            Assert.Contains(topic.Key, browsableTopicKeys);
            Assert.Empty(HelpTopicCatalog.ValidateKeys(topic.RelatedTopicKeys, topic.Key));
        }
    }

    [Theory]
    [InlineData("admin", "App.AdministratorMode")]
    [InlineData("stuck", "Console.FocusAndRecovery")]
    [InlineData("conflict", "Files.ExternalChanges")]
    [InlineData("debug output", "Debug.OutputAndSessions")]
    [InlineData("Ctrl+Shift+F6", "Help.Shortcuts")]
    public void Phase2Topics_AreSearchableByUsefulTerms(string searchText, string expectedKey)
    {
        Assert.Contains(HelpTopicCatalog.Search(searchText), topic => topic.Key == expectedKey);
    }

    [Fact]
    public void ConsoleOutput_SeparatesConsoleActivityAndDebugOutput()
    {
        var text = GetTopicText("Console.Output");

        Assert.DoesNotContain("debugger-related terminal output", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Activity", text, StringComparison.Ordinal);
        Assert.Contains("Debug Output", text, StringComparison.Ordinal);
        Assert.Contains("separate", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdministratorMode_ExplainsWindowsDragDropRestrictionAndFileOpenAlternative()
    {
        var text = GetTopicText("App.AdministratorMode");

        Assert.Contains("Windows", text, StringComparison.Ordinal);
        Assert.Contains("non-elevated File Explorer", text, StringComparison.Ordinal);
        Assert.Contains("File > Open", text, StringComparison.Ordinal);
        Assert.Contains("mapped-drive", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortcutReference_ContainsVerifiedCoreShortcuts()
    {
        var text = GetTopicText("Help.Shortcuts");
        var expectedShortcuts = new[]
        {
            "Ctrl+N", "Ctrl+O", "Ctrl+Shift+O", "Ctrl+S", "Ctrl+Shift+S", "Ctrl+W", "Ctrl+Shift+W",
            "Ctrl+F", "Ctrl+H", "Ctrl+G", "Ctrl+F5", "F8", "F5", "F9", "F10", "F11", "Shift+F5",
            "Shift+F11", "F1", "Ctrl+Space", "Ctrl+Tab", "Ctrl+Shift+Tab", "F3", "Shift+F3", "Ctrl+/",
            "Alt+Up", "Alt+Down", "Ctrl+mouse wheel", "Ctrl+Shift+F6"
        };

        Assert.All(expectedShortcuts, shortcut => Assert.Contains(shortcut, text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("terminal", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paused debug session", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RevisedAndNewTopics_HaveResolvableRelatedTopics()
    {
        var keys = Phase2TopicKeys.Concat(new[]
        {
            "Console.Output",
            "Editor.DragDrop",
            "Help.Troubleshooting"
        });

        foreach (var key in keys)
        {
            var topic = HelpTopicCatalog.Get(key);
            Assert.Empty(HelpTopicCatalog.ValidateKeys(topic.RelatedTopicKeys, topic.Key));
        }
    }

    private static string GetTopicText(string key)
    {
        var topic = HelpTopicCatalog.Get(key);
        return string.Join(
            Environment.NewLine,
            new[] { topic.Title, topic.QuickSummary, topic.WhenToUse, topic.LimitationOrGotcha }
                .Concat(topic.Keywords)
                .Concat(topic.Sections.SelectMany(section => section.Items)));
    }
}
