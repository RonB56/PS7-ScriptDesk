namespace PS7ScriptDesk.Domain.Models;

public sealed record GitFileStatus(
    string FullPath,
    string RelativePath,
    string? OriginalPath,
    char IndexStatus,
    char WorkingTreeStatus,
    bool IsTracked,
    bool IsUntracked,
    bool IsConflicted,
    bool IsRenamed,
    bool IsDeleted,
    bool IsAdded,
    bool IsModified,
    bool IsCopied)
{
    public bool IsStaged => IsTracked && !IsConflicted && IndexStatus is not ' ' and not '.' and not '?';

    public bool HasUnstagedChanges => !IsUntracked && WorkingTreeStatus is not ' ' and not '.' and not '?';

    public string StatusGlyph => IsConflicted
        ? "U"
        : IsUntracked
            ? "?"
            : IsRenamed
                ? "R"
                : IsDeleted
                    ? "D"
                    : IsAdded
                        ? "A"
                        : IsModified
                            ? "M"
                            : "•";
}
