using System.Management.Automation.Language;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>Builds PowerShell folding candidates from parser-produced ASTs and token extents.</summary>
internal sealed class BraceFoldingStrategy
{
    public IReadOnlyList<FoldingRange> CreateFoldings(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var ast = Parser.ParseInput(text, out Token[] tokens, out _);
        return CreateFoldings(ast, tokens, text, cancellationToken);
    }

    internal IReadOnlyList<FoldingRange> CreateFoldings(
        ScriptBlockAst? rootAst,
        IReadOnlyList<Token> tokens,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var lineStarts = BuildLineStarts(text, cancellationToken);
        var matchingBraces = BuildBraceMatches(tokens, text.Length, cancellationToken);
        var ranges = new Dictionary<(int Start, int End), FoldingRange>();

        if (rootAst is not null)
        {
            var nodes = rootAst.FindAll(static node =>
                node is FunctionDefinitionAst or TypeDefinitionAst or FunctionMemberAst or IfStatementAst or
                    SwitchStatementAst or LoopStatementAst or TryStatementAst or ScriptBlockAst,
                searchNestedScriptBlocks: true);

            var visited = 0;
            foreach (var node in nodes)
            {
                if ((visited++ & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                switch (node)
                {
                    case FunctionDefinitionAst function:
                        AddBlock(function.Body.Extent, $"function {function.Name}");
                        break;
                    case TypeDefinitionAst type when type.IsClass || type.IsInterface:
                        AddBlock(type.Extent, $"class {type.Name}");
                        break;
                    case FunctionMemberAst method:
                        if (method.Body is not null)
                            AddBlock(method.Body.Extent, $"method {method.Name}");
                        break;
                    case IfStatementAst ifStatement:
                        for (var clauseIndex = 0; clauseIndex < ifStatement.Clauses.Count; clauseIndex++)
                        {
                            AddBlock(ifStatement.Clauses[clauseIndex].Item2.Extent,
                                clauseIndex == 0 ? "if branch" : "elseif branch");
                        }
                        if (ifStatement.ElseClause is { } elseClause)
                            AddBlock(elseClause.Extent, "else branch");
                        break;
                    case SwitchStatementAst switchStatement:
                        AddBlock(switchStatement.Extent, "switch");
                        foreach (var clause in switchStatement.Clauses)
                            AddBlock(clause.Item2.Extent, "switch clause");
                        if (switchStatement.Default is { } defaultClause)
                            AddBlock(defaultClause.Extent, "switch default");
                        break;
                    case LoopStatementAst loop:
                        AddBlock(loop.Body.Extent, GetLoopName(loop));
                        break;
                    case TryStatementAst tryStatement:
                        AddBlock(tryStatement.Body.Extent, "try block");
                        foreach (var catchClause in tryStatement.CatchClauses)
                            AddBlock(catchClause.Body.Extent, "catch block");
                        if (tryStatement.Finally is { } finallyClause)
                            AddBlock(finallyClause.Extent, "finally block");
                        break;
                    case ScriptBlockAst scriptBlock when !ReferenceEquals(scriptBlock, rootAst):
                        AddBlock(scriptBlock.Extent, "script block");
                        break;
                }
            }
        }

        AddRegionDirectives();
        cancellationToken.ThrowIfCancellationRequested();

        var orderedRanges = ranges.Values
            .OrderBy(static range => range.StartOffset)
            .ThenByDescending(static range => range.EndOffset)
            .ToArray();
        return RemoveCrossingRegions(orderedRanges, cancellationToken);

        void AddBlock(IScriptExtent extent, string name)
        {
            var startLimit = Math.Clamp(extent.StartOffset, 0, text.Length);
            var endLimit = Math.Clamp(extent.EndOffset, startLimit, text.Length);
            if (endLimit <= startLimit) return;

            var tokenIndex = FindFirstTokenAtOrAfter(tokens, startLimit);
            Token? openToken = null;
            for (; tokenIndex < tokens.Count; tokenIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = tokens[tokenIndex];
                if (candidate.Extent.StartOffset >= endLimit) break;
                if (candidate.Kind == TokenKind.LCurly && matchingBraces.ContainsKey(candidate.Extent.StartOffset))
                {
                    openToken = candidate;
                    break;
                }
            }
            if (openToken is null ||
                !matchingBraces.TryGetValue(openToken.Extent.StartOffset, out var closeEnd) ||
                closeEnd > endLimit ||
                !IsMultiline(lineStarts, openToken.Extent.StartOffset, closeEnd))
            {
                return;
            }

            AddRange(openToken.Extent.StartOffset, closeEnd, name);
        }

        void AddRegionDirectives()
        {
            var openRegions = new Stack<(int StartOffset, string Name)>();
            foreach (var token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (token.Kind != TokenKind.Comment) continue;

                var start = token.Extent.StartOffset;
                var end = token.Extent.EndOffset;
                if (start < 0 || end < start || end > text.Length) continue;
                var lineIndex = FindLineIndex(lineStarts, start);
                var lineStart = lineStarts[lineIndex];
                var lineEnd = FindLineEnd(text, end);
                if (!IsOnlyWhitespace(text, lineStart, start)) continue;

                var comment = text.AsSpan(start, end - start).Trim();
                if (StartsDirective(comment, "#region"))
                {
                    var name = comment.Length > 7 ? comment[7..].Trim().ToString() : string.Empty;
                    openRegions.Push((lineStart, name));
                }
                else if (StartsDirective(comment, "#endregion") && openRegions.Count > 0)
                {
                    var (regionStart, name) = openRegions.Pop();
                    if (IsMultiline(lineStarts, regionStart, lineEnd))
                    {
                        AddRange(regionStart, lineEnd,
                            string.IsNullOrWhiteSpace(name) ? "#region" : $"#region {BoundName(name)}");
                    }
                }
            }
        }

        void AddRange(int start, int end, string? name)
        {
            if (start < 0 || end <= start || end > text.Length ||
                !IsMultiline(lineStarts, start, end)) return;
            var key = (start, end);
            if (!ranges.ContainsKey(key)) ranges.Add(key, new FoldingRange(start, end, BoundName(name)));
        }
    }

    private static Dictionary<int, int> BuildBraceMatches(
        IReadOnlyList<Token> tokens,
        int textLength,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<int>();
        var matches = new Dictionary<int, int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if ((index & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[index];
            var start = token.Extent.StartOffset;
            var end = token.Extent.EndOffset;
            if (start < 0 || end < start || end > textLength) continue;
            if (token.Kind == TokenKind.LCurly) stack.Push(start);
            else if (token.Kind == TokenKind.RCurly && stack.Count > 0) matches[stack.Pop()] = end;
        }

        return matches;
    }

    private static int FindFirstTokenAtOrAfter(IReadOnlyList<Token> tokens, int offset)
    {
        var low = 0;
        var high = tokens.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (tokens[middle].Extent.StartOffset < offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static IReadOnlyList<FoldingRange> RemoveCrossingRegions(
        IReadOnlyList<FoldingRange> ranges,
        CancellationToken cancellationToken)
    {
        var accepted = new List<FoldingRange>(ranges.Count);
        foreach (var candidate in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crossings = accepted.Where(existing =>
                candidate.StartOffset < existing.EndOffset && existing.StartOffset < candidate.EndOffset &&
                !(candidate.StartOffset <= existing.StartOffset && existing.EndOffset <= candidate.EndOffset) &&
                !(existing.StartOffset <= candidate.StartOffset && candidate.EndOffset <= existing.EndOffset)).ToArray();
            if (crossings.Length == 0)
            {
                accepted.Add(candidate);
                continue;
            }

            var candidateIsRegion = candidate.Name?.StartsWith("#region", StringComparison.OrdinalIgnoreCase) == true;
            if (candidateIsRegion || crossings.Any(crossing =>
                    crossing.Name?.StartsWith("#region", StringComparison.OrdinalIgnoreCase) != true)) continue;

            accepted.RemoveAll(existing => crossings.Contains(existing));
            accepted.Add(candidate);
        }

        return accepted.OrderBy(static range => range.StartOffset)
            .ThenByDescending(static range => range.EndOffset)
            .ToArray();
    }

    private static List<int> BuildLineStarts(string text, CancellationToken cancellationToken)
    {
        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                lineStarts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        return lineStarts;
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
        for (var index = start; index < end; index++)
            if (!char.IsWhiteSpace(text[index])) return false;
        return true;
    }

    private static bool IsMultiline(IReadOnlyList<int> lineStarts, int start, int end)
        => FindLineIndex(lineStarts, start) != FindLineIndex(lineStarts, Math.Max(start, end - 1));

    private static bool StartsDirective(ReadOnlySpan<char> text, string directive)
        => text.StartsWith(directive, StringComparison.OrdinalIgnoreCase) &&
           (text.Length == directive.Length || char.IsWhiteSpace(text[directive.Length]));

    private static string GetLoopName(LoopStatementAst loop) => loop switch
    {
        ForStatementAst => "for loop",
        ForEachStatementAst => "foreach loop",
        WhileStatementAst => "while loop",
        DoWhileStatementAst => "do/while loop",
        DoUntilStatementAst => "do/until loop",
        _ => "loop"
    };

    private static string? BoundName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        const int maxLength = 96;
        var normalized = name.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..(maxLength - 1)] + "…";
    }
}

internal sealed record FoldingRange(int StartOffset, int EndOffset, string? Name = null);
