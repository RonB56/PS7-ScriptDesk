using System;
using System.Collections.Generic;

namespace PowerShellStudio.Shell.Editor
{
    public sealed class DiagnosticsParseResult
    {
        public DiagnosticsParseResult(
            IReadOnlyList<ParseErrorInfo>? errors,
            string? failureMessage = null,
            IReadOnlyList<SyntaxTokenInfo>? syntaxTokens = null,
            ScriptAuthoringFacts? authoringFacts = null)
        {
            Errors = errors ?? Array.Empty<ParseErrorInfo>();
            SyntaxTokens = syntaxTokens ?? Array.Empty<SyntaxTokenInfo>();
            AuthoringFacts = authoringFacts ?? ScriptAuthoringFacts.Empty;
            FailureMessage = string.IsNullOrWhiteSpace(failureMessage) ? null : failureMessage;
        }

        public IReadOnlyList<ParseErrorInfo> Errors { get; }

        public IReadOnlyList<SyntaxTokenInfo> SyntaxTokens { get; }

        public ScriptAuthoringFacts AuthoringFacts { get; }

        public string? FailureMessage { get; }

        public bool Succeeded => FailureMessage is null;
    }
}
