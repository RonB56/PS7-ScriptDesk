using System;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Indentation;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Basic PowerShell indentation. It uses nearby line text only and deliberately
    /// avoids brace adjustments when lexical context is uncertain.
    /// </summary>
    public sealed class PowerShellIndentationStrategy : DefaultIndentationStrategy
    {
        private const string IndentUnit = "    "; // 4 spaces

        public override void IndentLine(TextDocument document, DocumentLine line)
        {
            if (document is null || line is null) return;
            // Leave a genuinely blank line empty. This avoids generating trailing
            // whitespace on Enter; indentation can still be applied to non-empty
            // split lines without touching unrelated user-authored text.
            if (line.Length == 0 || string.IsNullOrWhiteSpace(document.GetText(line))) return;
            var newIndent = GetIndentationForLine(document, line);

            SetLeadingWhitespace(document, line, newIndent);
        }

        public string GetIndentationForLine(TextDocument document, DocumentLine line, string? incomingText = null)
        {
            if (document is null || line is null || line.PreviousLine is null) return string.Empty;

            var prev = line.PreviousLine;
            while (prev is not null && string.IsNullOrWhiteSpace(document.GetText(prev))) prev = prev.PreviousLine;
            if (prev is null) return string.Empty;

            var prevText = document.GetText(prev).TrimEnd();
            var baseIndent = GetLeadingWhitespace(document, prev);
            var currentText = (line.Length == 0 && incomingText is not null ? incomingText : document.GetText(line)).TrimStart();

            if (IsInsideHereString(document, prev) || IsInsideHereString(document, line)) return baseIndent;
            if (currentText.StartsWith("}", StringComparison.Ordinal)) return baseIndent;
            if (EndsWithCodeOpeningBrace(prevText)) return baseIndent + IndentUnit;
            return baseIndent;
        }

        private static bool EndsWithCodeOpeningBrace(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('#')) return false;

            var quote = '\0';
            var escaped = false;
            var commentStart = text.Length;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote == '\'')
                {
                    if (ch == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                    if (ch == '\'') quote = '\0';
                    continue;
                }
                if (quote == '"')
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '`') { escaped = true; continue; }
                    if (ch == '"') quote = '\0';
                    continue;
                }
                if (ch == '#') { commentStart = i; break; }
                if (ch is '\'' or '"') quote = ch;
            }

            return text[..commentStart].TrimEnd().EndsWith("{", StringComparison.Ordinal) && quote == '\0';
        }

        private static bool IsInsideHereString(TextDocument document, DocumentLine line)
        {
            const int maximumLookbackLines = 128;
            var firstLine = Math.Max(1, line.LineNumber - maximumLookbackLines + 1);
            var inside = false;
            for (var number = firstLine; number < line.LineNumber; number++)
            {
                var candidate = document.GetLineByNumber(number);
                var text = document.GetText(candidate).Trim();
                if (inside)
                {
                    if (text is "'@" or "\"@") inside = false;
                }
                else if (text.EndsWith("@'", StringComparison.Ordinal) || text.EndsWith("@\"", StringComparison.Ordinal))
                {
                    inside = true;
                }
            }

            // If the bounded window begins after the document start, syntax state
            // may have started earlier. Be conservative for brace adjustment.
            return inside || firstLine > 1;
        }

        private static string GetLeadingWhitespace(TextDocument document, DocumentLine line)
        {
            var text = document.GetText(line);
            var i = 0;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text[..i];
        }

        private static void SetLeadingWhitespace(TextDocument document, DocumentLine line, string indent)
        {
            var text = document.GetText(line);
            var existingIndentLength = 0;
            while (existingIndentLength < text.Length &&
                   (text[existingIndentLength] == ' ' || text[existingIndentLength] == '\t'))
                existingIndentLength++;

            if (string.Equals(text[..existingIndentLength], indent, StringComparison.Ordinal))
                return;

            using var _ = document.RunUpdate();
            document.Replace(line.Offset, existingIndentLength, indent);
        }
    }
}
