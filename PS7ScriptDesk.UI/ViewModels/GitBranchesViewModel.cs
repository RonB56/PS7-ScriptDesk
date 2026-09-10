using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitBranchesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator; private readonly MainWindowViewModel _actions; private GitBranchState? _state; private GitBranch? _selected; private string _newBranchName = string.Empty; private string _renameBranchName = string.Empty;
    public GitBranchesViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel actions) { _coordinator = coordinator; _actions = actions; _state = coordinator.CurrentBranchState; coordinator.BranchStateChanged += Coordinator_BranchStateChanged; SwitchCommand = new RelayCommand(p => _ = SwitchAsync(p as GitBranch), p => p is GitBranch); CreateCommand = new RelayCommand(() => _ = CreateAsync()); RenameCommand = new RelayCommand(() => _ = RenameAsync(), () => SelectedBranch is not null && !string.IsNullOrWhiteSpace(RenameBranchName)); DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => SelectedBranch is not null); }
    public event PropertyChangedEventHandler? PropertyChanged; public IReadOnlyList<GitBranch> Branches => _state?.Branches ?? Array.Empty<GitBranch>(); public GitBranch? SelectedBranch { get => _selected; set { _selected = value; OnPropertyChanged(); Raise(); } } public string NewBranchName { get => _newBranchName; set { _newBranchName = value ?? string.Empty; OnPropertyChanged(); } } public string RenameBranchName { get => _renameBranchName; set { _renameBranchName = value ?? string.Empty; OnPropertyChanged(); Raise(); } } public string CurrentText => _state?.CurrentBranch is null ? "No branch" : $"Current: {_state.CurrentBranch}"; public string DetachedText => _state?.IsDetachedHead == true ? $"Detached HEAD — {_state.HeadCommit ?? "unknown"}" : string.Empty; public ICommand SwitchCommand { get; } public ICommand CreateCommand { get; } public ICommand RenameCommand { get; } public ICommand DeleteCommand { get; }
    private Task SwitchAsync(GitBranch? branch) => branch is null ? Task.CompletedTask : _actions.SwitchBranchAsync(branch.Name);
    private async Task CreateAsync() { if (!string.IsNullOrWhiteSpace(NewBranchName)) await _actions.CreateBranchAsync(NewBranchName.Trim(), true); }
    private async Task RenameAsync() { if (SelectedBranch is not null && !string.IsNullOrWhiteSpace(RenameBranchName)) await _actions.RenameBranchAsync(SelectedBranch.Name, RenameBranchName.Trim()); }
    private async Task DeleteAsync() { if (SelectedBranch is not null && _actions.ConfirmBranchDeletion(SelectedBranch.Name)) await _actions.DeleteBranchAsync(SelectedBranch.Name); }
    private void Raise() { (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged(); (RenameCommand as RelayCommand)?.RaiseCanExecuteChanged(); (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    private void Coordinator_BranchStateChanged(object? sender, GitBranchState? state) { _state = state; OnPropertyChanged(nameof(Branches)); OnPropertyChanged(nameof(CurrentText)); OnPropertyChanged(nameof(DetachedText)); }
    public void Dispose() => _coordinator.BranchStateChanged -= Coordinator_BranchStateChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
