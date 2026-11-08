using System.Management.Automation.Language;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>Computes immutable fold descriptors from one immutable script snapshot.</summary>
internal sealed class BraceFoldingStrategy
{
    public IReadOnlyList<FoldingRange> CreateFoldings(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        Token[] tokens;
        ParseError[] errors;
        Parser.ParseInput(text, out tokens, out errors);
        cancellationToken.ThrowIfCancellationRequested();

        var foldings = new List<FoldingRange>();
        var openBraces = new Stack<int>();
        var openRegions = new Stack<(int Offset, string Name)>();
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                lineStarts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        var tokenIndex = 0;
        foreach (var token in tokens)
        {
            if ((tokenIndex++ & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var start = token.Extent.StartOffset;
            var end = token.Extent.EndOffset;
            if (start < 0 || end < start || end > text.Length) continue;

            if (token.Kind == TokenKind.LCurly)
            {
                openBraces.Push(start);
                continue;
            }

            if (token.Kind == TokenKind.RCurly)
            {
                if (openBraces.Count == 0) continue;
                var regionStart = openBraces.Pop();
                var regionEnd = end;
                if (IsMultiline(lineStarts, regionStart, regionEnd))
                    foldings.Add(new FoldingRange(regionStart, regionEnd));
                continue;
            }

            if (token.Kind != TokenKind.Comment) continue;
            var lineIndex = FindLineIndex(lineStarts, start);
            var lineStart = lineStarts[lineIndex];
            var lineEnd = FindLineEnd(text, end);
            if (!IsOnlyWhitespace(text, lineStart, start)) continue;

            var comment = text.AsSpan(start, end - start).Trim();
            if (comment.StartsWith("#region", StringComparison.OrdinalIgnoreCase) &&
                (comment.Length == 7 || char.IsWhiteSpace(comment[7])))
            {
                var name = comment.Length > 7 ? comment[7..].Trim().ToString() : string.Empty;
                openRegions.Push((lineStart, name));
            }
            else if (comment.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase) &&
                     (comment.Length == 10 || char.IsWhiteSpace(comment[10])))
            {
                if (openRegions.Count == 0) continue;
                var (regionStart, name) = openRegions.Pop();
                if (!IsMultiline(lineStarts, regionStart, lineEnd)) continue;
                foldings.Add(new FoldingRange(regionStart, lineEnd, string.IsNullOrWhiteSpace(name) ? null : $"#region {name}"));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        foldings.Sort(static (left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return foldings;
    }

    private static int FindLineIndex(IReadOnlyList<int> lineStarts, int offset)
    {
        var low = 0;
        var high = lineStarts.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lineStarts[middle] <= offset) low = middle + 1;
            else high = middle - 1;
        }
        return Math.Max(0, high);
    }

    private static int FindLineEnd(string text, int offset)
    {
        while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++;
        return offset;
    }

    private static bool IsOnlyWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    private static bool IsMultiline(IReadOnlyList<int> lineStarts, int start, int end)
        => FindLineIndex(lineStarts, start) != FindLineIndex(lineStarts, Math.Max(start, end - 1));
}

internal sealed record FoldingRange(int StartOffset, int EndOffset, string? Name = null);
