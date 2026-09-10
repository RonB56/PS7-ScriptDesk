using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitHistoryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGitWorkspaceCoordinator _coordinator;
    private readonly MainWindowViewModel _legacyActions;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    private string _searchText = string.Empty;
    private GitHistoryScope _scope = GitHistoryScope.CurrentBranch;
    private GitCommit? _selectedCommit;
    private GitCommitDetails? _details;
    private GitCommitFileChange? _selectedFile;
    private GitDiff? _selectedDiff;
    private bool _isLoading;
    private bool _hasMore;
    private string _statusText = "Select History to load repository commits.";

    public GitHistoryViewModel(IGitWorkspaceCoordinator coordinator, MainWindowViewModel legacyActions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _legacyActions = legacyActions ?? throw new ArgumentNullException(nameof(legacyActions));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync(), () => HasMore && !IsLoading);
        CopyHashCommand = new RelayCommand(CopyHash, () => SelectedCommit is not null);
        coordinator.StateChanged += Coordinator_StateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? CopyHashRequested;
    public ObservableCollection<GitCommit> Commits { get; } = new();
    public ObservableCollection<GitCommitFileChange> Files { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand CopyHashCommand { get; }
    public string SearchText { get => _searchText; set { if (_searchText != value) { _searchText = value ?? string.Empty; OnPropertyChanged(); if (_coordinator.CurrentState?.Repository.IsRepository == true) _ = RefreshAsync(); } } }
    public GitHistoryScope Scope { get => _scope; set { if (_scope != value) { _scope = value; OnPropertyChanged(); _ = RefreshAsync(); } } }
    public GitCommit? SelectedCommit { get => _selectedCommit; private set { _selectedCommit = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedHashText)); OnPropertyChanged(nameof(SelectedCommitText)); RaiseCommands(); } }
    public GitCommitDetails? Details { get => _details; private set { _details = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedCommitText)); } }
    public GitCommitFileChange? SelectedFile { get => _selectedFile; private set { _selectedFile = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedFileText)); } }
    public GitDiff? SelectedDiff { get => _selectedDiff; private set { _selectedDiff = value; OnPropertyChanged(); OnPropertyChanged(nameof(DiffLines)); } }
    public IReadOnlyList<GitDiffLine> DiffLines => SelectedDiff?.Lines ?? Array.Empty<GitDiffLine>();
    public bool IsLoading { get => _isLoading; private set { _isLoading = value; OnPropertyChanged(); RaiseCommands(); } }
    public bool HasMore { get => _hasMore; private set { _hasMore = value; OnPropertyChanged(); RaiseCommands(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
    public string SelectedHashText => SelectedCommit?.Hash ?? "No commit selected";
    public string SelectedCommitText => Details is null ? (SelectedCommit?.Subject ?? "Select a commit to review its details.") : $"{Details.Commit.Subject}\n{Details.Commit.AuthorName}  {Details.Commit.AuthorDate:g}\nParents: {string.Join(", ", Details.Commit.ParentHashes)}\n{Details.Commit.Body}";
    public string SelectedFileText => SelectedFile?.DisplayName ?? "Select a changed file to review its historical diff.";

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var root = _coordinator.CurrentState?.Repository.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(root)) { StatusText = "History is unavailable because no Git repository is open."; return; }
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCancellation.Token;
        var generation = Interlocked.Increment(ref _generation);
        IsLoading = true; StatusText = "Loading repository history…"; Commits.Clear(); Files.Clear(); SelectedCommit = null; Details = null; SelectedFile = null; SelectedDiff = null;
        try
        {
            var page = await _coordinator.GitService.GetHistoryPageAsync(root, Scope, 0, 100, SearchText, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            foreach (var commit in page.Commits) Commits.Add(commit);
            HasMore = page.HasMore; StatusText = page.Error ?? $"{Commits.Count} commit(s) loaded.";
            DeveloperDiagnostics.LogInfo("Git", "Dedicated history page loaded.", new Dictionary<string, object?> { ["count"] = page.Commits.Count, ["scope"] = Scope.ToString(), ["searchLength"] = SearchText.Length });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (generation == _generation) IsLoading = false; }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        var root = _coordinator.CurrentState?.Repository.RepositoryRoot;
        if (!HasMore || IsLoading || string.IsNullOrWhiteSpace(root)) return;
        var generation = _generation; IsLoading = true;
        try
        {
            var page = await _coordinator.GitService.GetHistoryPageAsync(root, Scope, Commits.Count, 100, SearchText, cancellationToken);
            if (generation != _generation || cancellationToken.IsCancellationRequested) return;
            foreach (var commit in page.Commits) Commits.Add(commit);
            HasMore = page.HasMore; StatusText = page.Error ?? $"{Commits.Count} commit(s) loaded.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { if (generation == _generation) IsLoading = false; }
    }

    public async Task SelectCommitAsync(GitCommit? commit, CancellationToken cancellationToken = default)
    {
        SelectedCommit = commit; Details = null; Files.Clear(); SelectedFile = null; SelectedDiff = null;
        var root = _coordinator.CurrentState?.Repository.RepositoryRoot;
        if (commit is null || string.IsNullOrWhiteSpace(root)) return;
        var generation = Interlocked.Increment(ref _generation); IsLoading = true;
        try
        {
            var details = await _coordinator.GitService.GetCommitDetailsAsync(root, commit.Hash, cancellationToken);
            if (generation != _generation || cancellationToken.IsCancellationRequested) return;
            Details = details; foreach (var file in details.Files) Files.Add(file);
            DeveloperDiagnostics.LogInfo("Git", "Dedicated history commit selected.", new Dictionary<string, object?> { ["hash"] = commit.ShortHash, ["fileCount"] = details.Files.Count });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { if (generation == _generation) IsLoading = false; }
    }

    public async Task SelectFileAsync(GitCommitFileChange? file, CancellationToken cancellationToken = default)
    {
        SelectedFile = file; SelectedDiff = null;
        var root = _coordinator.CurrentState?.Repository.RepositoryRoot; var commit = SelectedCommit;
        if (file is null || commit is null || string.IsNullOrWhiteSpace(root)) return;
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var path = file.Status.StartsWith("D", StringComparison.OrdinalIgnoreCase) ? file.OldPath : file.NewPath;
            var diff = await _coordinator.GitService.GetCommitDiffAsync(root, commit.Hash, path, commit.ParentHashes.FirstOrDefault(), cancellationToken);
            if (generation != _generation || cancellationToken.IsCancellationRequested) return;
            SelectedDiff = diff;
            DeveloperDiagnostics.LogInfo("Git", "Dedicated historical diff selected.", new Dictionary<string, object?> { ["hash"] = commit.ShortHash, ["path"] = path, ["lineCount"] = diff.Lines.Count });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CopyHash() { if (SelectedCommit is not null) CopyHashRequested?.Invoke(this, SelectedCommit.Hash); }
    private void Coordinator_StateChanged(object? sender, GitRepositoryState? state) { if (state?.Repository.RepositoryRoot is null) { Commits.Clear(); Files.Clear(); SelectedDiff = null; } }
    private void RaiseCommands() { (LoadMoreCommand as RelayCommand)?.RaiseCanExecuteChanged(); (CopyHashCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    public void Dispose() { _coordinator.StateChanged -= Coordinator_StateChanged; _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
