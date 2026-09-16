using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;
using System.Runtime.CompilerServices;

namespace PS7ScriptDesk.Tests;

public sealed class GitChangesSelectionStateRefreshTests
{
    [Fact]
    public void StageRefreshRebindsSelectionToTheStagedRepresentation()
    {
        using var changes = CreateChanges(out var coordinator);
        var working = Status("Discard-Test.txt", ' ', 'M');
        coordinator.PublishState(State(working));

        changes.SelectFile(working, "CHANGES");
        Assert.Equal("Working-tree change", changes.SelectedStatusText);
        Assert.True(changes.StageCommand.CanExecute(working));
        Assert.False(changes.UnstageCommand.CanExecute(working));

        var staged = Status("Discard-Test.txt", 'M', ' ');
        coordinator.PublishState(State(staged));

        Assert.Same(staged, changes.SelectedStatus);
        Assert.Same(staged, changes.Groups.Single(group => group.Title == "STAGED CHANGES").SelectedItem);
        Assert.Null(changes.Groups.SingleOrDefault(group => group.Title == "CHANGES")?.SelectedItem);
        Assert.Equal("Staged change", changes.SelectedStatusText);
        Assert.False(changes.StageCommand.CanExecute(staged));
        Assert.True(changes.UnstageCommand.CanExecute(staged));
    }

    [Fact]
    public void UnstageRefreshRebindsSelectionToTheWorkingTreeRepresentation()
    {
        using var changes = CreateChanges(out var coordinator);
        var staged = Status("Discard-Test.txt", 'M', ' ');
        coordinator.PublishState(State(staged));

        changes.SelectFile(staged, "STAGED CHANGES");
        Assert.Equal("Staged change", changes.SelectedStatusText);
        Assert.True(changes.UnstageCommand.CanExecute(staged));

        var working = Status("Discard-Test.txt", ' ', 'M');
        coordinator.PublishState(State(working));

        Assert.Same(working, changes.SelectedStatus);
        Assert.Same(working, changes.Groups.Single(group => group.Title == "CHANGES").SelectedItem);
        Assert.Equal("Working-tree change", changes.SelectedStatusText);
        Assert.True(changes.StageCommand.CanExecute(working));
        Assert.False(changes.UnstageCommand.CanExecute(working));
    }

    [Fact]
    public void BothStatesPreserveTheSelectedSemanticGroupAndDiffScope()
    {
        using var changes = CreateChanges(out var coordinator);
        var both = Status("Discard-Test.txt", 'M', 'M');
        coordinator.PublishState(State(both));

        changes.SelectFile(both, "CHANGES");
        Assert.Equal("Working-tree change", changes.SelectedStatusText);
        Assert.True(changes.StageCommand.CanExecute(both));
        Assert.False(changes.UnstageCommand.CanExecute(both));

        var refreshedBoth = Status("Discard-Test.txt", 'M', 'M');
        coordinator.PublishState(State(refreshedBoth));
        Assert.Same(refreshedBoth, changes.SelectedStatus);
        Assert.Equal("Working-tree change", changes.SelectedStatusText);
        Assert.True(changes.StageCommand.CanExecute(refreshedBoth));
        Assert.False(changes.UnstageCommand.CanExecute(refreshedBoth));

        changes.SelectFile(refreshedBoth, "STAGED CHANGES");
        Assert.Equal("Staged change", changes.SelectedStatusText);
        Assert.False(changes.StageCommand.CanExecute(refreshedBoth));
        Assert.True(changes.UnstageCommand.CanExecute(refreshedBoth));
    }

    [Fact]
    public async Task SelectionRefreshLoadsTheDiffForTheReconciledScope()
    {
        using var changes = CreateChanges(out var coordinator);
        var working = Status("Discard-Test.txt", ' ', 'M');
        coordinator.PublishState(State(working));
        changes.SelectFile(working, "CHANGES");
        await WaitForDiffAsync(changes);
        Assert.Equal(GitDiffScope.Unstaged, changes.SelectedDiff?.Scope);

        var staged = Status("Discard-Test.txt", 'M', ' ');
        coordinator.PublishState(State(staged));
        await WaitForDiffAsync(changes);
        Assert.Equal(GitDiffScope.Staged, changes.SelectedDiff?.Scope);
    }

