using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.UI.ViewModels;

public sealed class DiffTabViewModel : EditorTabViewModel
{
    private GitDiff _diff;
    private bool _isStale;
    private int _currentHunkIndex = -1;

    public DiffTabViewModel(GitDiff diff, string? repositoryRoot = null, string? relativePath = null)
        : base(BuildTitle(diff), string.Empty)
    {
        _diff = diff with { RepositoryRoot = repositoryRoot ?? diff.RepositoryRoot, RelativePath = relativePath ?? diff.RelativePath };
        IsReadOnly = true;
    }

    public GitDiff Diff => _diff;

    public string? RepositoryRoot => _diff.RepositoryRoot;
    public string RelativePath => _diff.RelativePath ?? _diff.DisplayName;

    public bool IsDiffDocument => true;

    public bool IsReadOnly { get; }

    public bool IsInline { get; set; }

    public bool IsStale
    {
        get => _isStale;
        private set { if (_isStale != value) { _isStale = value; OnPropertyChanged(); OnPropertyChanged(nameof(StaleText)); } }
    }

    public string StaleText => IsStale ? "Diff may be out of date" : string.Empty;
    public int CurrentHunkIndex => _currentHunkIndex;
    public bool CanPreviousChange => _currentHunkIndex > 0;
    public bool CanNextChange => _currentHunkIndex >= 0 && _currentHunkIndex < Diff.Hunks.Count - 1;

    public string ScopeText => Diff.Scope switch
    {
        GitDiffScope.Staged => "Staged Changes",
        GitDiffScope.Commit => $"Commit {ShortCommitHash} Diff",
        _ => "Unstaged Changes"
    };
    public string ShortCommitHash => string.IsNullOrWhiteSpace(Diff.CommitHash) ? "unknown" : Diff.CommitHash.Length <= 7 ? Diff.CommitHash : Diff.CommitHash[..7];

    public string StatusText => Diff.Error ?? (Diff.IsRenameOnly ? $"Renamed ({Diff.SimilarityIndex?.ToString() ?? "?"}% similar)" : Diff.IsBinary ? "Binary file changed. Text diff is not available." : Diff.IsUntracked ? "New file vs working tree" : Diff.IsDeleted ? "Deleted file" : Diff.Lines.Count == 0 ? "No differences remain." : "Ready for review");

    public void ReplaceDiff(GitDiff diff)
    {
        _diff = diff;
        _currentHunkIndex = diff.Hunks.Count == 0 ? -1 : Math.Min(_currentHunkIndex < 0 ? 0 : _currentHunkIndex, diff.Hunks.Count - 1);
        IsStale = false;
        OnPropertyChanged(nameof(Diff)); OnPropertyChanged(nameof(RepositoryRoot)); OnPropertyChanged(nameof(RelativePath));
        OnPropertyChanged(nameof(ScopeText)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StaleText));
        OnPropertyChanged(nameof(CanPreviousChange)); OnPropertyChanged(nameof(CanNextChange)); OnPropertyChanged(nameof(CurrentHunkIndex));
    }

    public void MarkStale() => IsStale = true;
    public bool MovePreviousChange() => MoveToChange(_currentHunkIndex - 1);
    public bool MoveNextChange() => MoveToChange(_currentHunkIndex + 1);

    private bool MoveToChange(int index)
    {
        if (index < 0 || index >= Diff.Hunks.Count) return false;
        _currentHunkIndex = index;
        OnPropertyChanged(nameof(CurrentHunkIndex)); OnPropertyChanged(nameof(CanPreviousChange)); OnPropertyChanged(nameof(CanNextChange));
        return true;
    }

    private static string BuildTitle(GitDiff diff)
    {
        var name = Path.GetFileName(diff.DisplayName);
        return diff.Scope == GitDiffScope.Commit
            ? $"{name} — Commit {(string.IsNullOrWhiteSpace(diff.CommitHash) ? "unknown" : diff.CommitHash[..Math.Min(7, diff.CommitHash.Length)])} Diff"
            : $"{name} — {diff.Scope} Diff";
    }
}
