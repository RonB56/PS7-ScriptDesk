using System.Windows;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell;

public partial class GitWorkspaceWindow : Window
{
    public GitWorkspaceWindow(GitWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += (_, _) =>
        {
            foreach (var branch in viewModel.Branches?.Branches.Take(50) ?? Array.Empty<PS7ScriptDesk.Domain.Models.GitBranch>())
            {
                PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write(
                    "GitWorkspaceWindow.BranchProjection",
                    "RENDER_SOURCE",
                    $"rawRef={branch.FullName}; operationName={branch.OperationName}; displayName={branch.DisplayName}; isRemote={branch.IsRemote}; isCurrent={branch.IsCurrent}; dataContextId={viewModel.GetHashCode():X8}");
            }
        };
        if (viewModel.History is not null) viewModel.History.CopyHashRequested += History_CopyHashRequested;
        if (viewModel.Branches is not null) viewModel.Branches.PropertyChanged += Branches_PropertyChanged;
        Closed += (_, _) =>
        {
            if (viewModel.Branches is not null) viewModel.Branches.PropertyChanged -= Branches_PropertyChanged;
            viewModel.Dispose();
        };
    }

    private static void History_CopyHashRequested(object? sender, string hash) => System.Windows.Clipboard.SetText(hash);

    private void BranchActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        var menu = button.ContextMenu;
        PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write(
            "GitWorkspaceWindow.BranchAction",
            "CLICK_ENTER",
            $"control=BranchActionsButton; menuResolved={menu is not null}; isOpenBefore={menu?.IsOpen ?? false}; selected={GetSelectedBranchName()}; canRename={GetCanRenameSelected()}; canDelete={GetCanDeleteSelected()}");
        if (menu is null)
        {
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "CLICK_EXIT", "control=BranchActionsButton; result=no-context-menu");
            return;
        }

        menu.PlacementTarget = button;
        PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "MENU_RESOLVED", $"placementTarget=BranchActionsButton; placement={menu.Placement}; isOpenBefore={menu.IsOpen}");
        UpdateBranchActionMenuState();
        menu.IsOpen = true;
        PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "CLICK_EXIT", $"control=BranchActionsButton; isOpenAfter={menu.IsOpen}; selected={GetSelectedBranchName()}; canRename={GetCanRenameSelected()}; canDelete={GetCanDeleteSelected()}");
        e.Handled = true;
    }

    private void BranchActionsMenu_Closed(object? sender, RoutedEventArgs e)
        => PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "MENU_CLOSED", $"selected={GetSelectedBranchName()}; isOpenAfter={(sender as System.Windows.Controls.ContextMenu)?.IsOpen ?? false}");

    private async void SwitchBranch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not GitWorkspaceViewModel { LegacyActions: not null, Branches: not null } viewModel || viewModel.Branches.SelectedBranch is not { IsRemote: false } branch)
                return;
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "ACCEPTED", $"action=Switch; operationName={branch.OperationName}; isCurrent={branch.IsCurrent}; isRemote={branch.IsRemote}");
            await viewModel.LegacyActions.SwitchBranchAsync(branch.OperationName);
        }
        catch (Exception ex)
        {
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "FAILURE", $"action=Switch; error={ex.GetType().Name}");
        }
    }

    private async void CreateSwitchBranch_Click(object sender, RoutedEventArgs e) => await CreateBranchAsync(true);
    private async void CreateOnlyBranch_Click(object sender, RoutedEventArgs e) => await CreateBranchAsync(false);

    private async Task CreateBranchAsync(bool switchTo)
    {
        if (DataContext is not GitWorkspaceViewModel { LegacyActions: not null } viewModel) return;
        var name = PromptForBranchName(switchTo ? "Create Branch" : "Create Branch", "New branch name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var normalized = name.Trim();
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "ACCEPTED", $"action={(switchTo ? "CreateAndSwitch" : "CreateOnly")}; name={normalized}");
            await viewModel.LegacyActions.CreateBranchAsync(normalized, switchTo);
        }
    }

    private async void RenameBranch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not GitWorkspaceViewModel { LegacyActions: not null, Branches: not null } viewModel || viewModel.Branches.SelectedBranch is not { IsRemote: false } branch)
                return;
            var name = PromptForBranchName("Rename Branch", "New branch name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var normalized = name.Trim();
                PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "ACCEPTED", $"action=Rename; oldOperationName={branch.OperationName}; newName={normalized}; isCurrent={branch.IsCurrent}");
                await viewModel.LegacyActions.RenameBranchAsync(branch.OperationName, normalized);
            }
        }
        catch (Exception ex)
        {
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "FAILURE", $"action=Rename; error={ex.GetType().Name}");
        }
    }

    private async void DeleteBranch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not GitWorkspaceViewModel { LegacyActions: not null, Branches: not null } viewModel || viewModel.Branches.SelectedBranch is not { IsRemote: false, IsCurrent: false } branch)
                return;
            if (viewModel.LegacyActions.ConfirmBranchDeletion(branch.OperationName))
            {
                PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "ACCEPTED", $"action=Delete; operationName={branch.OperationName}; isCurrent={branch.IsCurrent}; isRemote={branch.IsRemote}");
                await viewModel.LegacyActions.DeleteBranchAsync(branch.OperationName);
            }
        }
        catch (Exception ex)
        {
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "FAILURE", $"action=Delete; error={ex.GetType().Name}");
        }
    }

    private string? PromptForBranchName(string title, string prompt)
    {
        var dialog = new BranchNameWindow(title, prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.BranchName : null;
    }

    private void BranchActionsMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateBranchActionMenuState();
    }

    private void Branches_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GitBranchesViewModel.CanRenameSelected) or nameof(GitBranchesViewModel.CanDeleteSelected) or nameof(GitBranchesViewModel.CanSwitchSelected) or nameof(GitBranchesViewModel.SelectedBranch) or null)
            _ = Dispatcher.InvokeAsync(UpdateBranchActionMenuState, System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private void UpdateBranchActionMenuState()
    {
        if (DataContext is GitWorkspaceViewModel { Branches: not null } viewModel)
        {
            RenameBranchMenuItem.IsEnabled = viewModel.Branches.CanRenameSelected;
            DeleteBranchMenuItem.IsEnabled = viewModel.Branches.CanDeleteSelected;
            PS7ScriptDesk.Application.Diagnostics.StartupLifecycleTrace.Write("GitWorkspaceWindow.BranchAction", "STATE", $"selected={viewModel.Branches.SelectedBranch?.OperationName ?? "none"}; canRename={RenameBranchMenuItem.IsEnabled}; canDelete={DeleteBranchMenuItem.IsEnabled}; canSwitch={viewModel.Branches.CanSwitchSelected}");
        }
    }

    private string GetSelectedBranchName() => (DataContext as GitWorkspaceViewModel)?.Branches?.SelectedBranch?.OperationName ?? "none";
    private bool GetCanRenameSelected() => (DataContext as GitWorkspaceViewModel)?.Branches?.CanRenameSelected == true;
    private bool GetCanDeleteSelected() => (DataContext as GitWorkspaceViewModel)?.Branches?.CanDeleteSelected == true;

    private void BranchSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { Branches: not null } viewModel &&
            sender is System.Windows.Controls.ComboBox comboBox &&
            comboBox.SelectedItem is PS7ScriptDesk.Domain.Models.GitBranch branch)
        {
            viewModel.Branches.SelectedBranch = branch;
        }
    }

    private void ChangeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { Changes: not null } viewModel &&
            sender is System.Windows.Controls.ListBox listBox &&
            listBox.SelectedItem is PS7ScriptDesk.Domain.Models.GitFileStatus status)
        {
            viewModel.Changes.SelectFile(
                status,
                (listBox.DataContext as PS7ScriptDesk.UI.ViewModels.SourceControlGroupViewModel)?.Title);
        }
    }

    private void HistoryCommitSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { History: not null } viewModel && sender is System.Windows.Controls.ListBox listBox)
            _ = viewModel.History.SelectCommitAsync(listBox.SelectedItem as PS7ScriptDesk.Domain.Models.GitCommit);
    }

    private void HistoryFileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { History: not null } viewModel && sender is System.Windows.Controls.ListBox listBox)
            _ = viewModel.History.SelectFileAsync(listBox.SelectedItem as PS7ScriptDesk.Domain.Models.GitCommitFileChange);
    }
}
