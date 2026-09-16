using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitRemotesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator;
    private readonly MainWindowViewModel _actions;
    private GitRemoteState? _state;
    private GitRemote? _selectedRemote;
    private bool _isBusy;

    public GitRemotesViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel actions)
    {
        _coordinator = coordinator;
        _actions = actions;
        _state = coordinator.CurrentRemoteState;
        _selectedRemote = _state?.Remotes.FirstOrDefault();
        coordinator.RemoteStateChanged += Coordinator_RemoteStateChanged;
        coordinator.StateChanged += Coordinator_StateChanged;
        _actions.PropertyChanged += Actions_PropertyChanged;
        RefreshCommand = new RelayCommand(() => _ = ExecuteRefreshSafelyAsync());
        FetchCommand = new RelayCommand(() => _ = ExecuteRemoteOperationAsync(_actions.FetchAsync, "Fetch"), () => HasRepository && !IsBusy);
        PullCommand = new RelayCommand(() => _ = ExecuteRemoteOperationAsync(_actions.PullAsync, "Pull"), () => HasRepository && !IsBusy);
        PushCommand = new RelayCommand(() => _ = ExecuteRemoteOperationAsync(_actions.PushAsync, "Push"), () => HasRepository && !IsBusy);
        SyncCommand = new RelayCommand(() => _ = ExecuteRemoteOperationAsync(_actions.SyncAsync, "Sync"), () => HasRepository && !IsBusy);
        PublishCommand = new RelayCommand(() => _ = ExecuteRemoteOperationAsync(PublishAsync, "Publish"), () => HasRepository && !IsBusy && SelectedRemote is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<GitRemote> Remotes => _state?.Remotes ?? Array.Empty<GitRemote>();
    public GitRemote? SelectedRemote { get => _selectedRemote; set { if (ReferenceEquals(_selectedRemote, value)) return; _selectedRemote = value; OnPropertyChanged(); Raise(); } }
    public bool HasRepository => _coordinator.CurrentState?.Repository.IsRepository == true;
    public bool IsBusy { get => _isBusy || _actions.IsRemoteOperationInProgress; private set { _isBusy = value; OnPropertyChanged(); Raise(); } }
    public string SummaryText => !HasRepository ? "No Git repository detected." : _state is null ? "Remote state not loaded." : _state.Error ?? (_state.Remotes.Count == 0 ? "No remotes configured. Add a remote before using Fetch, Pull, Push, or Sync." : _state.Remotes.Count == 1 ? $"Remote: {_state.Remotes[0].Name}" : $"{_state.Remotes.Count} remotes configured.");
    public ICommand RefreshCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand PublishCommand { get; }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        IsBusy = true;
        try { _coordinator.PublishRemoteState(await _actions.GetRemotesAsync(token)); }
        finally { IsBusy = false; }
    }

    private Task ExecuteRefreshSafelyAsync() => _actions.ExecuteGitCommandSafelyAsync(() => RefreshAsync(), "Refresh remotes");

    private async Task ExecuteRemoteOperationAsync(Func<Task> operation, string operationName)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        LogCommandState(operationName, attemptId, "before");
        try { await _actions.ExecuteGitCommandSafelyAsync(operation, operationName).ConfigureAwait(false); LogCommandState(operationName, attemptId, "after"); }
        finally { LogCommandState(operationName, attemptId, "finally"); }
    }

    private async Task PublishAsync() { if (SelectedRemote is not null) await _actions.PublishBranchAsync(SelectedRemote.Name); }

    private void Coordinator_RemoteStateChanged(object? sender, GitRemoteState? state)
    {
        var selectedName = _selectedRemote?.Name;
        _state = state;
        _selectedRemote = selectedName is null
            ? state?.Remotes.FirstOrDefault()
            : state?.Remotes.FirstOrDefault(remote => string.Equals(remote.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(Remotes));
        OnPropertyChanged(nameof(SelectedRemote));
        OnPropertyChanged(nameof(SummaryText));
        Raise();
    }

    private void Coordinator_StateChanged(object? sender, GitRepositoryState? state)
    {
        if (!HasRepository) SelectedRemote = null;
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(SummaryText));
        Raise();
    }

    private void Actions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || e.PropertyName is nameof(MainWindowViewModel.IsRemoteOperationInProgress) || e.PropertyName is nameof(MainWindowViewModel.IsGitMutationInProgress))
        {
            OnPropertyChanged(nameof(IsBusy));
            Raise();
        }
    }

    private void LogCommandState(string operationName, string attemptId, string stage)
        => DeveloperDiagnostics.LogInfo("Git", "Remote command state trace.", new Dictionary<string, object?>
        {
            ["attemptId"] = attemptId, ["operation"] = operationName, ["stage"] = stage,
            ["isBusy"] = IsBusy, ["localBusy"] = _isBusy,
            ["parentRemoteOperationInProgress"] = _actions.IsRemoteOperationInProgress,
            ["parentGitMutationInProgress"] = _actions.IsGitMutationInProgress,
            ["hasRepository"] = HasRepository, ["selectedRemote"] = SelectedRemote?.Name,
            ["fetchCanExecute"] = FetchCommand.CanExecute(null), ["pullCanExecute"] = PullCommand.CanExecute(null),
            ["pushCanExecute"] = PushCommand.CanExecute(null), ["syncCanExecute"] = SyncCommand.CanExecute(null)
        });

    private void Raise() { foreach (var c in new[] { FetchCommand, PullCommand, PushCommand, SyncCommand, PublishCommand, RefreshCommand }) (c as RelayCommand)?.RaiseCanExecuteChanged(); }
    public void Dispose() { _coordinator.RemoteStateChanged -= Coordinator_RemoteStateChanged; _coordinator.StateChanged -= Coordinator_StateChanged; _actions.PropertyChanged -= Actions_PropertyChanged; }
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
