using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitBranchesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator; private readonly MainWindowViewModel _actions; private GitBranchState? _state; private GitBranch? _selected; private string _newBranchName = string.Empty; private string _renameBranchName = string.Empty;
    public GitBranchesViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel actions) { _coordinator = coordinator; _actions = actions; _state = coordinator.CurrentBranchState; SelectCurrentBranch(_state); LogProjection(_state); coordinator.BranchStateChanged += Coordinator_BranchStateChanged; coordinator.StateChanged += Coordinator_StateChanged; _actions.PropertyChanged += Actions_PropertyChanged; SwitchCommand = new RelayCommand(p => _ = SwitchAsync(p as GitBranch), p => CanSwitch(p as GitBranch)); CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => HasRepository); RenameCommand = new RelayCommand(() => _ = RenameAsync(), () => CanRename(SelectedBranch) && !string.IsNullOrWhiteSpace(RenameBranchName)); DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => CanDelete(SelectedBranch)); }
    public event PropertyChangedEventHandler? PropertyChanged; public IReadOnlyList<GitBranch> Branches => _state?.Branches ?? Array.Empty<GitBranch>(); public bool HasRepository => _coordinator.CurrentState?.Repository.IsRepository == true; public GitBranch? SelectedBranch { get => _selected; set { var next = value ?? GetCurrentLocalBranch(); if (ReferenceEquals(_selected, next)) return; _selected = next; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedBranchName)); Raise(); } } public string? SelectedBranchName => SelectedBranch?.OperationName; public string NewBranchName { get => _newBranchName; set { _newBranchName = value ?? string.Empty; OnPropertyChanged(); } } public string RenameBranchName { get => _renameBranchName; set { _renameBranchName = value ?? string.Empty; OnPropertyChanged(); Raise(); } } public string CurrentText => _state?.CurrentBranch is null ? "No branch" : $"Current: {GitBranchName.Normalize(_state.CurrentBranch, false)}"; public string DetachedText => _state?.IsDetachedHead == true ? $"Detached HEAD — {_state.HeadCommit ?? "unknown"}" : string.Empty; public ICommand SwitchCommand { get; } public ICommand CreateCommand { get; } public ICommand RenameCommand { get; } public ICommand DeleteCommand { get; }
    private Task SwitchAsync(GitBranch? branch) => branch is null ? Task.CompletedTask : _actions.SwitchBranchAsync(branch.OperationName);
    private async Task CreateAsync() { if (!string.IsNullOrWhiteSpace(NewBranchName)) await _actions.CreateBranchAsync(NewBranchName.Trim(), true); }
    private async Task RenameAsync() { if (SelectedBranch is not null && !string.IsNullOrWhiteSpace(RenameBranchName)) await _actions.RenameBranchAsync(SelectedBranch.OperationName, RenameBranchName.Trim()); }
    private async Task DeleteAsync() { if (SelectedBranch is not null && _actions.ConfirmBranchDeletion(SelectedBranch.OperationName)) await _actions.DeleteBranchAsync(SelectedBranch.OperationName); }
    public bool CanSwitchSelected => CanSwitch(SelectedBranch);
    public bool CanDeleteSelected => CanDelete(SelectedBranch);
    public bool CanRenameSelected => CanRename(SelectedBranch);
    private bool CanSwitch(GitBranch? branch) => HasRepository && !_actions.IsGitMutationInProgress && branch is { IsRemote: false, IsCurrent: false };
    private bool CanDelete(GitBranch? branch) => HasRepository && branch is { IsRemote: false, IsCurrent: false };
    private bool CanRename(GitBranch? branch) => HasRepository && branch is { IsRemote: false };
    private void Raise() { (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged(); (RenameCommand as RelayCommand)?.RaiseCanExecuteChanged(); (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged(); (SwitchCommand as RelayCommand)?.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(CanSwitchSelected)); OnPropertyChanged(nameof(CanDeleteSelected)); OnPropertyChanged(nameof(CanRenameSelected)); }
    private void Coordinator_BranchStateChanged(object? sender, GitBranchState? state) { _state = state; SelectCurrentBranch(state); LogProjection(state); OnPropertyChanged(nameof(Branches)); OnPropertyChanged(nameof(CurrentText)); OnPropertyChanged(nameof(DetachedText)); OnPropertyChanged(nameof(SelectedBranch)); OnPropertyChanged(nameof(SelectedBranchName)); Raise(); }
    private void Coordinator_StateChanged(object? sender, GitRepositoryState? state) { OnPropertyChanged(nameof(HasRepository)); Raise(); }
    private void Actions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CanSwitchBranches) or nameof(MainWindowViewModel.IsGitMutationInProgress) or null)
            Raise();
    }
    private void LogProjection(GitBranchState? state)
    {
        foreach (var branch in state?.Branches.Take(50) ?? Array.Empty<GitBranch>())
        {
            StartupLifecycleTrace.Write("GitBranchesViewModel.BranchProjection", "PUBLISHED", $"rawRef={branch.FullName}; operationName={branch.OperationName}; displayName={branch.DisplayName}; isRemote={branch.IsRemote}; isCurrent={branch.IsCurrent}; collection=GitBranchesViewModel; sourceCoordinator={_coordinator.GetHashCode():X8}");
        }
    }
    private void SelectCurrentBranch(GitBranchState? state) => _selected = state is null || state.IsDetachedHead ? null : state.Branches.FirstOrDefault(branch => !branch.IsRemote && (branch.IsCurrent || string.Equals(branch.OperationName, GitBranchName.Normalize(state.CurrentBranch ?? string.Empty, false), StringComparison.Ordinal)));
    private GitBranch? GetCurrentLocalBranch() => _state is null || _state.IsDetachedHead ? null : _state.Branches.FirstOrDefault(branch => !branch.IsRemote && (branch.IsCurrent || string.Equals(branch.OperationName, GitBranchName.Normalize(_state.CurrentBranch ?? string.Empty, false), StringComparison.Ordinal)));
    public void Dispose() { _coordinator.BranchStateChanged -= Coordinator_BranchStateChanged; _coordinator.StateChanged -= Coordinator_StateChanged; _actions.PropertyChanged -= Actions_PropertyChanged; }
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
