namespace PS7ScriptDesk.Domain.Models;

public enum GitDiffScope { Unstaged, Staged, Commit }

public enum GitDiffLineKind { Context, Added, Removed, FileHeader, HunkHeader, NoNewline }

public sealed record GitDiffLine(GitDiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber)
{
    public string OldText => Kind is GitDiffLineKind.Removed or GitDiffLineKind.Context ? Text : string.Empty;
    public string NewText => Kind is GitDiffLineKind.Added or GitDiffLineKind.Context ? Text : string.Empty;
}

public sealed record GitDiffHunk(int Index, string Header, int StartLineIndex, IReadOnlyList<GitDiffLine> Lines)
{
    public IReadOnlyList<GitDiffAlignedRow> AlignedRows => GitDiffAlignment.Align(Lines);
}

public sealed record GitDiffAlignedRow(
    int? LeftLineNumber,
    string LeftText,
    GitDiffLineKind LeftKind,
    int? RightLineNumber,
    string RightText,
    GitDiffLineKind RightKind,
    int HunkIndex);

internal static class GitDiffAlignment
{
    public static IReadOnlyList<GitDiffAlignedRow> Align(IReadOnlyList<GitDiffLine> lines)
    {
        var rows = new List<GitDiffAlignedRow>();
        var hunkIndex = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind == GitDiffLineKind.HunkHeader) { hunkIndex++; continue; }
            if (line.Kind == GitDiffLineKind.Context)
            {
                rows.Add(new(line.OldLineNumber, line.Text, line.Kind, line.NewLineNumber, line.Text, line.Kind, hunkIndex));
                continue;
            }
            if (line.Kind == GitDiffLineKind.Removed)
            {
                var removed = new List<GitDiffLine>();
                while (i < lines.Count && lines[i].Kind == GitDiffLineKind.Removed) removed.Add(lines[i++]);
                var added = new List<GitDiffLine>();
                while (i < lines.Count && lines[i].Kind == GitDiffLineKind.Added) added.Add(lines[i++]);
                var count = Math.Max(removed.Count, added.Count);
                for (var n = 0; n < count; n++)
                {
                    var old = n < removed.Count ? removed[n] : null;
                    var @new = n < added.Count ? added[n] : null;
                    rows.Add(new(old?.OldLineNumber, old?.Text ?? string.Empty, old?.Kind ?? GitDiffLineKind.Context,
                        @new?.NewLineNumber, @new?.Text ?? string.Empty, @new?.Kind ?? GitDiffLineKind.Context, hunkIndex));
                }
                i--;
                continue;
            }
            if (line.Kind == GitDiffLineKind.Added)
            {
                rows.Add(new(null, string.Empty, GitDiffLineKind.Context, line.NewLineNumber, line.Text, line.Kind, hunkIndex));
            }
        }
        return rows;
    }
}

public sealed record GitDiff(
    string OldPath,
    string NewPath,
    GitDiffScope Scope,
    IReadOnlyList<GitDiffLine> Lines,
    bool IsBinary,
    bool IsUntracked,
    bool IsDeleted,
    bool IsRenamed,
    string? Error = null,
    int? SimilarityIndex = null,
    bool IsLarge = false,
    string? RepositoryRoot = null,
    string? RelativePath = null,
    string? CommitHash = null,
    string? ParentHash = null)
{
    public string DisplayName => NewPath == "/dev/null" ? OldPath : NewPath;
    public bool HasContentChanges => Lines.Any(line => line.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed);
    public bool IsRenameOnly => IsRenamed && !HasContentChanges;
    public string RenameText => IsRenamed ? $"{OldPath} → {NewPath}" : string.Empty;
    public IReadOnlyList<GitDiffHunk> Hunks
    {
        get
        {
            var result = new List<GitDiffHunk>();
            var current = new List<GitDiffLine>();
            var start = 0;
            var index = 0;
            for (var i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Kind == GitDiffLineKind.HunkHeader)
                {
                    if (current.Count > 0) result.Add(new(index++, current[0].Text, start, current));
                    current = new List<GitDiffLine>(); start = i;
                    current.Add(Lines[i]);
                }
                else if (current.Count > 0) current.Add(Lines[i]);
            }
            if (current.Count > 0) result.Add(new(index, current[0].Text, start, current));
            return result;
        }
    }
}
