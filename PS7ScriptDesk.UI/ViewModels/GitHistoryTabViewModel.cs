using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class GitHistoryTabViewModel : EditorTabViewModel
{
    private string _searchText = string.Empty;
    private GitHistoryScope _scope = GitHistoryScope.CurrentBranch;
    private GitCommit? _selectedCommit;
    private GitCommitDetails? _selectedDetails;
    private bool _isLoading;
    private bool _hasMore;
    private string _statusText = "History is ready.";

    public GitHistoryTabViewModel(string repositoryRoot) : base("Repository History", string.Empty)
    {
        RepositoryRoot = repositoryRoot;
        IsReadOnly = true;
    }

    public string RepositoryRoot { get; }
    public bool IsGitHistoryDocument => true;
    public bool IsReadOnly { get; }
    public ObservableCollection<GitCommit> Commits { get; } = new();
    public GitCommit? SelectedCommit { get => _selectedCommit; set { if (_selectedCommit != value) { _selectedCommit = value; OnPropertyChanged(); } } }
    public GitCommitDetails? SelectedDetails { get => _selectedDetails; set { if (_selectedDetails != value) { _selectedDetails = value; OnPropertyChanged(); } } }
    public string SearchText { get => _searchText; set { if (_searchText != value) { _searchText = value ?? string.Empty; OnPropertyChanged(); } } }
    public GitHistoryScope Scope { get => _scope; set { if (_scope != value) { _scope = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScopeText)); } } }
    public string ScopeText => Scope == GitHistoryScope.AllLocalBranches ? "All Local Branches" : "Current Branch";
    public bool IsLoading { get => _isLoading; set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); } } }
    public bool HasMore { get => _hasMore; set { if (_hasMore != value) { _hasMore = value; OnPropertyChanged(); } } }
    public string StatusText { get => _statusText; set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } } }
    protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null) => base.OnPropertyChanged(propertyName);
}
