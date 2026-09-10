using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public static class GitHistoryParser
{
    private const char RecordSeparator = '\u001e';
    public static IReadOnlyList<GitCommit> ParseCommits(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\r', '\n').Split('\0');
            if (fields.Length < 10 || string.IsNullOrWhiteSpace(fields[0])) continue;
            DateTimeOffset? ParseDate(string value) => DateTimeOffset.TryParse(value, out var date) ? date : null;
            var body = fields[8];
            if (body.StartsWith(fields[7], StringComparison.Ordinal)) body = body[fields[7].Length..].TrimStart('\r', '\n');
            commits.Add(new(fields[0], fields[1], fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[3], fields[4],
                ParseDate(fields[5]), ParseDate(fields[6]), fields[7], body, fields[9]));
        }
        return commits;
    }

    public static IReadOnlyList<GitCommitFileChange> ParseNameStatus(string output)
    {
        var values = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<GitCommitFileChange>();
        for (var i = 0; i < values.Length; i++)
        {
            var status = values[i];
            if (status.StartsWith('R') && i + 2 < values.Length) changes.Add(new(status, values[++i], values[++i], IsRename: true));
            else if (i + 1 < values.Length) changes.Add(new(status, values[++i], values[i]));
        }
        return changes;
    }
}