    [Fact]
    public void NoSuccessfulRefreshLeavesTheExistingSelectionAndActionsUntouched()
    {
        using var changes = CreateChanges(out var coordinator);
        var working = Status("Discard-Test.txt", ' ', 'M');
        coordinator.PublishState(State(working));
        changes.SelectFile(working, "CHANGES");

        Assert.Same(working, changes.SelectedStatus);
        Assert.True(changes.StageCommand.CanExecute(working));
        Assert.False(changes.UnstageCommand.CanExecute(working));
    }

    [Fact]
    public void StagedAddedFileCannotBeDiscardedThroughTheGenericDiscardCommand()
    {
        using var changes = CreateChanges(out var coordinator);
        var stagedAdded = new GitFileStatus("C:\\repo\\Untracked-Test.txt", "Untracked-Test.txt", null,
            'A', ' ', true, false, false, false, false, true, true, false);
        coordinator.PublishState(State(stagedAdded));

        changes.SelectFile(stagedAdded, "STAGED CHANGES");

        Assert.False(changes.DiscardCommand.CanExecute(stagedAdded));
        Assert.False(stagedAdded.HasUnstagedChanges);
    }

    [Fact]
    public void DisappearingSelectionClearsStatusAndDiff()
    {
        using var changes = CreateChanges(out var coordinator);
        var working = Status("Discard-Test.txt", ' ', 'M');
        coordinator.PublishState(State(working));
        changes.SelectFile(working, "CHANGES");

        coordinator.PublishState(State());

        Assert.Null(changes.SelectedStatus);
        Assert.Null(changes.SelectedDiff);
        Assert.Equal("No file selected", changes.SelectedPathText);
        Assert.Equal("Select a file to review its saved Git change.", changes.DiffStatusText);
        Assert.False(changes.DiscardCommand.CanExecute(working));
    }

    [Fact]
    public void UnstageOfStagedAddedFileRebindsSelectionToUntrackedGroup()
    {
        using var changes = CreateChanges(out var coordinator);
        var staged = new GitFileStatus("C:\\repo\\Untracked-Test.txt", "Untracked-Test.txt", null,
            'A', ' ', true, false, false, false, false, true, false, false);
        coordinator.PublishState(State(staged));
        changes.SelectFile(staged, "STAGED CHANGES");

        var untracked = new GitFileStatus("C:\\repo\\Untracked-Test.txt", "Untracked-Test.txt", null,
            '?', '?', false, true, false, false, false, false, false, false);
        coordinator.PublishState(State(untracked));

        Assert.Same(untracked, changes.SelectedStatus);
        Assert.Same(untracked, changes.Groups.Single(group => group.Title == "UNTRACKED").SelectedItem);
        Assert.Equal("Untracked file", changes.SelectedStatusText);
    }

    private static GitChangesViewModel CreateChanges(out GitWorkspaceCoordinator coordinator)
    {
        var runner = new GitCommandRunner();
        var service = new GitService(runner, new GitRepositoryLocator(runner));
        coordinator = new GitWorkspaceCoordinator(service);
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        return new GitChangesViewModel(coordinator, host);
    }

    private static GitRepositoryState State(params GitFileStatus[] changes)
        => new(
            new GitEnvironmentInfo(true, "git.exe", "2.0", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow) { Changes = changes };

    private static GitFileStatus Status(string relativePath, char index, char working)
        => new($"C:\\repo\\{relativePath}", relativePath, null, index, working, true, false, false,
            false, false, false, index == 'M' || working == 'M', false);

    private static async Task WaitForDiffAsync(GitChangesViewModel changes)
    {
        for (var attempt = 0; attempt < 50 && changes.IsDiffLoading; attempt++)
            await Task.Delay(10);
    }
}
