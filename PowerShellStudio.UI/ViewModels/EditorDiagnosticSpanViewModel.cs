using System;

namespace PowerShellStudio.UI.ViewModels
{
    /// <summary>
    /// Represents one editor diagnostic span with a message and source offsets.
    /// The shell layer uses the offsets for squiggle rendering; the view-model layer
    /// uses the line and column metadata for tab-level summaries, list panels, and navigation.
    /// </summary>
    public sealed class EditorDiagnosticSpanViewModel
    {
        public EditorDiagnosticSpanViewModel(int lineNumber, int columnNumber, string message, int startOffset, int endOffset)
        {
            LineNumber = Math.Max(1, lineNumber);
            ColumnNumber = Math.Max(1, columnNumber);
            Message = string.IsNullOrWhiteSpace(message) ? "Syntax error" : message;
            StartOffset = Math.Max(0, startOffset);
            EndOffset = Math.Max(StartOffset, endOffset);
        }

        public int LineNumber { get; }

        public int ColumnNumber { get; }

        public string Message { get; }

        public int StartOffset { get; }

        public int EndOffset { get; }

        public string DisplayText => $"Line {LineNumber}, Col {ColumnNumber}: {Message}";
    }
}
