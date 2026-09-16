using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;
using System.Reflection;

namespace PS7ScriptDesk.Tests;

public sealed class GitDirtyDocumentRuntimeSafetyTests
{
    [Fact]
    public async Task WorkspaceSwitchCommandBlocksDirtyGeneralTextDocumentBeforeGit()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "ScriptDesk-GitSafety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        var filePath = Path.Combine(repositoryRoot, "README.txt");
        await File.WriteAllTextAsync(filePath, "master\n");

        var git = new RecordingGitService();
        var prompt = new RecordingPromptService();
        var coordinator = new GitWorkspaceCoordinator(git);
        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, repositoryRoot, repositoryRoot, "master", false, "master-sha", false, false, false, null, null),
            repositoryRoot,
            DateTimeOffset.UtcNow));
        coordinator.PublishBranchState(new GitBranchState(new[]
        {
            new GitBranch("master", "refs/heads/master", true, false, null, 0, 0, "master-sha"),
            new GitBranch("dirty-test", "refs/heads/dirty-test", false, false, null, 0, 0, "dirty-sha")
        }, "master", false, "master-sha"));

        using var host = new MainWindowViewModel(
            new FakeWorkspaceService(), new FakeRuntimeService(), new FileDocumentService(),
            new FakeWorkspaceFolderService(), prompt, new FakeLiveConsoleService(), new FakeExeExportService(),
            gitService: git, gitWorkspaceCoordinator: coordinator);
        var tab = new EditorTabViewModel("README.txt", "master\n", filePath);
        tab.MarkSaved();
        tab.Content += "UNSAVED SCRIPT DESK TEST\n";
        host.OpenTabs.Add(tab);
        using var workspace = new GitWorkspaceViewModel(coordinator, host);
        var target = workspace.Branches!.Branches.Single(branch => branch.Name == "dirty-test");
        workspace.Branches.SelectedBranch = target;

        workspace.Branches.SwitchCommand.Execute(target);
        await WaitForAsync(() => prompt.WarningCount == 1);

        Assert.Equal(0, git.SwitchCount);
        Assert.Equal("master", coordinator.CurrentState!.Repository.CurrentBranch);
        Assert.True(tab.IsDirty);
        Assert.Equal("master\nUNSAVED SCRIPT DESK TEST\n", tab.Content);
        Assert.Equal("Branch switch blocked", host.StatusText);
        Assert.Equal("Cannot switch branch", prompt.LastTitle);
        Assert.Contains("unsaved changes", prompt.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(workspace.Branches.CanSwitchSelected);
        Assert.True(workspace.Branches.SwitchCommand.CanExecute(target));
    }

    [Fact]
    public async Task WorkspaceSwitchCommandTreatsDirtyPowerShellDocumentTheSameWay()
    {
        var git = new RecordingGitService();
        var prompt = new RecordingPromptService();
        var coordinator = new GitWorkspaceCoordinator(git);
        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "master", false, "sha", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow));
        coordinator.PublishBranchState(new GitBranchState(new[]
        {
            new GitBranch("master", "refs/heads/master", true, false, null, 0, 0, "sha"),
            new GitBranch("feature", "refs/heads/feature", false, false, null, 0, 0, "feature-sha")
        }, "master", false, "sha"));
        using var host = new MainWindowViewModel(
            new FakeWorkspaceService(), new FakeRuntimeService(), new FileDocumentService(),
            new FakeWorkspaceFolderService(), prompt, new FakeLiveConsoleService(), new FakeExeExportService(),
            gitService: git, gitWorkspaceCoordinator: coordinator);
        var tab = new EditorTabViewModel("script.ps1", "Write-Host master\n", "C:\\repo\\script.ps1");
        tab.MarkSaved();
        tab.Content += "Write-Host unsaved\n";
        host.OpenTabs.Add(tab);
        using var workspace = new GitWorkspaceViewModel(coordinator, host);

        workspace.Branches!.SwitchCommand.Execute(workspace.Branches.Branches.Single(branch => branch.Name == "feature"));
        await WaitForAsync(() => prompt.WarningCount == 1);

        Assert.Equal(0, git.SwitchCount);
        Assert.True(tab.IsDirty);
        Assert.Contains("unsaved", tab.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceSwitchCommandAllowsCleanGeneralTextDocument()
    {
        var git = new RecordingGitService
        {
            RepositoryState = new GitRepositoryState(
                new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
                new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "master", false, "sha", false, false, false, null, null),
                "C:\\repo", DateTimeOffset.UtcNow),
            BranchState = new GitBranchState(new[]
            {
                new GitBranch("master", "refs/heads/master", true, false, null, 0, 0, "sha"),
                new GitBranch("feature", "refs/heads/feature", false, false, null, 0, 0, "feature-sha")
            }, "feature", false, "feature-sha")
        };
        var coordinator = new GitWorkspaceCoordinator(git);
        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "master", false, "sha", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow));
        coordinator.PublishBranchState(new GitBranchState(new[]
        {
            new GitBranch("master", "refs/heads/master", true, false, null, 0, 0, "sha"),
            new GitBranch("feature", "refs/heads/feature", false, false, null, 0, 0, "feature-sha")
        }, "master", false, "sha"));
        using var host = new MainWindowViewModel(
            new FakeWorkspaceService(), new FakeRuntimeService(), new FileDocumentService(),
            new FakeWorkspaceFolderService(), new RecordingPromptService(), new FakeLiveConsoleService(), new FakeExeExportService(),
            gitService: git, gitWorkspaceCoordinator: coordinator);
        typeof(MainWindowViewModel).GetField("_gitRepositoryState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, git.RepositoryState);
        var tab = new EditorTabViewModel("README.txt", "master\n", "C:\\repo\\README.txt");
        tab.MarkSaved();
        host.OpenTabs.Add(tab);
        using var workspace = new GitWorkspaceViewModel(coordinator, host);

        await host.SwitchBranchAsync("feature");

        Assert.Equal(1, git.SwitchCount);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task ConflictStageBlocksDirtyOpenDocumentBeforeGit()
    {
        var git = new RecordingGitService();
        var prompt = new RecordingPromptService();
        var coordinator = new GitWorkspaceCoordinator(git);
        var conflict = new GitFileStatus("C:\\repo\\ConflictTest.txt", "ConflictTest.txt", null, 'U', 'U', true, false, true, false, false, false, true, false);
        var state = new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "master", false, "sha", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow) { Changes = new[] { conflict } };
        git.RepositoryState = state;
        coordinator.PublishState(state);
        using var host = new MainWindowViewModel(
            new FakeWorkspaceService(), new FakeRuntimeService(), new FileDocumentService(),
            new FakeWorkspaceFolderService(), prompt, new FakeLiveConsoleService(), new FakeExeExportService(),
            gitService: git, gitWorkspaceCoordinator: coordinator);
        typeof(MainWindowViewModel).GetField("_gitRepositoryState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, state);
        var tab = new EditorTabViewModel("ConflictTest.txt", "resolved\n", conflict.FullPath);
        tab.MarkSaved();
        tab.Content += "unsaved\n";
        host.OpenTabs.Add(tab);

        await host.StageFileAsync(conflict);

        Assert.Equal(0, git.StageCount);
        Assert.Equal("Stage blocked", host.StatusText);
        Assert.Equal("Cannot stage", prompt.LastTitle);
        Assert.Contains("Save the file", prompt.LastMessage, StringComparison.Ordinal);
        Assert.True(tab.IsDirty);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 50 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class RecordingPromptService : IUserPromptService
    {
        public int WarningCount { get; private set; }
        public string LastTitle { get; private set; } = string.Empty;
        public string LastMessage { get; private set; } = string.Empty;
        public UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName) => UnsavedChangesDecision.Cancel;
        public ExternalFileConflictDecision ShowExternalFileConflictPrompt(string filePath, string conflictReason) => ExternalFileConflictDecision.Cancel;
        public DocumentRecoveryAction ShowDocumentRecoveryPrompt(DocumentRecoveryCandidate recoveryCandidate) => DocumentRecoveryAction.KeepForLater;
        public string? ShowSaveFileDialog(string suggestedFileName) => null;
        public string? ShowSaveExecutableDialog(string suggestedFileName) => null;
        public string? ShowOpenFolderDialog() => null;
        public string? ShowOpenPowerShellExecutableDialog() => null;
        public void ShowWarningMessage(string title, string message) { WarningCount++; LastTitle = title; LastMessage = message; }
        public bool ShowConfirmation(string title, string message, string primaryText, string secondaryText, bool destructive = false) => false;
    }

    private sealed class RecordingGitService : IGitService
    {
        public int SwitchCount { get; private set; }
        public int StageCount { get; private set; }
        public GitEnvironmentInfo Environment { get; init; } = new GitEnvironmentInfo(true, "git.exe", "2.x", null, null);
        public GitRepositoryState? RepositoryState { get; set; }
        public GitBranchState BranchState { get; init; } = new GitBranchState(Array.Empty<GitBranch>(), null, false, null);
        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default) => Task.FromResult(Environment);
        public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default) => NotSupported<GitRepositoryInfo>();
        public Task<GitRepositoryState> RefreshRepositoryStateAsync(string? workspacePath, CancellationToken cancellationToken = default) => Task.FromResult(RepositoryState ?? throw new InvalidOperationException("RepositoryState not configured."));
        public Task<GitCommandResult> StageFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default) { StageCount++; return Task.FromResult(new GitCommandResult(true, 0, string.Empty, string.Empty, TimeSpan.Zero, false, false, null)); }
        public Task<GitCommandResult> StageAllAsync(string repositoryRoot, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> UnstageFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> UnstageAllAsync(string repositoryRoot, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> DiscardFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitDiff> GetDiffAsync(string repositoryRoot, string relativePath, GitDiffScope scope, bool isUntracked = false, bool allowLarge = false, CancellationToken cancellationToken = default) => NotSupported<GitDiff>();
        public Task<GitBranchState> GetBranchesAsync(string repositoryRoot, CancellationToken cancellationToken = default) => Task.FromResult(BranchState);
        public Task<GitCommandResult> CreateBranchAsync(string repositoryRoot, string branchName, bool switchTo, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> SwitchBranchAsync(string repositoryRoot, string branchName, CancellationToken cancellationToken = default) { SwitchCount++; return Task.FromResult(new GitCommandResult(true, 0, string.Empty, string.Empty, TimeSpan.Zero, false, false, null)); }
        public Task<GitCommandResult> RenameBranchAsync(string repositoryRoot, string oldName, string newName, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> DeleteBranchAsync(string repositoryRoot, string branchName, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitRemoteState> GetRemotesAsync(string repositoryRoot, CancellationToken cancellationToken = default) => NotSupported<GitRemoteState>();
        public Task<GitCommandResult> FetchAsync(string repositoryRoot, string? remote = null, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> PullAsync(string repositoryRoot, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> PushAsync(string repositoryRoot, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> PublishBranchAsync(string repositoryRoot, string remote, string branchName, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitHistoryPage> GetHistoryPageAsync(string repositoryRoot, GitHistoryScope scope, int skip, int pageSize, string? search = null, CancellationToken cancellationToken = default) => NotSupported<GitHistoryPage>();
        public Task<GitCommitDetails> GetCommitDetailsAsync(string repositoryRoot, string commitHash, CancellationToken cancellationToken = default) => NotSupported<GitCommitDetails>();
        public Task<GitDiff> GetCommitDiffAsync(string repositoryRoot, string commitHash, string relativePath, string? parentHash = null, CancellationToken cancellationToken = default) => NotSupported<GitDiff>();
        public Task<GitCommandResult> CloneAsync(string source, string destination, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        public Task<GitCommandResult> InitializeAsync(string folderPath, CancellationToken cancellationToken = default) => NotSupported<GitCommandResult>();
        private static Task<T> NotSupported<T>() => Task.FromException<T>(new NotSupportedException());
    }
}
