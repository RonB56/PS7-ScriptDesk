using System.Text.RegularExpressions;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public static partial class GitDiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@.*$")]
    private static partial Regex HunkRegex();

    public static GitDiff Parse(string output, string path, GitDiffScope scope, bool isUntracked = false)
    {
        var lines = new List<GitDiffLine>();
        var oldPath = path;
        var newPath = path;
        var oldLine = 0;
        var newLine = 0;
        var binary = output.Contains("Binary files", StringComparison.OrdinalIgnoreCase);
        var renamed = false;
        var deleted = false;
        int? similarity = null;

        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                oldPath = line[12..];
                renamed = true;
            }
            else if (line.StartsWith("similarity index ", StringComparison.Ordinal) && int.TryParse(line[17..].TrimEnd('%'), out var parsedSimilarity))
            {
                similarity = parsedSimilarity;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                newPath = line[10..];
                renamed = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = NormalizePath(line[4..]);
                lines.Add(new(GitDiffLineKind.FileHeader, line, null, null));
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = NormalizePath(line[4..]);
                deleted = newPath == "/dev/null";
                lines.Add(new(GitDiffLineKind.FileHeader, line, null, null));
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = HunkRegex().Match(line);
                if (match.Success)
                {
                    oldLine = int.Parse(match.Groups[1].Value);
                    newLine = int.Parse(match.Groups[3].Value);
                }
                lines.Add(new(GitDiffLineKind.HunkHeader, line, oldLine, newLine));
            }
            else if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
            {
                lines.Add(new(GitDiffLineKind.NoNewline, line, null, null));
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                lines.Add(new(GitDiffLineKind.Added, line[1..], null, newLine++));
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                lines.Add(new(GitDiffLineKind.Removed, line[1..], oldLine++, null));
            }
            else if (line.StartsWith(' '))
            {
                lines.Add(new(GitDiffLineKind.Context, line[1..], oldLine++, newLine++));
            }
        }

        return new(oldPath, newPath, scope, lines, binary, isUntracked, deleted, renamed, null, similarity);
    }

    private static string NormalizePath(string path)
    {
        var value = path.Split('\t')[0].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value[2..]
            : value;
    }
}
