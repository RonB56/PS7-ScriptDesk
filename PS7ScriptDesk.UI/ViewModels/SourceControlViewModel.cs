using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.UI.ViewModels;

public enum SourceControlScope
{
    Repository,
    CurrentFolder,
    CurrentFile
}

public sealed class SourceControlGroupViewModel
{
    public SourceControlGroupViewModel(string title, IEnumerable<GitFileStatus> items)
    {
        Title = title;
        Items = new ObservableCollection<GitFileStatus>(items);
    }

    public string Title { get; }

    public ObservableCollection<GitFileStatus> Items { get; }

    public int Count => Items.Count;

    public string HeaderText => $"{Title} ({Count})";

    public bool IsExpanded { get; set; } = true;
}

public sealed class SourceControlViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<SourceControlScope> _scopes =
        new[] { SourceControlScope.Repository, SourceControlScope.CurrentFolder, SourceControlScope.CurrentFile };
    private IReadOnlyList<GitFileStatus> _allChanges = Array.Empty<GitFileStatus>();
    private string _filterText = string.Empty;
    private string _statusFilter = "All";
    private SourceControlScope _scope = SourceControlScope.Repository;
    private string _repositoryText = "No repository is open.";
    private string _branchText = "Branch: unavailable";
    private string _statusText = "No workspace is open.";
    private string? _currentFolderPath;
    private string? _currentFilePath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SourceControlGroupViewModel> Groups { get; } = new();

    public IReadOnlyList<SourceControlScope> Scopes => _scopes;

    public IReadOnlyList<string> ScopeOptions { get; } = new[] { "Repository", "Current Folder", "Current File" };

    public IReadOnlyList<string> StatusFilters { get; } = new[]
    {
        "All", "Modified", "Added", "Deleted", "Renamed", "Untracked", "Staged", "Conflicts"
    };

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText == value)
            {
                return;
            }

            _filterText = value ?? string.Empty;
            OnPropertyChanged();
            RebuildGroups();
        }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (string.Equals(_statusFilter, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusFilter = value ?? "All";
            OnPropertyChanged();
            RebuildGroups();
        }
    }

    public SourceControlScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value)
            {
                return;
            }

            _scope = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScopeText));
            RebuildGroups();
        }
    }

    public string ScopeText => Scope switch
    {
        SourceControlScope.CurrentFolder => "Current Folder",
        SourceControlScope.CurrentFile => "Current File",
        _ => "Repository"
    };

    public string ScopeSelection
    {
        get => ScopeText;
        set
        {
            Scope = value switch
            {
                "Current Folder" => SourceControlScope.CurrentFolder,
                "Current File" => SourceControlScope.CurrentFile,
                _ => SourceControlScope.Repository
            };
        }
    }

    public string RepositoryText
    {
        get => _repositoryText;
        private set { if (_repositoryText != value) { _repositoryText = value; OnPropertyChanged(); } }
    }

    public string BranchText
    {
        get => _branchText;
        private set { if (_branchText != value) { _branchText = value; OnPropertyChanged(); } }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public bool HasChanges => _allChanges.Count > 0;

    public string ChangeSummaryText => $"{_allChanges.Count} {(_allChanges.Count == 1 ? "change" : "changes")}";

    public void ApplyState(GitRepositoryState? state, string? currentFolderPath, string? currentFilePath)
    {
        _currentFolderPath = NormalizePath(currentFolderPath);
        _currentFilePath = NormalizePath(currentFilePath);
        _allChanges = state?.Changes ?? Array.Empty<GitFileStatus>();

        if (state?.Environment.IsAvailable != true)
        {
            RepositoryText = "Git unavailable";
            BranchText = "Branch: unavailable";
            StatusText = state?.Environment.Error ?? "Git could not be found.";
        }
        else if (state.Repository.IsRepository)
        {
            RepositoryText = state.Repository.RepositoryRoot ?? "Repository detected";
            BranchText = state.Repository.IsDetachedHead
                ? $"Detached HEAD — {state.Repository.HeadCommit ?? "unknown"}"
                : $"Branch: {state.Repository.CurrentBranch ?? "unknown"}";
            StatusText = state.StatusError is not null
                ? "Unable to refresh Git status."
                : state.OperationState is not GitRepositoryOperationState.None
                    ? $"Git {state.OperationState} in progress{(_allChanges.Any(change => change.IsConflicted) ? ". Resolve conflicts before committing." : ".")}"
                : _allChanges.Count == 0 ? "No changes. Working tree is clean." : $"{_allChanges.Count} change(s) detected.";
        }
        else
        {
            RepositoryText = "No Git repository is open.";
            BranchText = "Branch: unavailable";
            StatusText = state?.Repository.Error ?? "The current workspace is not inside a Git repository.";
        }

        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeSummaryText));
        RebuildGroups();
    }

    public void SetCurrentFile(string? currentFilePath)
    {
        _currentFilePath = NormalizePath(currentFilePath);
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        var filtered = _allChanges
            .Where(IsInScope)
            .Where(MatchesStatusFilter)
            .Where(MatchesTextFilter)
            .ToArray();

        Groups.Clear();
        AddGroup("CHANGES", filtered.Where(change => change.HasUnstagedChanges && !change.IsConflicted));
        AddGroup("STAGED CHANGES", filtered.Where(change => change.IsStaged));
        AddGroup("UNTRACKED", filtered.Where(change => change.IsUntracked));
        AddGroup("CONFLICTS", filtered.Where(change => change.IsConflicted));
        OnPropertyChanged(nameof(Groups));
    }

    private void AddGroup(string title, IEnumerable<GitFileStatus> items)
    {
        var groupItems = items.DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (groupItems.Length > 0)
        {
            Groups.Add(new SourceControlGroupViewModel(title, groupItems));
        }
    }

    private bool IsInScope(GitFileStatus change)
        => Scope switch
        {
            SourceControlScope.CurrentFile => _currentFilePath is not null && PathsEqual(change.FullPath, _currentFilePath),
            SourceControlScope.CurrentFolder => _currentFolderPath is not null && IsUnderFolder(change.FullPath, _currentFolderPath),
            _ => true
        };

    private bool MatchesStatusFilter(GitFileStatus change)
        => StatusFilter switch
        {
            "Modified" => change.IsModified,
            "Added" => change.IsAdded,
            "Deleted" => change.IsDeleted,
            "Renamed" => change.IsRenamed,
            "Untracked" => change.IsUntracked,
            "Staged" => change.IsStaged,
            "Conflicts" => change.IsConflicted,
            _ => true
        };

    private bool MatchesTextFilter(GitFileStatus change)
        => string.IsNullOrWhiteSpace(FilterText) ||
           change.RelativePath.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderFolder(string path, string folder)
        => PathsEqual(path, folder) || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return path; }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
