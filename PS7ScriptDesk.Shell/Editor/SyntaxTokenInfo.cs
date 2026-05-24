using System;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Represents a token returned by PowerShell's own parser.
    /// Offsets are translated into AvalonEdit document offsets before the UI consumes them.
    /// </summary>
    public sealed record SyntaxTokenInfo(string Kind, string Text, int StartOffset, int EndOffset)
    {
        public int Length => Math.Max(0, EndOffset - StartOffset);
    }
}
