using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public static class GitStatusParser
{
    public static IReadOnlyList<GitFileStatus> Parse(string? porcelainV2ZeroDelimitedOutput, string repositoryRoot)
    {
        if (string.IsNullOrEmpty(porcelainV2ZeroDelimitedOutput) || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return Array.Empty<GitFileStatus>();
        }

        var root = Path.GetFullPath(repositoryRoot);
        var records = porcelainV2ZeroDelimitedOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var statuses = new List<GitFileStatus>(records.Length);

        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("# ", StringComparison.Ordinal) || record.Length == 0)
            {
                continue;
            }

            if (record[0] is '?' or '!')
            {
                var relativePath = record.Length > 2 ? record[2..] : string.Empty;
                if (record[0] == '?')
                {
                    statuses.Add(CreateStatus(root, relativePath, null, ' ', '?', isTracked: false, isUntracked: true, isConflicted: false));
                }

                continue;
            }

            if (record[0] == '1')
            {
                var fields = record.Split(' ', 9, StringSplitOptions.None);
                if (fields.Length == 9 && TryReadXY(fields[1], out var indexStatus, out var workingTreeStatus))
                {
                    statuses.Add(CreateStatus(root, fields[8], null, indexStatus, workingTreeStatus, isTracked: true, isUntracked: false, isConflicted: false));
                }

                continue;
            }

            if (record[0] == '2')
            {
                var fields = record.Split(' ', 10, StringSplitOptions.None);
                if (fields.Length == 10 && index + 1 < records.Length && TryReadXY(fields[1], out var indexStatus, out var workingTreeStatus))
                {
                    var originalPath = records[++index];
                    statuses.Add(CreateStatus(root, fields[9], originalPath, indexStatus, workingTreeStatus, isTracked: true, isUntracked: false, isConflicted: false));
                }

                continue;
            }

            if (record[0] == 'u')
            {
                var fields = record.Split(' ', 11, StringSplitOptions.None);
                if (fields.Length == 11 && TryReadXY(fields[1], out var indexStatus, out var workingTreeStatus))
                {
                    statuses.Add(CreateStatus(root, fields[10], null, indexStatus, workingTreeStatus, isTracked: true, isUntracked: false, isConflicted: true));
                }
            }
        }

        return statuses;
    }

    private static GitFileStatus CreateStatus(
        string repositoryRoot,
        string relativePath,
        string? originalPath,
        char indexStatus,
        char workingTreeStatus,
        bool isTracked,
        bool isUntracked,
        bool isConflicted)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, normalizedRelativePath));
        var isRenamed = indexStatus == 'R' || workingTreeStatus == 'R';
        var isCopied = indexStatus == 'C' || workingTreeStatus == 'C';
        var isDeleted = indexStatus == 'D' || workingTreeStatus == 'D';
        var isAdded = indexStatus == 'A' || workingTreeStatus == 'A' || isUntracked;
        var isModified = indexStatus == 'M' || workingTreeStatus == 'M' || indexStatus == 'T' || workingTreeStatus == 'T';

        return new GitFileStatus(
            fullPath,
            relativePath,
            string.IsNullOrWhiteSpace(originalPath) ? null : originalPath,
            indexStatus,
            workingTreeStatus,
            isTracked,
            isUntracked,
            isConflicted,
            isRenamed,
            isDeleted,
            isAdded,
            isModified,
            isCopied);
    }

    private static bool TryReadXY(string value, out char indexStatus, out char workingTreeStatus)
    {
        if (value.Length >= 2)
        {
            indexStatus = value[0];
            workingTreeStatus = value[1];
            return true;
        }

        indexStatus = ' ';
        workingTreeStatus = ' ';
        return false;
    }
}
