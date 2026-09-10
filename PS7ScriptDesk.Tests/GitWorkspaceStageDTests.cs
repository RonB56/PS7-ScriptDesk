using System.Runtime.CompilerServices;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitWorkspaceStageDTests
{
    [Fact]
    public void DedicatedWorkspaceOwnsRepositorySetupAndSyncEntryPoints()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var workspace = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitWorkspaceViewModel.cs");

        Assert.Contains("OpenRepositoryCommand", shell, StringComparison.Ordinal);
        Assert.Contains("CloneRepositoryCommand", shell, StringComparison.Ordinal);
        Assert.Contains("InitializeRepositoryCommand", shell, StringComparison.Ordinal);
        Assert.Contains("GitDiagnosticsCommand", shell, StringComparison.Ordinal);
        Assert.Contains("Remotes.FetchCommand", shell, StringComparison.Ordinal);
        Assert.Contains("Remotes.PullCommand", shell, StringComparison.Ordinal);
        Assert.Contains("Remotes.PushCommand", shell, StringComparison.Ordinal);
        Assert.Contains("Remotes.SyncCommand", shell, StringComparison.Ordinal);
        Assert.Contains("legacyActions?.OpenRepositoryFolderCommand", workspace, StringComparison.Ordinal);
        Assert.Contains("legacyActions?.CloneRepositoryCommand", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowRetainsOnlyConciseGitEntryPoints()
    {
        var mainXaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Open Git _Workspace", mainXaml, StringComparison.Ordinal);
        Assert.Contains("GitStatusText", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceControlBottomPane", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomSourceControlToolTab", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OpenGitWorkspace(\"CommandPalette\")", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceKeepsOneWindowActivationPathAndDoesNotLaunchGitDirectly()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml") +
                    TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml.cs");

        Assert.Contains("_gitWorkspaceWindow is { IsLoaded: true } existing", main, StringComparison.Ordinal);
        Assert.Contains("existing.Activate()", main, StringComparison.Ordinal);
        Assert.Contains("window.Closed += GitWorkspaceWindow_Closed", main, StringComparison.Ordinal);
        Assert.Contains("viewModel.Dispose()", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("git.exe", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkspaceRefreshUsesSharedCoordinatorAndPreservesRepositoryNeutralState()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));

        using var workspace = new GitWorkspaceViewModel(coordinator, host);

        Assert.Equal("No Git repository open", workspace.RepositoryText);
        Assert.Contains("RefreshAsync", TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitWorkspaceViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("RequestRefresh", TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitWorkspaceViewModel.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceChangesLayoutUsesResizableAdaptivePanesAndFullWidthCommitArea()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");

        Assert.Contains("<ColumnDefinition Width=\"1.1*\" MinWidth=\"320\" />", shell, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"2*\" MinWidth=\"400\" />", shell, StringComparison.Ordinal);
        Assert.Contains("ResizeDirection=\"Columns\"", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Resize Git changes and diff panes\"", shell, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"*\" MinHeight=\"120\" />", shell, StringComparison.Ordinal);
        Assert.Contains("<Border Grid.Row=\"2\"", shell, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\" Style=\"{StaticResource IdeSecondaryButtonStyle}\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"320\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"280\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceHistoryLayoutUsesResizableColumnsAndCompactEmptyStates()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");

        Assert.Contains("Resize Git history list and details", shell, StringComparison.Ordinal);
        Assert.Contains("Resize Git history details and diff", shell, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"1.1*\" MinWidth=\"220\" />", shell, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"1.2*\" MinWidth=\"240\" />", shell, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"2*\" MinWidth=\"400\" />", shell, StringComparison.Ordinal);
        Assert.Contains("No working-tree changes.", shell, StringComparison.Ordinal);
        Assert.Contains("Select a changed file to review its diff.", shell, StringComparison.Ordinal);
        Assert.Contains("No commits match the current filter.", shell, StringComparison.Ordinal);
        Assert.Contains("Select a history file to review its diff.", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\" VerticalScrollBarVisibility=\"Disabled\"", shell, StringComparison.Ordinal);
    }
}
