using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Editor;

public readonly record struct EditorCommandResult(int SelectionStart, int SelectionLength);

public static class EditorProductivityCommands
{
    public static EditorCommandResult InsertLineAbove(TextDocument document, int caretOffset)
    {
        var line = document.GetLineByOffset(Math.Clamp(caretOffset, 0, document.TextLength));
        var delimiter = GetPreferredLineEnding(document);
        using (BeginUndoGroup(document)) document.Insert(line.Offset, delimiter);
        return new EditorCommandResult(line.Offset, 0);
    }

    public static EditorCommandResult InsertLineBelow(TextDocument document, int caretOffset)
    {
        var line = document.GetLineByOffset(Math.Clamp(caretOffset, 0, document.TextLength));
        var delimiter = GetPreferredLineEnding(document);
        var insertOffset = line.Offset + line.TotalLength;
        using (BeginUndoGroup(document)) document.Insert(insertOffset, delimiter);
        return new EditorCommandResult(insertOffset + delimiter.Length, 0);
    }

    public static EditorCommandResult DeleteToLineStart(TextDocument document, int caretOffset, int selectionLength)
    {
        if (selectionLength != 0) return new EditorCommandResult(caretOffset, selectionLength);
        var line = document.GetLineByOffset(Math.Clamp(caretOffset, 0, document.TextLength));
        var start = line.Offset;
        var length = Math.Clamp(caretOffset, start, start + line.Length) - start;
        if (length > 0)
        {
            using (BeginUndoGroup(document)) document.Remove(start, length);
        }

        return new EditorCommandResult(start, 0);
    }

    public static EditorCommandResult DeleteToLineEnd(TextDocument document, int caretOffset, int selectionLength)
    {
        if (selectionLength != 0) return new EditorCommandResult(caretOffset, selectionLength);
        var line = document.GetLineByOffset(Math.Clamp(caretOffset, 0, document.TextLength));
        var caret = Math.Clamp(caretOffset, line.Offset, line.Offset + line.Length);
        var length = line.Offset + line.Length - caret;
        if (length > 0)
        {
            using (BeginUndoGroup(document)) document.Remove(caret, length);
        }

        return new EditorCommandResult(caret, 0);
    }

    public static EditorCommandResult DuplicateSelection(TextDocument document, int selectionStart, int selectionLength)
    {
        if (selectionLength <= 0) return new EditorCommandResult(selectionStart, 0);
        var start = Math.Clamp(selectionStart, 0, document.TextLength);
        var length = Math.Min(selectionLength, document.TextLength - start);
        var selected = document.GetText(start, length);
        using (BeginUndoGroup(document)) document.Insert(start + length, selected);
        return new EditorCommandResult(start + length, length);
    }

    public static EditorCommandResult ToggleComment(TextDocument document, int selectionStart, int selectionLength)
    {
        var range = GetLineRange(document, selectionStart, selectionLength);
        var lines = GetLines(document, range.FirstLine, range.LastLine);
        var allCommented = lines.Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .All(line => line.Text.AsSpan().TrimStart().StartsWith("#", StringComparison.Ordinal));

        using (BeginUndoGroup(document))
        {
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                var text = line.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var indent = text.Length - text.TrimStart().Length;
                if (allCommented)
                {
                    var hashIndex = text.IndexOf('#', indent);
                    var removeLength = hashIndex + 1 < text.Length && text[hashIndex + 1] == ' ' ? 2 : 1;
                    document.Remove(line.Offset + hashIndex, removeLength);
                }
                else
                {
                    document.Insert(line.Offset + indent, "# ");
                }
            }
        }

