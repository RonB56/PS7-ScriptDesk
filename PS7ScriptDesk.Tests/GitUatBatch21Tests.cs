using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitUatBatch21Tests
{
    [Fact]
    public void NonRepositorySnapshotClearsPresentationAndDisablesRepositoryCommands()
    {
        using var fixture = CreateFixture();
        PublishValidRepository(fixture.Coordinator);

        Assert.Equal("main", fixture.Workspace.Branches!.SelectedBranchName);
        Assert.True(fixture.Workspace.Remotes!.FetchCommand.CanExecute(null));

        fixture.Coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            GitRepositoryInfo.NotRepository("fatal: not a git repository (or any of the parent directories): .git", "NotRepository"),
            "C:\\ordinary-folder",
            DateTimeOffset.UtcNow));
        fixture.Coordinator.PublishBranchState(null);
        fixture.Coordinator.PublishRemoteState(new GitRemoteState(Array.Empty<GitRemote>(), "fatal: not a git repository (or any of the parent directories): .git"));

        Assert.Equal("No Git repository detected.", fixture.Workspace.StatusText);
        Assert.Empty(fixture.Workspace.Branches.Branches);
        Assert.Null(fixture.Workspace.Branches.SelectedBranch);
        Assert.False(fixture.Workspace.Branches.HasRepository);
        Assert.Empty(fixture.Workspace.Remotes.Remotes);
        Assert.False(fixture.Workspace.Remotes.HasRepository);
        Assert.Equal("No Git repository detected.", fixture.Workspace.Remotes.SummaryText);
        Assert.False(fixture.Workspace.Remotes.FetchCommand.CanExecute(null));
        Assert.False(fixture.Workspace.Remotes.PullCommand.CanExecute(null));
        Assert.False(fixture.Workspace.Remotes.PushCommand.CanExecute(null));
        Assert.False(fixture.Workspace.Remotes.SyncCommand.CanExecute(null));
        Assert.False(fixture.Workspace.Branches.SwitchCommand.CanExecute(new GitBranch("feature", "refs/heads/feature", false, false, null, null, null, null)));
    }

    [Fact]
    public void ReturningToRepositoryRepopulatesBranchAndRemoteCommandStateWithoutRecreation()
    {
        using var fixture = CreateFixture();
        fixture.Coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            GitRepositoryInfo.NotRepository("No repository", "NotRepository"),
            "C:\\ordinary-folder",
            DateTimeOffset.UtcNow));
        fixture.Coordinator.PublishBranchState(null);
        fixture.Coordinator.PublishRemoteState(new GitRemoteState(Array.Empty<GitRemote>()));
        Assert.False(fixture.Workspace.Remotes!.FetchCommand.CanExecute(null));

        PublishValidRepository(fixture.Coordinator);

        Assert.Equal("main", fixture.Workspace.Branches!.SelectedBranchName);
        Assert.Equal("Remote: origin", fixture.Workspace.Remotes!.SummaryText);
        Assert.True(fixture.Workspace.Remotes.FetchCommand.CanExecute(null));
        Assert.True(fixture.Workspace.Remotes.PullCommand.CanExecute(null));
        Assert.True(fixture.Workspace.Remotes.PushCommand.CanExecute(null));
        Assert.True(fixture.Workspace.Remotes.SyncCommand.CanExecute(null));
        Assert.True(fixture.Workspace.Branches.HasRepository);
    }

    [Fact]
    public void NonRepositoryStateKeepsSetupCommandsAvailable()
    {
        using var fixture = CreateFixture();
        fixture.Coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            GitRepositoryInfo.NotRepository("No repository", "NotRepository"),
            "C:\\ordinary-folder",
            DateTimeOffset.UtcNow));

        Assert.True(fixture.Workspace.OpenRepositoryCommand.CanExecute(null));
        Assert.True(fixture.Workspace.CloneRepositoryCommand.CanExecute(null));
        Assert.True(fixture.Workspace.InitializeRepositoryCommand.CanExecute(null));
        Assert.True(fixture.Workspace.RefreshCommand.CanExecute(null));
        Assert.True(fixture.Workspace.GitDiagnosticsCommand.CanExecute(null));
    }

    private static Fixture CreateFixture()
    {
        var runner = new GitCommandRunner();
        var gitService = new GitService(runner, new GitRepositoryLocator(runner));
        var coordinator = new GitWorkspaceCoordinator(gitService);
        var host = new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            new FakeLiveConsoleService(),
            new FakeExeExportService(),
            gitSetupPromptService: new FakeGitSetupPromptService(),
            gitService: gitService,
            gitWorkspaceCoordinator: coordinator);
        return new Fixture(coordinator, new GitWorkspaceViewModel(coordinator, host));
    }

    private static void PublishValidRepository(GitWorkspaceCoordinator coordinator)
    {
        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo",
            DateTimeOffset.UtcNow));
        coordinator.PublishBranchState(new GitBranchState(new[]
        {
            new GitBranch("main", "refs/heads/main", true, false, "origin/main", 0, 0, "abc"),
            new GitBranch("feature", "refs/heads/feature", false, false, null, 0, 0, "def")
        }, "main", false, "abc"));
        coordinator.PublishRemoteState(new GitRemoteState(new[] { new GitRemote("origin", "https://example.invalid/fetch", "https://example.invalid/push") }));
    }

    private sealed record Fixture(GitWorkspaceCoordinator Coordinator, GitWorkspaceViewModel Workspace) : IDisposable
    {
        public void Dispose() => Workspace.Dispose();
    }

    private sealed class FakeGitSetupPromptService : IGitSetupPromptService
    {
        public GitCloneRequest? ShowCloneDialog() => null;
        public string? ShowInitializeDialog(string folderPath) => null;
    }
}
