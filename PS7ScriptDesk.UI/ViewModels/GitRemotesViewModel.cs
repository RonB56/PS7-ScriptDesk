using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitRemotesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator; private readonly MainWindowViewModel _actions; private GitRemoteState? _state; private bool _isBusy;
    public GitRemotesViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel actions) { _coordinator = coordinator; _actions = actions; _state = coordinator.CurrentRemoteState; coordinator.RemoteStateChanged += Coordinator_RemoteStateChanged; RefreshCommand = new RelayCommand(() => _ = RefreshAsync()); FetchCommand = new RelayCommand(() => _ = _actions.FetchAsync(), () => !IsBusy); PullCommand = new RelayCommand(() => _ = _actions.PullAsync(), () => !IsBusy); PushCommand = new RelayCommand(() => _ = _actions.PushAsync(), () => !IsBusy); SyncCommand = new RelayCommand(() => _ = _actions.SyncAsync(), () => !IsBusy); PublishCommand = new RelayCommand(() => _ = PublishAsync(), () => !IsBusy && SelectedRemote is not null); }
    public event PropertyChangedEventHandler? PropertyChanged; public IReadOnlyList<GitRemote> Remotes => _state?.Remotes ?? Array.Empty<GitRemote>(); public GitRemote? SelectedRemote { get; set; } public bool IsBusy { get => _isBusy || _actions.IsRemoteOperationInProgress; private set { _isBusy = value; OnPropertyChanged(); Raise(); } } public string SummaryText => _state is null ? "Remote state not loaded." : _state.Error ?? (_state.Remotes.Count == 0 ? "No remotes configured." : _state.Remotes.Count == 1 ? $"Remote: {_state.Remotes[0].Name}" : $"{_state.Remotes.Count} remotes configured."); public ICommand RefreshCommand { get; } public ICommand FetchCommand { get; } public ICommand PullCommand { get; } public ICommand PushCommand { get; } public ICommand SyncCommand { get; } public ICommand PublishCommand { get; }
    public async Task RefreshAsync(CancellationToken token = default) { IsBusy = true; try { var state = await _actions.GetRemotesAsync(token); _coordinator.PublishRemoteState(state); } finally { IsBusy = false; } }
    private async Task PublishAsync() { if (SelectedRemote is not null) await _actions.PublishBranchAsync(SelectedRemote.Name); }
    private void Coordinator_RemoteStateChanged(object? sender, GitRemoteState? state) { _state = state; OnPropertyChanged(nameof(Remotes)); OnPropertyChanged(nameof(SummaryText)); }
    private void Raise() { foreach (var c in new[] { FetchCommand, PullCommand, PushCommand, SyncCommand, PublishCommand, RefreshCommand }) (c as RelayCommand)?.RaiseCanExecuteChanged(); }
    public void Dispose() => _coordinator.RemoteStateChanged -= Coordinator_RemoteStateChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
