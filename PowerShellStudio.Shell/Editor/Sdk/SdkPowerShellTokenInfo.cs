using System;
using System.Management.Automation.Language;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkPowerShellTokenInfo
    {
        public SdkPowerShellTokenInfo(
            string text,
            string kind,
            int startOffset,
            int endOffset,
            int startLineNumber,
            int startColumnNumber,
            int endLineNumber,
            int endColumnNumber)
        {
            Text = text ?? string.Empty;
            Kind = kind ?? string.Empty;
            StartOffset = Math.Max(0, startOffset);
            EndOffset = Math.Max(StartOffset, endOffset);
            StartLineNumber = Math.Max(0, startLineNumber);
            StartColumnNumber = Math.Max(0, startColumnNumber);
            EndLineNumber = Math.Max(StartLineNumber, endLineNumber);
            EndColumnNumber = Math.Max(0, endColumnNumber);
        }

        public string Text { get; }

        public string Kind { get; }

        public int StartOffset { get; }

        public int EndOffset { get; }

        public int StartLineNumber { get; }

        public int StartColumnNumber { get; }

        public int EndLineNumber { get; }

        public int EndColumnNumber { get; }

        internal static SdkPowerShellTokenInfo FromToken(Token token)
        {
            ArgumentNullException.ThrowIfNull(token);

            var extent = token.Extent;
            return new SdkPowerShellTokenInfo(
                text: token.Text ?? string.Empty,
                kind: token.Kind.ToString(),
                startOffset: extent?.StartOffset ?? 0,
                endOffset: extent?.EndOffset ?? 0,
                startLineNumber: extent?.StartLineNumber ?? 0,
                startColumnNumber: extent?.StartColumnNumber ?? 0,
                endLineNumber: extent?.EndLineNumber ?? 0,
                endColumnNumber: extent?.EndColumnNumber ?? 0);
        }
    }
}
