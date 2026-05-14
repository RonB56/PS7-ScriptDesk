namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// A diagnostic returned for a specific character range in the script text.
    /// StartOffset and EndOffset are zero-based character offsets into the script text.
    /// Parser failures are errors; editor authoring guidance is usually warning-level.
    /// </summary>
    public sealed record ParseErrorInfo(string Message, int StartOffset, int EndOffset, string Severity = ParseErrorInfo.ErrorSeverity)
    {
        public const string ErrorSeverity = "Error";
        public const string WarningSeverity = "Warning";

        public bool IsError => string.Equals(Severity, ErrorSeverity, System.StringComparison.OrdinalIgnoreCase);

        public bool IsWarning => string.Equals(Severity, WarningSeverity, System.StringComparison.OrdinalIgnoreCase);

        public static ParseErrorInfo AsWarning(ParseErrorInfo diagnostic)
        {
            return diagnostic with { Severity = WarningSeverity };
        }
    }
}
