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

    public BranchPickerWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        BranchList.ItemsSource = _visibleBranches;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await _viewModel.RefreshBranchesAsync();
        var branches = _viewModel.GitBranchState?.Branches ?? Array.Empty<GitBranch>();
        _visibleBranches.Clear();
        foreach (var branch in branches.Where(IsMatch)) _visibleBranches.Add(branch);
        BranchList.SelectedItem = _visibleBranches.FirstOrDefault(branch => branch.IsCurrent);
    }

    private bool IsMatch(GitBranch branch) => string.IsNullOrWhiteSpace(SearchBox.Text) || branch.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
    private GitBranch? SelectedBranch => BranchList.SelectedItem as GitBranch;

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is not { IsRemote: false } branch) return;
        if (await _viewModel.SwitchBranchAsync(branch.Name) is not null) await ReloadAsync();
    }

    private async void CreateSwitch_Click(object sender, RoutedEventArgs e) => await CreateAsync(true);
    private async void CreateOnly_Click(object sender, RoutedEventArgs e) => await CreateAsync(false);

    private async Task CreateAsync(bool switchTo)
    {
        var name = PromptForName("New branch name", "Create Branch");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await _viewModel.CreateBranchAsync(name.Trim(), switchTo) is not null) await ReloadAsync();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is not { IsRemote: false } branch) return;
        var name = PromptForName("New branch name", "Rename Branch");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await _viewModel.RenameBranchAsync(branch.Name, name.Trim()) is not null) await ReloadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is not { IsRemote: false } branch || branch.IsCurrent) return;
        if (!_viewModel.ConfirmBranchDeletion(branch.Name)) return;
        if (await _viewModel.DeleteBranchAsync(branch.Name) is not null) await ReloadAsync();
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        var remotes = await GetRemotesAsync();
        if (remotes.Count != 1) { _viewModel.StatusText = remotes.Count == 0 ? "No remote is configured." : "Select a remote before publishing this branch."; return; }
        await _viewModel.PublishBranchAsync(remotes[0].Name);
        await ReloadAsync();
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
    }
    private void BranchList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class BranchNameWindow : Window
{
    private readonly TextBox _input = new() { MinWidth = 280, Padding = new Thickness(6) };
    public string BranchName => _input.Text;
    public BranchNameWindow(string title, string prompt)
    {
        Title = title; Width = 360; Height = 150; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0, 8, 6, 0) }; ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
        Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) }, _input, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } };
        Loaded += (_, _) => { _input.Focus(); Keyboard.Focus(_input); };
    }
}
