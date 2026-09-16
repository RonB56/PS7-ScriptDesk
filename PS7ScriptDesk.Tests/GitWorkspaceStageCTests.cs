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

        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo",
            DateTimeOffset.UtcNow));
        coordinator.PublishBranchState(branchState);
        coordinator.PublishRemoteState(remoteState);

        Assert.Equal("main", workspace.Branches!.CurrentText.Replace("Current: ", string.Empty, StringComparison.Ordinal));
        Assert.Single(workspace.Branches.Branches);
        Assert.Equal("Remote: origin", workspace.Remotes!.SummaryText);
        Assert.Single(workspace.Remotes.Remotes);
    }

    [Fact]
    public void CoordinatorCanonicalizesRuntimeBranchSnapshotBeforeBranchManagerConsumesIt()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var state = new GitBranchState(new[]
        {
            new GitBranch("refs/heads/main", "refs/heads/main", true, false, null, null, null, "one"),
            new GitBranch("refs/heads/uat-branch", "refs/heads/uat-branch", false, false, null, null, null, "two"),
            new GitBranch("refs/heads/feature/foo", "refs/heads/feature/foo", false, false, null, null, null, "three")
        }, "refs/heads/main", false, "one");

        coordinator.PublishBranchState(state);

        var branches = coordinator.CurrentBranchState!.Branches;
        Assert.Equal(new[] { "main", "uat-branch", "feature/foo" }, branches.Select(branch => branch.Name));
        Assert.Equal("refs/heads/uat-branch", branches[1].FullName);
        Assert.Equal("main", coordinator.CurrentBranchState.CurrentBranch);
    }

    [Fact]
    public void BranchSelectionRebindsToTheCurrentBranchInEachImmutableSnapshot()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        using var branches = new GitBranchesViewModel(coordinator, host);
        var firstMain = new GitBranch("main", "refs/heads/main", true, false, null, null, null, "one");
        coordinator.PublishBranchState(new GitBranchState(new[] { firstMain }, "main", false, "one"));
        Assert.Same(firstMain, branches.SelectedBranch);

        var replacementMain = new GitBranch("main", "refs/heads/main", true, false, null, null, null, "two");
        coordinator.PublishBranchState(new GitBranchState(new[] { replacementMain }, "main", false, "two"));
        Assert.Same(replacementMain, branches.SelectedBranch);
        Assert.Equal("Current: main", branches.CurrentText);
    }

    [Fact]
    public void BranchSelectionIgnoresTransientNullWhileCurrentBranchRemainsAvailable()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        using var branches = new GitBranchesViewModel(coordinator, host);
        var current = new GitBranch("main", "refs/heads/main", true, false, null, null, null, "one");
        coordinator.PublishBranchState(new GitBranchState(new[] { current }, "main", false, "one"));

        branches.SelectedBranch = null;

        Assert.Same(current, branches.SelectedBranch);
    }

    [Fact]
    public void InitialBranchSnapshotSelectsTheCurrentLocalBranchWithoutRefresh()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var current = new GitBranch("main", "refs/heads/main", true, false, null, null, null, "one");
        coordinator.PublishBranchState(new GitBranchState(new[] { current }, "main", false, "one"));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));

        using var branches = new GitBranchesViewModel(coordinator, host);

        Assert.Same(current, branches.Branches.Single());
        Assert.Same(current, branches.SelectedBranch);
    }

    [Fact]
    public void GitWorkspaceSelectionWiringMaintainsOneVisualSelectionAndUsesSemanticDialogStyles()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml.cs");
        var workspace = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var dialog = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Dialogs", "IdeMessageDialog.xaml.cs");
        var app = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "App.xaml");

        Assert.DoesNotContain("Dispatcher.BeginInvoke", shell, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedItem, Mode=OneWay}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding Branches.SelectedBranchName, Mode=OneWay}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"BranchSelectionChanged\"", workspace, StringComparison.Ordinal);
        Assert.Contains("IdeDialogDestructiveButtonStyle", dialog, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDialogDestructiveButtonStyle\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchSelectorUsesAnExplicitNameTemplateWhileKeepingTypedSelection()
    {
        var workspace = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var marker = workspace.IndexOf("AutomationProperties.Name=\"Git branch selector\"", StringComparison.Ordinal);
        var start = workspace.LastIndexOf("<ComboBox", marker, StringComparison.Ordinal);
        var end = workspace.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        var branchCombo = workspace[start..(end + "</ComboBox>".Length)];

        Assert.Contains("ItemsSource=\"{Binding Branches.Branches}\"", branchCombo, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding Branches.SelectedBranchName, Mode=OneWay}\"", branchCombo, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"OperationName\"", branchCombo, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"BranchSelectionChanged\"", branchCombo, StringComparison.Ordinal);
        Assert.Contains("<ComboBox.ItemTemplate>", branchCombo, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", branchCombo, StringComparison.Ordinal);
        Assert.Contains("OperationName =>", TestRepositoryPaths.ReadFile("PS7ScriptDesk.Domain", "Models", "GitBranch.cs"), StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"6,3\" />", branchCombo, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayMemberPath", branchCombo, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedBranchActionSurfaceExposesMutationsWithoutLegacyPickerNavigation()
    {
        var workspace = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var codeBehind = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml.cs");

        Assert.Contains("x:Name=\"BranchActionsButton\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Click=\"BranchActionsButton_Click\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Placement=\"Bottom\"", workspace, StringComparison.Ordinal);
        Assert.Contains("StaysOpen=\"False\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Header=\"Rename\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Header=\"Delete\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Header=\"Create &amp; Switch\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Header=\"Create Only\"", workspace, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding Branches.CanRenameSelected}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding Branches.CanDeleteSelected}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("BranchActionsMenu_Opened", workspace, StringComparison.Ordinal);
        Assert.Contains("BranchActionsButton_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("menu.IsOpen = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RenameBranchMenuItem.IsEnabled", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await viewModel.LegacyActions.RenameBranchAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await viewModel.LegacyActions.DeleteBranchAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("new BranchPickerWindow", codeBehind, StringComparison.Ordinal);
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
