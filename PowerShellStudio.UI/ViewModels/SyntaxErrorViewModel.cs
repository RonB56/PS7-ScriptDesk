using System;

namespace PowerShellStudio.UI.ViewModels
{
    public sealed class SyntaxErrorViewModel
    {
        public const string ErrorSeverity = "Error";
        public const string WarningSeverity = "Warning";

        public SyntaxErrorViewModel(int lineNumber, int columnNumber, string message, int startOffset, int endOffset, string? severity = null)
        {
            LineNumber = Math.Max(1, lineNumber);
            ColumnNumber = Math.Max(1, columnNumber);
            Message = message ?? string.Empty;
            StartOffset = Math.Max(0, startOffset);
            EndOffset = Math.Max(StartOffset, endOffset);
            Severity = NormalizeSeverity(severity);
        }

        public int LineNumber { get; }

        public int ColumnNumber { get; }

        public string Message { get; }

        public int StartOffset { get; }

        public int EndOffset { get; }

        public string Severity { get; }

        public bool IsError => string.Equals(Severity, ErrorSeverity, StringComparison.OrdinalIgnoreCase);

        public bool IsWarning => string.Equals(Severity, WarningSeverity, StringComparison.OrdinalIgnoreCase);

        public string DisplayText => $"Line {LineNumber}, Col {ColumnNumber}: {Message}";

        private static string NormalizeSeverity(string? severity)
        {
            return string.Equals(severity, WarningSeverity, StringComparison.OrdinalIgnoreCase)
                ? WarningSeverity
                : ErrorSeverity;
        }
    }
}
