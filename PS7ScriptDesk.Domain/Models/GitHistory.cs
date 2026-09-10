namespace PS7ScriptDesk.Domain.Models;

public enum GitHistoryScope { CurrentBranch, AllLocalBranches }

public sealed record GitCommit(
    string Hash,
    string ShortHash,
    IReadOnlyList<string> ParentHashes,
    string AuthorName,
    string? AuthorEmail,
    DateTimeOffset? AuthorDate,
    DateTimeOffset? CommitterDate,
    string Subject,
    string Body,
    string Decoration);

public sealed record GitCommitFileChange(
    string Status,
    string OldPath,
    string NewPath,
    int? Additions = null,
    int? Deletions = null,
    bool IsRename = false,
    bool IsBinary = false)
{
    public string DisplayName => IsRename ? $"{OldPath} → {NewPath}" : NewPath;
}

public sealed record GitCommitDetails(GitCommit Commit, IReadOnlyList<GitCommitFileChange> Files, string? Error = null);

public sealed record GitHistoryPage(IReadOnlyList<GitCommit> Commits, bool HasMore, string? Error = null);
