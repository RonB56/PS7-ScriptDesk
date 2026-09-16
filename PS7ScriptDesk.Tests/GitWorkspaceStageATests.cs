using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;
using System.Runtime.CompilerServices;

namespace PS7ScriptDesk.Tests;

public sealed class GitWorkspaceStageATests
{
    [Fact]
    public void CoordinatorPublishesOneSharedRepositorySnapshot()
    {
        var service = new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner()));
        var coordinator = new GitWorkspaceCoordinator(service);
        var viewModel = new GitWorkspaceViewModel(coordinator);
        var state = new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.0", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow);

        coordinator.PublishState(state);

        Assert.Same(service, coordinator.GitService);
        Assert.Same(state, coordinator.CurrentState);
        Assert.Equal("C:\\repo", viewModel.RepositoryText);
        Assert.Equal("Branch: main", viewModel.BranchText);
        viewModel.Dispose();
    }

    [Fact]
    public void EmptyStateIsNeutralAndModesAreRealShellDestinations()
    {
        var service = new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner()));
        var coordinator = new GitWorkspaceCoordinator(service);
        using var viewModel = new GitWorkspaceViewModel(coordinator);

        Assert.Equal("No Git repository open", viewModel.RepositoryText);
        Assert.Equal("Changes", viewModel.ModeText);
        viewModel.ShowHistoryCommand.Execute(null);
        Assert.Equal("History", viewModel.ModeText);
        Assert.Contains("repository", viewModel.ContentText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowShellUsesSharedStateAndKeepsLegacySurface()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml") +
                    TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml.cs");
        var mainWindow = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var sourceControl = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlToolWindow.xaml");

        Assert.Contains("Theme.App.Background", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", shell, StringComparison.Ordinal);
        Assert.Contains("Open Git _Workspace", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Source Control", sourceControl, StringComparison.Ordinal);
        Assert.DoesNotContain("git.exe", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangesViewModelProjectsGroupsAndSelectionWithoutOpeningAnEditor()
    {
        var runner = new GitCommandRunner();
        var service = new GitService(runner, new GitRepositoryLocator(runner));
        var coordinator = new GitWorkspaceCoordinator(service);
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        using var changes = new GitChangesViewModel(coordinator, host);
        var statuses = new[]
        {
            Status("conflict.ps1", 'U', 'U', conflicted: true),
            Status("staged.ps1", 'M', ' '),
            Status("working.ps1", ' ', 'M'),
            Status("untracked.ps1", ' ', '?', tracked: false, untracked: true)
        };
        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.0", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow) { Changes = statuses });

        changes.SelectFile(statuses[0]);

        Assert.Equal(new[] { "CHANGES", "STAGED CHANGES", "UNTRACKED", "CONFLICTS" }, changes.Groups.Select(group => group.Title));
        Assert.Equal("conflict.ps1", changes.SelectedPathText);
        Assert.True(changes.StageCommand.CanExecute(statuses[0]));
        Assert.Contains("manual resolution", changes.DiffStatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static GitFileStatus Status(string relativePath, char index, char working, bool tracked = true, bool untracked = false, bool conflicted = false)
        => new($"C:\\repo\\{relativePath}", relativePath, null, index, working, tracked, untracked, conflicted,
            false, false, false, index == 'M' || working == 'M', false);
}