        return GetTransformedSelection(document, range, selectionStart, selectionLength, allCommented ? -2 : 2);
    }

    public static EditorCommandResult Indent(TextDocument document, int selectionStart, int selectionLength, int indentationSize = 4)
    {
        var range = GetLineRange(document, selectionStart, selectionLength);
        var lines = GetLines(document, range.FirstLine, range.LastLine);
        var indent = new string(' ', Math.Max(1, indentationSize));
        using (BeginUndoGroup(document))
        {
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                document.Insert(line.Offset, indent);
            }
        }

        return GetTransformedSelection(document, range, selectionStart, selectionLength, indent.Length);
    }

    public static EditorCommandResult Outdent(TextDocument document, int selectionStart, int selectionLength, int indentationSize = 4)
    {
        var range = GetLineRange(document, selectionStart, selectionLength);
        var lines = GetLines(document, range.FirstLine, range.LastLine);
        var width = Math.Max(1, indentationSize);
        var removedFromFirstLine = 0;
        using (BeginUndoGroup(document))
        {
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                var remove = Math.Min(width, line.Text.TakeWhile(ch => ch == ' ').Count());
                if (remove == 0 && line.Text.StartsWith("\t", StringComparison.Ordinal))
                {
                    remove = 1;
                }

                if (remove > 0)
                {
                    document.Remove(line.Offset, remove);
                    if (line.LineNumber == range.FirstLine)
                    {
                        removedFromFirstLine = remove;
                    }
                }
            }
        }

        return GetTransformedSelection(document, range, selectionStart, selectionLength, -removedFromFirstLine);
    }

    public static EditorCommandResult MoveLines(TextDocument document, int selectionStart, int selectionLength, int direction)
    {
        if (direction is not (-1 or 1))
        {
            return new EditorCommandResult(selectionStart, selectionLength);
        }

        var range = GetLineRange(document, selectionStart, selectionLength);
        DeveloperDiagnostics.LogInfo(
            "Editor",
            "MoveLines resolved the selected logical range.",
            new Dictionary<string, object?>
            {
                ["selectionStart"] = selectionStart,
                ["selectionLength"] = selectionLength,
                ["selectionEndExclusive"] = Math.Clamp(selectionStart + Math.Max(0, selectionLength), 0, document.TextLength),
                ["firstLine"] = range.FirstLine,
                ["lastLine"] = range.LastLine,
                ["lineCount"] = document.LineCount,
                ["direction"] = direction
            });
        if (direction < 0 && range.FirstLine == 1 || direction > 0 && range.LastLine == document.LineCount)
        {
            DeveloperDiagnostics.LogDecision("Editor", "MoveLines", "Move request reached a document boundary and was left unchanged.", "NoOpBoundary");
            return new EditorCommandResult(selectionStart, selectionLength);
        }

        var first = direction < 0 ? range.FirstLine - 1 : range.FirstLine;
        var last = direction < 0 ? range.LastLine : range.LastLine + 1;
        var lines = GetLines(document, first, last);
        var blockCount = range.LastLine - range.FirstLine + 1;
        var moved = direction < 0
            ? lines.Skip(1).Concat(lines.Take(1)).ToArray()
            : lines.Skip(blockCount).Concat(lines.Take(blockCount)).ToArray();
        var startOffset = lines[0].Offset;
        var totalLength = lines.Sum(line => line.TotalLength);
        // Keep line content separate from delimiter ownership. Moving a final
        // unterminated line must not concatenate it with the next moved line.
        // The existing delimiter slots remain in document order, preserving LF,
        // CRLF, mixed endings, and whether the region ends with a delimiter.
        var delimiters = lines.Select(line => line.Delimiter).ToArray();
        var movedText = string.Concat(moved.Select((line, index) =>
            line.Text + (index < delimiters.Length ? delimiters[index] : string.Empty)));

        using (BeginUndoGroup(document))
        {
            DeveloperDiagnostics.LogInfo(
                "Editor",
                "MoveLines entering one grouped document replacement.",
                new Dictionary<string, object?>
                {
                    ["sourceOffset"] = startOffset,
                    ["sourceLength"] = totalLength,
                    ["replacementLength"] = movedText.Length,
                    ["selectedLineCount"] = blockCount,
                    ["adjacentLineNumber"] = direction > 0 ? lines[blockCount].LineNumber : lines[0].LineNumber
                });
            document.Replace(startOffset, totalLength, movedText);
        }

        var newStart = direction < 0
            ? startOffset
            : startOffset + lines[blockCount].Text.Length + delimiters[0].Length;
        return new EditorCommandResult(newStart, Math.Min(selectionLength, Math.Max(0, document.TextLength - newStart)));
    }

    public static EditorCommandResult DuplicateLines(TextDocument document, int selectionStart, int selectionLength, int direction)
    {
        var range = GetLineRange(document, selectionStart, selectionLength);
        var lines = GetLines(document, range.FirstLine, range.LastLine);
        var blockText = string.Concat(lines.Select(line => line.Text + line.Delimiter));
        var insertedText = blockText;
        if (direction >= 0 && lines[^1].TotalLength == lines[^1].Length)
        {
            insertedText = GetPreferredLineEnding(document) + blockText;
        }
        var blockLength = blockText.Length;
        var insertOffset = direction < 0 ? lines[0].Offset : lines[^1].Offset + lines[^1].TotalLength;
        using (BeginUndoGroup(document))
        {
            document.Insert(insertOffset, insertedText);
        }

        var selectedStart = direction < 0 ? insertOffset : insertOffset;
        return new EditorCommandResult(selectedStart, Math.Min(insertedText.Length, Math.Max(0, document.TextLength - selectedStart)));
    }

    public static EditorCommandResult DeleteLines(TextDocument document, int selectionStart, int selectionLength)
    {
        var range = GetLineRange(document, selectionStart, selectionLength);
        var lines = GetLines(document, range.FirstLine, range.LastLine);
        var start = lines[0].Offset;
        var length = lines.Sum(line => line.TotalLength);
        if (lines[^1].LineNumber == document.LineCount && lines[0].LineNumber > 1)
        {
            var previous = document.GetLineByNumber(lines[0].LineNumber - 1);
            start = previous.Offset + previous.Length;
            length = document.TextLength - start;
        }

        using (BeginUndoGroup(document))
        {
            document.Remove(start, Math.Min(length, document.TextLength - start));
        }

        return new EditorCommandResult(Math.Min(start, document.TextLength), 0);
    }

    public static EditorCommandResult SurroundSelection(TextDocument document, int selectionStart, int selectionLength, char opener, char closer)
    {
        if (selectionLength <= 0)
        {
            return new EditorCommandResult(selectionStart, selectionLength);
        }

        using (BeginUndoGroup(document))
        {
            document.Insert(selectionStart + selectionLength, closer.ToString());
            document.Insert(selectionStart, opener.ToString());
        }

        return new EditorCommandResult(selectionStart + 1, selectionLength);
    }

    private static EditorCommandResult GetTransformedSelection(TextDocument document, LineRange range, int selectionStart, int selectionLength, int perLineDelta)
    {
        if (selectionLength == 0)
        {
            var line = document.GetLineByNumber(range.FirstLine);
            return new EditorCommandResult(Math.Min(document.TextLength, selectionStart + perLineDelta), 0);
        }

        var start = document.GetLineByNumber(range.FirstLine).Offset;
        var endLine = document.GetLineByNumber(Math.Min(range.LastLine, document.LineCount));
        // SelectionLength is a character count with an exclusive end. Do not add
        // an indentation delta here: endLine.Offset/Length already describe the
        // post-mutation line, and adding a delta selects the following line.
        var end = endLine.Offset + endLine.Length;
        return new EditorCommandResult(start, Math.Clamp(end - start, 0, document.TextLength - start));
    }

    private static LineRange GetLineRange(TextDocument document, int selectionStart, int selectionLength)
    {
        var start = Math.Clamp(selectionStart, 0, document.TextLength);
        var end = Math.Clamp(selectionStart + Math.Max(0, selectionLength), 0, document.TextLength);
        var firstLine = document.GetLineByOffset(start).LineNumber;
        var lastLine = document.GetLineByOffset(end).LineNumber;
        if (selectionLength > 0 && lastLine > firstLine && end == document.GetLineByNumber(lastLine).Offset)
        {
            lastLine--;
        }

        return new LineRange(firstLine, lastLine);
    }

    private static List<LineInfo> GetLines(TextDocument document, int firstLine, int lastLine)
    {
        return Enumerable.Range(firstLine, lastLine - firstLine + 1)
            .Select(number =>
            {
                var line = document.GetLineByNumber(number);
                return new LineInfo(
                    line.LineNumber,
                    line.Offset,
                    line.Length,
                    line.TotalLength,
                    document.GetText(line.Offset, line.Length),
                    document.GetText(line.Offset + line.Length, line.TotalLength - line.Length));
            })
            .ToList();
    }

    private static string GetPreferredLineEnding(TextDocument document)
    {
        for (var lineNumber = 1; lineNumber < document.LineCount; lineNumber++)
        {
            var line = document.GetLineByNumber(lineNumber);
            var delimiter = document.GetText(line.Offset + line.Length, line.TotalLength - line.Length);
            if (delimiter.Length > 0)
            {
                return delimiter;
            }
        }

        return Environment.NewLine;
    }

    private readonly record struct LineRange(int FirstLine, int LastLine);
    private readonly record struct LineInfo(int LineNumber, int Offset, int Length, int TotalLength, string Text, string Delimiter);

    private static IDisposable BeginUndoGroup(TextDocument document)
    {
        document.UndoStack.StartUndoGroup();
        return new UndoGroup(document);
    }

    private sealed class UndoGroup : IDisposable
    {
        private readonly TextDocument _document;
        private bool _disposed;

        public UndoGroup(TextDocument document) => _document = document;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _document.UndoStack.EndUndoGroup();
        }
    }
}
