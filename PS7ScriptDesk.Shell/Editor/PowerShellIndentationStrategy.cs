using System;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Indentation;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Smart indentation strategy for PowerShell: copies the previous line's indentation
    /// and increases it by one level when the previous non-empty line ends with <c>{</c>.
    /// </summary>
    public sealed class PowerShellIndentationStrategy : DefaultIndentationStrategy
    {
        private const string IndentUnit = "    "; // 4 spaces

        public override void IndentLine(TextDocument document, DocumentLine line)
        {
            if (document is null || line is null) return;
            if (line.PreviousLine is null) { base.IndentLine(document, line); return; }

            // Find the nearest non-empty previous line
            var prev = line.PreviousLine;
            while (prev is not null && prev.Length == 0) prev = prev.PreviousLine;
            if (prev is null) { base.IndentLine(document, line); return; }

            var prevText = document.GetText(prev).TrimEnd();
            var baseIndent = GetLeadingWhitespace(document, prev);
            var currentText = document.GetText(line).TrimStart();

            string newIndent;

            if (prevText.EndsWith("{", StringComparison.Ordinal))
            {
                // After an opening brace — increase indent
                newIndent = baseIndent + IndentUnit;
            }
            else if (currentText.StartsWith("}", StringComparison.Ordinal) && baseIndent.Length >= IndentUnit.Length)
            {
                // Closing brace — decrease indent to match the opening brace level
                newIndent = baseIndent.Length >= IndentUnit.Length
                    ? baseIndent[..^IndentUnit.Length]
                    : string.Empty;
            }
            else
            {
                newIndent = baseIndent;
            }

            SetLeadingWhitespace(document, line, newIndent);
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
