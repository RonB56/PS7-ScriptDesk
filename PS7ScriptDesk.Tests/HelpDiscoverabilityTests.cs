using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Tests;

public sealed class HelpDiscoverabilityTests
{
    [Fact]
    public void EveryCatalogTopic_HasOneDiscoverableCategory()
    {
        var categories = HelpTopicCatalog.GetCategories();
        var topics = HelpTopicCatalog.GetAllTopics();
        var categoryKeys = categories.Select(category => category.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categorizedTopicKeys = categories
            .SelectMany(category => HelpTopicCatalog.GetTopicsForCategory(category.Key))
            .Select(topic => topic.Key)
            .ToArray();

        Assert.NotEmpty(categories);
        Assert.All(topics, topic => Assert.Contains(topic.CategoryKey, categoryKeys));
        Assert.Equal(topics.Count, categorizedTopicKeys.Length);
        Assert.Equal(topics.Count, categorizedTopicKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CatalogSearch_FindsTopicsAcrossTitlesKeysAndDetails()
    {
        Assert.Contains(HelpTopicCatalog.Search("terminal"), topic => topic.Key == "Console.Area");
        Assert.Contains(HelpTopicCatalog.Search("RunSelection"), topic => topic.Key == "Command.RunSelection");
        Assert.Contains(HelpTopicCatalog.Search("temporary snapshot"), topic => topic.Key == "Command.RunScript");
    }

    [Fact]
    public void CatalogSearch_ReturnsNoResultsForAnUnknownTerm()
    {
        Assert.Empty(HelpTopicCatalog.Search("not-a-real-help-topic"));
    }

    [Fact]
    public void ExistingRelatedTopicsAndContextKeys_RemainResolvable()
    {
        var topics = HelpTopicCatalog.GetAllTopics();
        var topicKeys = topics.Select(topic => topic.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(topics.Count, topicKeys.Count);
        Assert.All(topics, topic => Assert.Empty(HelpTopicCatalog.ValidateKeys(topic.RelatedTopicKeys, topic.Key)));
        Assert.True(HelpTopicCatalog.TryGet("App.Settings", out _));
        Assert.True(HelpTopicCatalog.TryGet("Editor.DragDrop", out _));
        Assert.True(HelpTopicCatalog.TryGet("Help.Troubleshooting", out _));
        Assert.True(HelpTopicCatalog.TryGet("Help.Packaging", out _));
    }

    [Fact]
    public void WorkspaceSummary_OffersRelevantWorkspaceNavigation()
    {
        var topic = HelpTopicCatalog.Get("Explorer.WorkspaceSummary");

        Assert.Equal(
            new[] { "Explorer.WorkspaceTree", "Explorer.WorkspaceFilter", "Command.OpenFolder" },
            topic.RelatedTopicKeys);
        Assert.Empty(HelpTopicCatalog.ValidateKeys(topic.RelatedTopicKeys, topic.Key));
    }
}
