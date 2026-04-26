using System;

namespace PowerShellStudio.UI.ViewModels
{
    public sealed class SyntaxErrorViewModel
    {
        public SyntaxErrorViewModel(int lineNumber, int columnNumber, string message, int startOffset, int endOffset)
        {
            LineNumber = Math.Max(1, lineNumber);
            ColumnNumber = Math.Max(1, columnNumber);
            Message = message ?? string.Empty;
            StartOffset = Math.Max(0, startOffset);
            EndOffset = Math.Max(StartOffset, endOffset);
        }

        public int LineNumber { get; }

        public int ColumnNumber { get; }

        public string Message { get; }

        public int StartOffset { get; }

        public int EndOffset { get; }

        public string Severity => "Error";

        public string DisplayText => $"Line {LineNumber}, Col {ColumnNumber}: {Message}";
    }
}
