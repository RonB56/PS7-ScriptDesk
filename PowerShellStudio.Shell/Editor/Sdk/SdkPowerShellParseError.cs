using System;
using System.Management.Automation.Language;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkPowerShellParseError
    {
        public SdkPowerShellParseError(
            string message,
            string errorId,
            int startOffset,
            int endOffset,
            int startLineNumber,
            int startColumnNumber,
            int endLineNumber,
            int endColumnNumber)
        {
            Message = message ?? string.Empty;
            ErrorId = errorId ?? string.Empty;
            StartOffset = Math.Max(0, startOffset);
            EndOffset = Math.Max(StartOffset, endOffset);
            StartLineNumber = Math.Max(0, startLineNumber);
            StartColumnNumber = Math.Max(0, startColumnNumber);
            EndLineNumber = Math.Max(StartLineNumber, endLineNumber);
            EndColumnNumber = Math.Max(0, endColumnNumber);
        }

        public string Message { get; }

        public string ErrorId { get; }

        public int StartOffset { get; }

        public int EndOffset { get; }

        public int StartLineNumber { get; }

        public int StartColumnNumber { get; }

        public int EndLineNumber { get; }

        public int EndColumnNumber { get; }

        internal static SdkPowerShellParseError FromParseError(ParseError error)
        {
            ArgumentNullException.ThrowIfNull(error);

            var extent = error.Extent;
            return new SdkPowerShellParseError(
                message: error.Message ?? string.Empty,
                errorId: error.ErrorId ?? string.Empty,
                startOffset: extent?.StartOffset ?? 0,
                endOffset: extent?.EndOffset ?? 0,
                startLineNumber: extent?.StartLineNumber ?? 0,
                startColumnNumber: extent?.StartColumnNumber ?? 0,
                endLineNumber: extent?.EndLineNumber ?? 0,
                endColumnNumber: extent?.EndColumnNumber ?? 0);
        }
    }
}
