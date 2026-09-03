using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;

namespace PS7ScriptDesk.Shell.Editor;

public static class EditorTransformCommands
{
    public static EditorCommandResult SortLinesAscending(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.OrderBy(line => line, StringComparer.Ordinal).ToArray());
    public static EditorCommandResult SortLinesDescending(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.OrderByDescending(line => line, StringComparer.Ordinal).ToArray());
    public static EditorCommandResult ReverseLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Reverse().ToArray());
    public static EditorCommandResult RemoveDuplicateLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Distinct(StringComparer.Ordinal).ToArray());
    public static EditorCommandResult TrimLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.Trim()).ToArray());
    public static EditorCommandResult TrimTrailingWhitespace(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.TrimEnd(' ', '\t')).ToArray());
    public static EditorCommandResult RemoveBlankLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
    public static EditorCommandResult UppercaseSelection(TextDocument document, int start, int length) => ApplySelection(document, start, length, text => text.ToUpperInvariant());
    public static EditorCommandResult LowercaseSelection(TextDocument document, int start, int length) => ApplySelection(document, start, length, text => text.ToLowerInvariant());
    public static EditorCommandResult TitleCaseSelection(TextDocument document, int start, int length) => ApplySelection(document, start, length, text => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text));
    public static EditorCommandResult PrefixLines(TextDocument document, int start, int length, string prefix) => ApplyLines(document, start, length, lines => lines.Select(line => prefix + line).ToArray());
    public static EditorCommandResult SuffixLines(TextDocument document, int start, int length, string suffix) => ApplyLines(document, start, length, lines => lines.Select(line => line + suffix).ToArray());
    public static EditorCommandResult QuoteLines(TextDocument document, int start, int length, char quote) => ApplyLines(document, start, length, lines => lines.Select(line => quote + line + quote).ToArray());
    public static EditorCommandResult AddTrailingComma(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.EndsWith(",", StringComparison.Ordinal) ? line : line + ",").ToArray());
    public static EditorCommandResult RemoveTrailingComma(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.EndsWith(",", StringComparison.Ordinal) ? line[..^1] : line).ToArray());

    public static EditorCommandResult TrimDocumentTrailingWhitespace(TextDocument document)
    {
        var lines = Enumerable.Range(1, document.LineCount).Select(number => document.GetLineByNumber(number)).ToArray();
        var replacements = lines
            .Select(line => (Line: line, Text: document.GetText(line.Offset, line.Length)))
            .Where(item => item.Text.Length > 0 && item.Text.TrimEnd(' ', '\t').Length != item.Text.Length)
            .ToArray();
        using (BeginUndoGroup(document))
        {
            foreach (var replacement in replacements.Reverse())
            {
                var trimmed = replacement.Text.TrimEnd(' ', '\t');
                document.Replace(replacement.Line.Offset, replacement.Line.Length, trimmed);
            }
        }

        return new EditorCommandResult(0, document.TextLength);
    }

    public static EditorCommandResult SortLinesIgnoreCaseAscending(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.OrderBy(line => line, StringComparer.OrdinalIgnoreCase).ToArray());

    public static EditorCommandResult SortLinesIgnoreCaseDescending(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.OrderByDescending(line => line, StringComparer.OrdinalIgnoreCase).ToArray());

    public static EditorCommandResult JoinLines(TextDocument document, int start, int length)
    {
        var range = GetLineRange(document, start, length);
        var lines = Enumerable.Range(range.FirstLine, range.LastLine - range.FirstLine + 1)
            .Select(number => document.GetLineByNumber(number))
            .Select(line => new
            {
                Line = line,
                Text = document.GetText(line.Offset, line.Length),
                Delimiter = document.GetText(line.Offset + line.Length, line.TotalLength - line.Length)
            })
            .ToArray();
        var joined = string.Join(" ", lines.Select(item => item.Text.Trim()).Where(text => text.Length > 0));
        var replacement = joined + lines[^1].Delimiter;
        var sourceStart = lines[0].Line.Offset;
        var sourceLength = lines.Sum(item => item.Line.TotalLength);
        using (BeginUndoGroup(document)) document.Replace(sourceStart, sourceLength, replacement);
        return new EditorCommandResult(sourceStart, joined.Length);
    }

    private static EditorCommandResult ApplySelection(TextDocument document, int start, int length, Func<string, string> transform)
    {
        if (length <= 0) return new EditorCommandResult(start, 0);
        using (BeginUndoGroup(document)) document.Replace(start, length, transform(document.GetText(start, length)));
        return new EditorCommandResult(start, length);
    }

    private static EditorCommandResult ApplyLines(TextDocument document, int start, int length, Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var range = GetLineRange(document, start, length);
        var lines = Enumerable.Range(range.FirstLine, range.LastLine - range.FirstLine + 1).Select(number => document.GetLineByNumber(number)).ToArray();
        var sourceTexts = lines.Select(line => document.GetText(line.Offset, line.Length)).ToArray();
        var delimiters = lines.Select(line => document.GetText(line.Offset + line.Length, line.TotalLength - line.Length)).ToArray();
        var transformed = transform(sourceTexts);
        var replacement = string.Concat(transformed.Select((text, index) => text + (index < delimiters.Length ? delimiters[index] : string.Empty)));
        var sourceStart = lines[0].Offset;
        var sourceLength = lines.Sum(line => line.TotalLength);
        using (BeginUndoGroup(document)) document.Replace(sourceStart, sourceLength, replacement);
        var resultLength = transformed.Count == 0 ? 0 : transformed.Sum(text => text.Length) + delimiters.Take(transformed.Count).Sum(delimiter => delimiter.Length);
        return new EditorCommandResult(sourceStart, Math.Min(resultLength, document.TextLength - sourceStart));
    }

    private static (int FirstLine, int LastLine) GetLineRange(TextDocument document, int start, int length)
    {
        var safeStart = Math.Clamp(start, 0, document.TextLength);
        var safeEnd = Math.Clamp(start + Math.Max(0, length), 0, document.TextLength);
        var first = document.GetLineByOffset(safeStart).LineNumber;
        var last = document.GetLineByOffset(safeEnd).LineNumber;
        if (length > 0 && last > first && safeEnd == document.GetLineByNumber(last).Offset) last--;
        return (first, Math.Max(first, last));
    }

    private static IDisposable BeginUndoGroup(TextDocument document)
    {
        document.UndoStack.StartUndoGroup();
        return new UndoGroup(document);
    }

    private sealed class UndoGroup(TextDocument document) : IDisposable
    {
        public void Dispose() => document.UndoStack.EndUndoGroup();
    }
}
