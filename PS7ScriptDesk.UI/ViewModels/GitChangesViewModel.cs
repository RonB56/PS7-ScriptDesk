using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitChangesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator;
    private readonly MainWindowViewModel _legacyActions;
    private readonly SourceControlViewModel _projection = new();
    private CancellationTokenSource? _diffCancellation;
    private int _diffGeneration;
    private GitFileStatus? _selectedStatus;
    private GitDiff? _selectedDiff;
    private bool _isDiffLoading;
    private string _diffStatusText = "Select a file to review its saved Git change.";

    public GitChangesViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel legacyActions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _legacyActions = legacyActions ?? throw new ArgumentNullException(nameof(legacyActions));
        coordinator.StateChanged += Coordinator_StateChanged;
        StageCommand = new RelayCommand(parameter => _ = StageAsync(parameter as GitFileStatus), parameter => CanStage(parameter as GitFileStatus));
        StageAllCommand = new RelayCommand(() => _ = _legacyActions.StageAllAsync());
        UnstageCommand = new RelayCommand(parameter => _ = UnstageAsync(parameter as GitFileStatus), parameter => CanUnstage(parameter as GitFileStatus));
        UnstageAllCommand = new RelayCommand(() => _ = _legacyActions.UnstageAllAsync());
        DiscardCommand = new RelayCommand(parameter => _ = _legacyActions.DiscardFileAsync(parameter as GitFileStatus));
        OpenFileCommand = new RelayCommand(parameter => OpenFile(parameter as GitFileStatus));
        CommitCommand = _legacyActions.CommitCommand;
        ApplyState(coordinator.CurrentState);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SourceControlGroupViewModel> Groups => _projection.Groups;
    public string FilterText { get => _projection.FilterText; set { _projection.FilterText = value; OnPropertyChanged(); OnPropertyChanged(nameof(Groups)); } }
    public string StatusFilter { get => _projection.StatusFilter; set { _projection.StatusFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(Groups)); } }
    public IReadOnlyList<string> StatusFilters => _projection.StatusFilters;
    public GitFileStatus? SelectedStatus { get => _selectedStatus; private set { if (_selectedStatus != value) { _selectedStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedPathText)); OnPropertyChanged(nameof(SelectedStatusText)); RaiseActions(); } } }
    public string SelectedPathText => SelectedStatus?.RelativePath ?? "No file selected";
    public string SelectedStatusText => SelectedStatus is null ? "Select a changed file to review it here." : SelectedStatus.IsConflicted ? "Unresolved Git conflict. Open the file and resolve it manually before staging." : SelectedStatus.IsStaged ? "Staged change" : SelectedStatus.IsUntracked ? "Untracked file" : "Working-tree change";
    public GitDiff? SelectedDiff { get => _selectedDiff; private set { _selectedDiff = value; OnPropertyChanged(); OnPropertyChanged(nameof(DiffLines)); } }
    public IReadOnlyList<GitDiffLine> DiffLines => SelectedDiff?.Lines ?? Array.Empty<GitDiffLine>();
    public bool IsDiffLoading { get => _isDiffLoading; private set { if (_isDiffLoading != value) { _isDiffLoading = value; OnPropertyChanged(); } } }
    public string DiffStatusText { get => _diffStatusText; private set { if (_diffStatusText != value) { _diffStatusText = value; OnPropertyChanged(); } } }
    public string CommitMessage { get => _legacyActions.CommitMessage; set { _legacyActions.CommitMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(CommitDisabledReason)); } }
    public string CommitDisabledReason => _legacyActions.CommitDisabledReason;
    public ICommand StageCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand CommitCommand { get; }

    public void SelectFile(GitFileStatus? status)
    {
        SelectedStatus = status;
        _ = LoadDiffAsync(status);
    }

    private async Task LoadDiffAsync(GitFileStatus? status)
    {
        var generation = Interlocked.Increment(ref _diffGeneration);
        _diffCancellation?.Cancel(); _diffCancellation?.Dispose();
        _diffCancellation = new CancellationTokenSource();
        SelectedDiff = null;
        if (status is null) { DiffStatusText = "Select a file to review its saved Git change."; return; }
        if (status.IsConflicted) { DiffStatusText = "Conflict contents require manual resolution; open the file to review and edit it."; return; }
        IsDiffLoading = true; DiffStatusText = "Loading saved Git diff…";
        try
        {
            var scope = status.IsStaged && !status.HasUnstagedChanges ? GitDiffScope.Staged : GitDiffScope.Unstaged;
            var diff = await _coordinator.GitService.GetDiffAsync(_coordinator.CurrentState?.Repository.RepositoryRoot ?? string.Empty, status.RelativePath, scope, status.IsUntracked, false, _diffCancellation.Token).ConfigureAwait(false);
            if (generation != _diffGeneration || _diffCancellation.IsCancellationRequested) return;
            SelectedDiff = diff;
            DiffStatusText = diff.Error ?? (diff.IsBinary ? "Binary file; text diff is unavailable." : diff.IsLarge ? "Large diff; use the existing diff viewer for full review." : diff.Lines.Count == 0 ? "No differences remain." : "Saved Git diff ready for review.");
        }
        catch (OperationCanceledException) when (_diffCancellation.IsCancellationRequested) { }
        finally { if (generation == _diffGeneration) IsDiffLoading = false; }
    }

    private void ApplyState(GitRepositoryState? state)
    {
        _projection.ApplyState(state, state?.WorkspacePath, null);
        OnPropertyChanged(nameof(Groups)); OnPropertyChanged(nameof(CommitDisabledReason)); RaiseActions();
    }

    private void Coordinator_StateChanged(object? sender, GitRepositoryState? state) => ApplyState(state);
    private bool CanStage(GitFileStatus? status) => status is not null && (status.IsUntracked || status.HasUnstagedChanges) && !status.IsConflicted;
    private bool CanUnstage(GitFileStatus? status) => status?.IsStaged == true;
    private Task StageAsync(GitFileStatus? status) => _legacyActions.StageFileAsync(status);
    private Task UnstageAsync(GitFileStatus? status) => _legacyActions.UnstageFileAsync(status);
    private void OpenFile(GitFileStatus? status) { if (status is not null) _legacyActions.TryOpenFileFromPath(status.FullPath, out _); }
    private void RaiseActions() { if (StageCommand is RelayCommand stage) stage.RaiseCanExecuteChanged(); if (UnstageCommand is RelayCommand unstage) unstage.RaiseCanExecuteChanged(); }
    public void Dispose() { _coordinator.StateChanged -= Coordinator_StateChanged; _diffCancellation?.Cancel(); _diffCancellation?.Dispose(); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
