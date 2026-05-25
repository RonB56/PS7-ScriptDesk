using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Management.Automation.Language;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Very small, in-process parser used only by the live editor syntax pump.
    ///
    /// The heavier PowerShellDiagnosticsService still owns authoring diagnostics because
    /// those checks intentionally run inside the selected external pwsh.exe runtime and
    /// may touch command metadata, modules, Get-Command, approved verbs, and other shell
    /// state.  This service avoids that process/JSON/stdout path for the hot typing loop.
    /// </summary>
    public sealed class InProcessPowerShellSyntaxDiagnosticsService : IDisposable
    {
        public InProcessPowerShellSyntaxDiagnosticsService()
        {
            _ = Task.Run(WarmUpParser);
        }

        public Task<DiagnosticsParseResult> ParseAsync(
            string scriptText,
            string pwshExecutablePath,
            PowerShellDiagnosticsMode diagnosticsMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // This service is deliberately syntax-only.  The parameters are kept aligned
            // with PowerShellDiagnosticsService so MainWindow can switch the live parser
            // without carrying two different call shapes.
            return Task.FromResult(ParseSyntax(scriptText, cancellationToken));
        }

        public Task<DiagnosticsParseResult> ParseAsync(
            string scriptText,
            string pwshExecutablePath,
            CancellationToken cancellationToken = default)
        {
            return ParseAsync(scriptText, pwshExecutablePath, PowerShellDiagnosticsMode.SyntaxOnly, cancellationToken);
        }

        public void Dispose()
        {
            // No external process, stream, runspace, or timer is owned by this service.
        }

        private static void WarmUpParser()
        {
            try
            {
                Token[] parserTokens;
                ParseError[] parserErrors;
                Parser.ParseInput(string.Empty, out parserTokens, out parserErrors);
            }
            catch
            {
                // Warmup is an optimization only; real parse failures are reported by ParseSyntax.
            }
        }

        private static DiagnosticsParseResult ParseSyntax(string? scriptText, CancellationToken cancellationToken)
        {
            var normalizedScriptText = scriptText ?? string.Empty;
            if (normalizedScriptText.Length == 0)
            {
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>());
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                Token[] parserTokens;
                ParseError[] parserErrors;
                Parser.ParseInput(normalizedScriptText, out parserTokens, out parserErrors);

                cancellationToken.ThrowIfCancellationRequested();

                return new DiagnosticsParseResult(
                    ConvertParseErrors(parserErrors, normalizedScriptText),
                    syntaxTokens: ConvertSyntaxTokens(parserTokens, normalizedScriptText));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DiagnosticsParseResult(
                    Array.Empty<ParseErrorInfo>(),
                    $"In-process syntax checking failed: {ex.Message}");
            }
        }

        private static IReadOnlyList<ParseErrorInfo> ConvertParseErrors(ParseError[]? parserErrors, string scriptText)
        {
            if (parserErrors is null || parserErrors.Length == 0)
            {
                return Array.Empty<ParseErrorInfo>();
            }

            var results = new List<ParseErrorInfo>(parserErrors.Length);
            foreach (var error in parserErrors)
            {
                if (error is null)
                {
                    continue;
                }

                var startOffset = ClampOffset(error.Extent?.StartOffset ?? 0, scriptText.Length);
                var endOffset = ClampOffset(error.Extent?.EndOffset ?? startOffset + 1, scriptText.Length);
                NormalizeNonEmptyRange(scriptText.Length, ref startOffset, ref endOffset);

                results.Add(new ParseErrorInfo(error.Message ?? string.Empty, startOffset, endOffset));
            }

            return results;
        }

        private static IReadOnlyList<SyntaxTokenInfo> ConvertSyntaxTokens(Token[]? parserTokens, string scriptText)
        {
            if (parserTokens is null || parserTokens.Length == 0)
            {
                return Array.Empty<SyntaxTokenInfo>();
            }

            var results = new List<SyntaxTokenInfo>(parserTokens.Length);
            foreach (var token in parserTokens)
            {
                if (token is null)
                {
                    continue;
                }

                var startOffset = ClampOffset(token.Extent?.StartOffset ?? 0, scriptText.Length);
                var endOffset = ClampOffset(token.Extent?.EndOffset ?? startOffset, scriptText.Length);
                if (endOffset <= startOffset)
                {
                    continue;
                }

                results.Add(new SyntaxTokenInfo(
                    token.Kind.ToString(),
                    token.Text ?? string.Empty,
                    startOffset,
                    endOffset));
            }

            return results;
        }

        private static int ClampOffset(int offset, int textLength)
        {
            return Math.Clamp(offset, 0, Math.Max(0, textLength));
        }

        private static void NormalizeNonEmptyRange(int textLength, ref int startOffset, ref int endOffset)
        {
            if (textLength <= 0)
            {
                startOffset = 0;
                endOffset = 0;
                return;
            }

            if (startOffset >= textLength)
            {
                startOffset = textLength - 1;
            }

            if (endOffset <= startOffset)
            {
                endOffset = Math.Min(startOffset + 1, textLength);
            }
        }
    }
}
