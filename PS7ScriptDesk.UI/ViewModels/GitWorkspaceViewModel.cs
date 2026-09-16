using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;
using PS7ScriptDesk.UI.Services;

namespace PS7ScriptDesk.UI.ViewModels;

public enum GitWorkspaceMode { Changes, History }

public sealed class GitWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator;
    private readonly MainWindowViewModel? _legacyActions;
    private GitRepositoryState? _state;
    private GitWorkspaceMode _mode = GitWorkspaceMode.Changes;
    private GitRepositoryChangeMonitor? _repositoryChangeMonitor;

    public GitWorkspaceViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel? legacyActions = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _legacyActions = legacyActions;
        _state = coordinator.CurrentState;
        Changes = legacyActions is null ? null : new GitChangesViewModel(coordinator, legacyActions);
        History = legacyActions is null ? null : new GitHistoryViewModel(coordinator, legacyActions);
        Branches = legacyActions is null ? null : new GitBranchesViewModel(coordinator, legacyActions);
        Remotes = legacyActions is null ? null : new GitRemotesViewModel(coordinator, legacyActions);
        if (Branches is not null && coordinator.CurrentBranchState is null && !string.IsNullOrWhiteSpace(legacyActions?.GitRepositoryRoot))
        {
            DeveloperDiagnostics.LogInfo("Git", "Git Workspace requested the initial branch snapshot.", new Dictionary<string, object?>
            {
                ["repositoryRoot"] = legacyActions.GitRepositoryRoot
            });
            _ = legacyActions.RefreshBranchesAsync();
        }
        coordinator.StateChanged += Coordinator_StateChanged;
        if (_legacyActions is not null) _legacyActions.PropertyChanged += LegacyActions_PropertyChanged;
        UpdateRepositoryChangeMonitor(_state);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        OpenRepositoryCommand = new RelayCommand(
            () => legacyActions?.OpenRepositoryFolderCommand.Execute(null),
            () => legacyActions?.OpenRepositoryFolderCommand.CanExecute(null) == true);
        CloneRepositoryCommand = new RelayCommand(
            () => legacyActions?.CloneRepositoryCommand.Execute(null),
            () => legacyActions?.CloneRepositoryCommand.CanExecute(null) == true);
        InitializeRepositoryCommand = new AsyncRelayCommand(
            async () =>
            {
                if (legacyActions is not null)
                    await legacyActions.InitializeRepositoryAsync().ConfigureAwait(false);
            },
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
    public MainWindowViewModel? LegacyActions => _legacyActions;
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
        ? _state?.Environment.IsAvailable == false ? "Git is unavailable" : "No Git repository detected."
            : _state.StatusError ?? (_state.HasUnresolvedConflicts
            ? $"{_state.Changes.Count(change => change.IsConflicted)} unresolved conflict(s)"
            : _state.Changes.Count == 0 ? "Working tree clean" : $"{_state.Changes.Count} change(s)");
    public string ActionStatusText => _legacyActions?.StatusText ?? string.Empty;
    public bool IsGitMutationInProgress => _legacyActions?.IsGitMutationInProgress == true;
    public string GitOperationText => _legacyActions?.GitOperationText ?? GitOperationPresentation.GetBusyText(GitOperationKind.None);
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
        UpdateRepositoryChangeMonitor(state);
        OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(RepositoryText)); OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(OperationText)); OnPropertyChanged(nameof(ConflictText)); OnPropertyChanged(nameof(RemoteText)); OnPropertyChanged(nameof(RepositoryActionText));
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CloneRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (InitializeRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GitDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void UpdateRepositoryChangeMonitor(GitRepositoryState? state)
    {
        var root = _legacyActions is not null && state?.Repository.IsRepository == true
            ? state.Repository.WorkingTreeRoot
            : null;
        if (string.Equals(root, _repositoryChangeMonitorRoot, StringComparison.OrdinalIgnoreCase))
            return;

        _repositoryChangeMonitor?.Dispose();
        _repositoryChangeMonitor = null;
        _repositoryChangeMonitorRoot = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || _legacyActions is null)
            return;

        try
        {
            _repositoryChangeMonitor = new GitRepositoryChangeMonitor(
                root,
                _legacyActions.RefreshGitRepositoryForExternalChangeAsync);
            _repositoryChangeMonitorRoot = root;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogWarning("Git", "Git Workspace could not attach the repository change monitor.", new Dictionary<string, object?>
            {
                ["repositoryRoot"] = root,
                ["exception"] = ex.GetType().Name
            });
        }
    }

    private void LegacyActions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.StatusText) or null) OnPropertyChanged(nameof(ActionStatusText));
        if (e.PropertyName is nameof(MainWindowViewModel.IsGitMutationInProgress) or null)
        {
            OnPropertyChanged(nameof(IsGitMutationInProgress));
            (OpenRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CloneRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (InitializeRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        if (e.PropertyName is nameof(MainWindowViewModel.GitOperationText) or nameof(MainWindowViewModel.CurrentGitOperation) or null) OnPropertyChanged(nameof(GitOperationText));
    }

    private string? _repositoryChangeMonitorRoot;

    public void Dispose() { _repositoryChangeMonitor?.Dispose(); _repositoryChangeMonitor = null; _repositoryChangeMonitorRoot = null; _coordinator.StateChanged -= Coordinator_StateChanged; if (_legacyActions is not null) _legacyActions.PropertyChanged -= LegacyActions_PropertyChanged; Changes?.Dispose(); History?.Dispose(); Branches?.Dispose(); Remotes?.Dispose(); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
