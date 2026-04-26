namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// A syntax error returned by the PowerShell parser for a specific character range.
    /// StartOffset and EndOffset are zero-based character offsets into the script text.
    /// </summary>
    public sealed record ParseErrorInfo(string Message, int StartOffset, int EndOffset);
}
