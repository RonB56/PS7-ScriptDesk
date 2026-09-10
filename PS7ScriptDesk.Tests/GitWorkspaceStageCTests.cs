using System.Runtime.CompilerServices;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitWorkspaceStageCTests
{
    [Fact]
    public void WorkspaceConstructsFocusedHistoryBranchAndRemoteChildren()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));

        using var workspace = new GitWorkspaceViewModel(coordinator, host);

        Assert.NotNull(workspace.Changes);
        Assert.NotNull(workspace.History);
        Assert.NotNull(workspace.Branches);
        Assert.NotNull(workspace.Remotes);
        Assert.Same(coordinator.GitService, coordinator.GitService);
    }

    [Fact]
    public void BranchAndRemoteSnapshotsAreSharedThroughCoordinator()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        using var workspace = new GitWorkspaceViewModel(coordinator, host);
        var branchState = new GitBranchState(new[] { new GitBranch("main", "refs/heads/main", true, false, "origin/main", 2, 1, "abc") }, "main", false, "abc");
        var remoteState = new GitRemoteState(new[] { new GitRemote("origin", "https://example.invalid/fetch", "https://example.invalid/push") });

        coordinator.PublishBranchState(branchState);
        coordinator.PublishRemoteState(remoteState);

        Assert.Equal("main", workspace.Branches!.CurrentText.Replace("Current: ", string.Empty, StringComparison.Ordinal));
        Assert.Single(workspace.Branches.Branches);
        Assert.Equal("Remote: origin", workspace.Remotes!.SummaryText);
        Assert.Single(workspace.Remotes.Remotes);
    }

    [Fact]
    public void DedicatedHistoryShellUsesPagingVirtualizationAndNoEditorHistoryRoute()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml") + TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml.cs");
        var history = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitHistoryViewModel.cs");

        Assert.Contains("LoadMoreCommand", shell, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing", shell, StringComparison.Ordinal);
        Assert.Contains("GetHistoryPageAsync", history, StringComparison.Ordinal);
        Assert.Contains("GetCommitDiffAsync", history, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenHistoryAsync", history, StringComparison.Ordinal);
        Assert.DoesNotContain("git.exe", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DedicatedGitShellKeepsLegacySourceControlAndForbidsForceOperations()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var sourceControl = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlToolWindow.xaml");
        var history = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitHistoryViewModel.cs");

        Assert.Contains("Source Control", sourceControl, StringComparison.Ordinal);
        Assert.Contains("SyncCommand", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("force", shell + history, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stash", shell + history, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rebase", shell + history, StringComparison.OrdinalIgnoreCase);
    }
}
