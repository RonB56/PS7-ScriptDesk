using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell;

public partial class BranchPickerWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ObservableCollection<GitBranch> _visibleBranches = new();
    private int _remoteCount;

    public BranchPickerWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        BranchList.ItemsSource = _visibleBranches;
        Loaded += async (_, _) =>
        {
            try { await ReloadAsync(); }
            catch (Exception ex) { SetStatus($"Branch Manager could not refresh: {ex.Message}"); }
        };
    }

    private async Task ReloadAsync()
    {
        await _viewModel.RefreshBranchesAsync();
        var branches = _viewModel.GitBranchState?.Branches ?? Array.Empty<GitBranch>();
        _remoteCount = (await _viewModel.GetRemotesAsync()).Remotes.Count;
        _visibleBranches.Clear();
        foreach (var branch in branches.Where(IsMatch)) _visibleBranches.Add(branch);
        BranchList.SelectedItem = _visibleBranches.FirstOrDefault(branch => branch.IsCurrent);
        UpdateActionState();
    }

    private bool IsMatch(GitBranch branch) => string.IsNullOrWhiteSpace(SearchBox.Text) || branch.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
    private GitBranch? SelectedBranch => BranchList.SelectedItem as GitBranch;

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedBranch is not { IsRemote: false } branch) { SetStatus("Select a local branch first."); return; }
            if (branch.IsCurrent) { SetStatus("Cannot switch because this branch is already checked out."); return; }
            if (await _viewModel.SwitchBranchAsync(branch.Name) is not null) await ReloadAsync();
        }
        catch (Exception ex) { SetStatus($"Switch failed: {ex.Message}"); }
    }

    private async void CreateSwitch_Click(object sender, RoutedEventArgs e) { try { await CreateAsync(true); } catch (Exception ex) { SetStatus($"Create & Switch failed: {ex.Message}"); } }
    private async void CreateOnly_Click(object sender, RoutedEventArgs e) { try { await CreateAsync(false); } catch (Exception ex) { SetStatus($"Create Only failed: {ex.Message}"); } }

    private async Task CreateAsync(bool switchTo)
    {
        var name = PromptForName("New branch name", "Create Branch");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await _viewModel.CreateBranchAsync(name.Trim(), switchTo) is not null) await ReloadAsync();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedBranch is not { IsRemote: false } branch) { SetStatus("Select a local branch to rename."); return; }
            var name = PromptForName("New branch name", "Rename Branch");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (await _viewModel.RenameBranchAsync(branch.Name, name.Trim()) is not null) await ReloadAsync();
        }
        catch (Exception ex) { SetStatus($"Rename failed: {ex.Message}"); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedBranch is not { IsRemote: false } branch) { SetStatus("Select a local branch to delete."); return; }
            if (branch.IsCurrent) { SetStatus("Cannot delete the currently checked-out branch."); return; }
            if (!_viewModel.ConfirmBranchDeletion(branch.Name)) return;
            if (await _viewModel.DeleteBranchAsync(branch.Name) is not null) await ReloadAsync();
        }
        catch (Exception ex) { SetStatus($"Delete failed: {ex.Message}"); }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var remotes = await GetRemotesAsync();
            if (remotes.Count != 1) { SetStatus(remotes.Count == 0 ? "Publish unavailable: no remote is configured." : "Publish requires exactly one configured remote."); return; }
            await _viewModel.PublishBranchAsync(remotes[0].Name);
            await ReloadAsync();
        }
        catch (Exception ex) { SetStatus($"Publish failed: {ex.Message}"); }
    }

    private async Task<IReadOnlyList<GitRemote>> GetRemotesAsync()
    {
        var state = await _viewModel.GetRemotesAsync();
        return state.Remotes;
    }

    private string? PromptForName(string prompt, string title)
    {
        var dialog = new BranchNameWindow(title, prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.BranchName : null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _visibleBranches.Clear();
        foreach (var branch in _viewModel.GitBranchState?.Branches ?? Array.Empty<GitBranch>()) if (IsMatch(branch)) _visibleBranches.Add(branch);
        UpdateActionState();
    }
    private void BranchList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionState();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateActionState()
    {
        var branch = SelectedBranch;
        var local = branch is { IsRemote: false };
        RenameButton.IsEnabled = local;
        DeleteButton.IsEnabled = local && branch!.IsCurrent == false;
        SwitchButton.IsEnabled = local && branch!.IsCurrent == false;
        PublishButton.IsEnabled = local && branch!.IsCurrent == true && _remoteCount == 1;
        DeleteButton.ToolTip = branch?.IsCurrent == true ? "Cannot delete the currently checked-out branch." : "Safely delete the selected non-current local branch.";
        SwitchButton.ToolTip = branch?.IsCurrent == true ? "This branch is already checked out." : "Switch to the selected different local branch.";
    }

    private void SetStatus(string message)
    {
        ActionStatusText.Text = message;
        _viewModel.StatusText = message;
    }
}

internal sealed class BranchNameWindow : Window
{
    private readonly TextBox _input = new() { MinWidth = 280, Padding = new Thickness(6) };
    public string BranchName => _input.Text;
    public BranchNameWindow(string title, string prompt)
    {
        Title = title; Width = 380; Height = 170; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Theme.App.Background"); Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Theme.Text.Primary");
        var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0, 8, 6, 0) }; ok.SetResourceReference(FrameworkElement.StyleProperty, "IdeDialogPrimaryButtonStyle"); ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 8, 0, 0) }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "IdeDialogSecondaryButtonStyle");
        Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) }, _input, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } };
        Loaded += (_, _) => { _input.Focus(); Keyboard.Focus(_input); };
    }
}
