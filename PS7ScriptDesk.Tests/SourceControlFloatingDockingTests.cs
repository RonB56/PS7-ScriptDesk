namespace PS7ScriptDesk.Tests;

public sealed class SourceControlFloatingDockingTests
{
    [Fact]
    public void SourceControlUsesAReusableViewAndOneExplicitViewModelInstance()
    {
        var view = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml");
        var viewCode = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml.cs");
        var hostCode = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlToolWindow.xaml.cs");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("SetViewModel(MainWindowViewModel viewModel)", viewCode, StringComparison.Ordinal);
        Assert.Contains("DataContext = viewModel", viewCode, StringComparison.Ordinal);
        Assert.Contains("SourceControlToolWindow(MainWindowViewModel viewModel)", hostCode, StringComparison.Ordinal);
        Assert.Contains("SourceControlContent.SetViewModel(viewModel)", hostCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new SourceControlToolWindow(viewModel)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("DockedSourceControlView.SetViewModel(viewModel)", shell, StringComparison.Ordinal);
        Assert.Contains("new GitWorkspaceWindow", shell, StringComparison.Ordinal);
        Assert.Contains("SourceControlView", view, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredSourceControlHostIsNoLongerOwnedByMainWindow()
    {
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlToolWindow.xaml");
        var hostCode = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlToolWindow.xaml.cs");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Content=\"Dock to ScriptDesk\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DockRequested", hostCode, StringComparison.Ordinal);
        Assert.Contains("Close is an explicit hide action for Source Control", hostCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DockBackRequested", hostCode, StringComparison.Ordinal);
        Assert.Contains("Pop Out", TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("SourceControlToolWindow", shell, StringComparison.Ordinal);
        Assert.Contains("OpenGitWorkspace", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredSourceControlGeometryIsNotRestoredIntoMainWindow()
    {
        var settings = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Domain", "Models", "ApplicationSettings.cs");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("bool? IsSourceControlVisible", settings, StringComparison.Ordinal);
        Assert.Contains("bool? IsSourceControlFloating", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("_loadedSettings.IsSourceControlVisible", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("_loadedSettings.IsSourceControlFloating", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.SourceControlWindowWidth", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreSourceControlWindowBounds", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceControlPresentationDoesNotInstantiateEditorDocuments()
    {
        var viewCode = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml.cs");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.DoesNotContain("new EditorTabViewModel", viewCode, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenTabs.Add", viewCode, StringComparison.Ordinal);
        Assert.Contains("RefreshGitRepositoryAsync", TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("SourceControlBottomPane", shell, StringComparison.Ordinal);
        Assert.Contains("OpenGitWorkspace", shell, StringComparison.Ordinal);
    }
}
