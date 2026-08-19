using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Debug;
using PS7ScriptDesk.Shell.Dialogs;
using PS7ScriptDesk.Shell.Help;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class AuxiliaryDialogHelpIntegrationTests
{
    [Fact]
    public void AuxiliaryWindows_UseResolvableContextHelpTopics()
    {
        RunOnStaThread(() =>
        {
            var resolver = new RuntimeResolverWindow(new RuntimeService());
            var conflict = new ExternalFileConflictDialog("C:\\Temp\\sample.ps1", "The file changed outside PS7 ScriptDesk.");
            var update = new StoreUpdateWindow(new StoreUpdateService(), new StoreUpdateCheckResult(), isMandatory: false);
            var debugPane = new DebugPaneWindow();

            Assert.Equal("Runtime.Resolver", ContextHelp.GetKey(resolver));
            Assert.Equal("Files.ExternalChanges", ContextHelp.GetKey(conflict));
            Assert.Equal("App.StoreUpdate", ContextHelp.GetKey(update));
            Assert.Equal("Debug.Area", ContextHelp.GetKey(debugPane));

            Assert.Empty(ContextHelp.ValidateWindowTopics(resolver));
            Assert.Empty(ContextHelp.ValidateWindowTopics(conflict));
            Assert.Empty(ContextHelp.ValidateWindowTopics(update));
            Assert.Empty(ContextHelp.ValidateWindowTopics(debugPane));
        });
    }

    [Fact]
    public void NewAuxiliaryDialogTopics_AreCategorizedSearchableAndRelated()
    {
        var resolver = HelpTopicCatalog.Get("Runtime.Resolver");
        var update = HelpTopicCatalog.Get("App.StoreUpdate");
        var categoryKeys = HelpTopicCatalog.GetCategories().Select(category => category.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var browsableKeys = HelpTopicCatalog.GetCategories()
            .SelectMany(category => HelpTopicCatalog.GetTopicsForCategory(category.Key))
            .Select(topic => topic.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(resolver.CategoryKey, categoryKeys);
        Assert.Contains(update.CategoryKey, categoryKeys);
        Assert.Contains(resolver.Key, browsableKeys);
        Assert.Contains(update.Key, browsableKeys);
        Assert.Empty(HelpTopicCatalog.ValidateKeys(resolver.RelatedTopicKeys, resolver.Key));
        Assert.Empty(HelpTopicCatalog.ValidateKeys(update.RelatedTopicKeys, update.Key));
        Assert.Contains(HelpTopicCatalog.Search("resolver"), topic => topic.Key == resolver.Key);
        Assert.Contains(HelpTopicCatalog.Search("Microsoft Store"), topic => topic.Key == update.Key);
    }

    [Fact]
    public void ExternalConflictAndDebugPane_ReuseTheIntendedExistingTopics()
    {
        Assert.True(HelpTopicCatalog.TryGet("Files.ExternalChanges", out _));
        Assert.True(HelpTopicCatalog.TryGet("Debug.Area", out _));
        Assert.True(HelpTopicCatalog.TryGet("Debug.Variables", out _));
        Assert.True(HelpTopicCatalog.TryGet("Debug.CallStack", out _));
        Assert.True(HelpTopicCatalog.TryGet("Debug.Breakpoints", out _));
        Assert.True(HelpTopicCatalog.TryGet("Debug.RemoveBreakpoint", out _));
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
