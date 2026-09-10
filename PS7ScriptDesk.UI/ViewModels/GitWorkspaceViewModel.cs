using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public enum GitWorkspaceMode { Changes, History }

public sealed class GitWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator;
    private GitRepositoryState? _state;
    private GitWorkspaceMode _mode = GitWorkspaceMode.Changes;

    public GitWorkspaceViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel? legacyActions = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _state = coordinator.CurrentState;
        Changes = legacyActions is null ? null : new GitChangesViewModel(coordinator, legacyActions);
        History = legacyActions is null ? null : new GitHistoryViewModel(coordinator, legacyActions);
        Branches = legacyActions is null ? null : new GitBranchesViewModel(coordinator, legacyActions);
        Remotes = legacyActions is null ? null : new GitRemotesViewModel(coordinator, legacyActions);
        coordinator.StateChanged += Coordinator_StateChanged;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        OpenRepositoryCommand = new RelayCommand(
            () => legacyActions?.OpenRepositoryFolderCommand.Execute(null),
            () => legacyActions?.OpenRepositoryFolderCommand.CanExecute(null) == true);
        CloneRepositoryCommand = new RelayCommand(
            () => legacyActions?.CloneRepositoryCommand.Execute(null),
            () => legacyActions?.CloneRepositoryCommand.CanExecute(null) == true);
        InitializeRepositoryCommand = new RelayCommand(
            () => legacyActions?.InitializeRepositoryCommand.Execute(null),
            () => legacyActions?.InitializeRepositoryCommand.CanExecute(null) == true);
        GitDiagnosticsCommand = new RelayCommand(
            () => legacyActions?.GitDiagnosticsCommand.Execute(null),
            () => legacyActions is not null);
        ShowChangesCommand = new RelayCommand(() => Mode = GitWorkspaceMode.Changes);
        ShowHistoryCommand = new RelayCommand(() => { Mode = GitWorkspaceMode.History; if (History is not null) _ = History.RefreshAsync(); if (Remotes is not null) _ = Remotes.RefreshAsync(); });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand RefreshCommand { get; }
    public ICommand OpenRepositoryCommand { get; }
    public ICommand CloneRepositoryCommand { get; }
    public ICommand InitializeRepositoryCommand { get; }
    public ICommand GitDiagnosticsCommand { get; }
    public ICommand ShowChangesCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public GitChangesViewModel? Changes { get; }
    public GitHistoryViewModel? History { get; }
    public GitBranchesViewModel? Branches { get; }
    public GitRemotesViewModel? Remotes { get; }
    public GitRepositoryState? State => _state;
    public GitWorkspaceMode Mode { get => _mode; set { if (_mode != value) { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeText)); OnPropertyChanged(nameof(ContentText)); } } }
    public string ModeText => Mode == GitWorkspaceMode.History ? "History" : "Changes";
    public string ContentText => Mode == GitWorkspaceMode.History ? "Review repository commits without opening editor documents." : "Review and stage saved working-tree changes.";
    public string RemoteText => Remotes?.SummaryText ?? "Remote state unavailable";
    public string RepositoryText => _state?.Repository.IsRepository == true
        ? _state.Repository.RepositoryRoot ?? "Git repository"
        : _state?.Environment.IsAvailable == false ? "Git unavailable" : "No Git repository open";
    public string BranchText => _state?.Repository.IsRepository == true
        ? _state.Repository.IsDetachedHead ? $"Detached HEAD — {_state.Repository.HeadCommit ?? "unknown"}" : $"Branch: {_state.Repository.CurrentBranch ?? "unknown"}"
        : "Branch: unavailable";
    public string StatusText => _state?.Repository.IsRepository != true
        ? _state?.Repository.Error ?? _state?.Environment.Error ?? "Open a workspace or repository to see Git status."
        : _state.StatusError ?? (_state.HasUnresolvedConflicts
            ? $"{_state.Changes.Count(change => change.IsConflicted)} unresolved conflict(s)"
            : _state.Changes.Count == 0 ? "Working tree clean" : $"{_state.Changes.Count} change(s)");
    public string OperationText => _state?.OperationState is null or GitRepositoryOperationState.None ? string.Empty : $"{_state.OperationState} in progress";
    public string ConflictText => _state?.HasUnresolvedConflicts == true ? "Conflicts require attention" : string.Empty;

    public string RepositoryActionText => _state?.Repository.IsRepository == true
        ? "Switch repository"
        : "Open repository";

    public async Task RefreshAsync()
    {
        _coordinator.RequestRefresh();
        if (Remotes is not null)
        {
            await Remotes.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void Coordinator_StateChanged(object? sender, GitRepositoryState? state)
    {
        _state = state;
        OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(RepositoryText)); OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(OperationText)); OnPropertyChanged(nameof(ConflictText)); OnPropertyChanged(nameof(RemoteText)); OnPropertyChanged(nameof(RepositoryActionText));
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CloneRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (InitializeRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GitDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose() { _coordinator.StateChanged -= Coordinator_StateChanged; Changes?.Dispose(); History?.Dispose(); Branches?.Dispose(); Remotes?.Dispose(); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
