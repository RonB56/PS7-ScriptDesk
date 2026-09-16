using System.Text.RegularExpressions;
using System.Text;
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
                oldPath = DecodeGitPath(line[12..]);
                renamed = true;
            }
            else if (line.StartsWith("similarity index ", StringComparison.Ordinal) && int.TryParse(line[17..].TrimEnd('%'), out var parsedSimilarity))
            {
                similarity = parsedSimilarity;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                newPath = DecodeGitPath(line[10..]);
                renamed = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = NormalizePath(line[4..]);
                lines.Add(new(GitDiffLineKind.FileHeader, $"--- {oldPath}", null, null));
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = NormalizePath(line[4..]);
                deleted = newPath == "/dev/null";
                lines.Add(new(GitDiffLineKind.FileHeader, $"+++ {newPath}", null, null));
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
        var value = DecodeGitPath(path.Split('\t')[0].Trim());
        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value[2..]
            : value;
    }

    private static string DecodeGitPath(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;

        var bytes = new List<byte>(value.Length);
        var text = new StringBuilder();
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] != '\\')
            {
                text.Append(value[i]);
                continue;
            }

            if (i + 3 < value.Length - 1 && IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
            {
                FlushText();
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 3), 8));
                i += 3;
                continue;
            }

            var escaped = i + 1 < value.Length - 1 ? value[++i] : '\\';
            text.Append(escaped switch { 'n' => '\n', 't' => '\t', 'b' => '\b', 'r' => '\r', _ => escaped });
        }
        FlushText();
        return Encoding.UTF8.GetString(bytes.ToArray());

        void FlushText()
        {
            if (text.Length == 0) return;
            bytes.AddRange(Encoding.UTF8.GetBytes(text.ToString()));
            text.Clear();
        }
    }

    private static bool IsOctal(char value) => value is >= '0' and <= '7';
}
