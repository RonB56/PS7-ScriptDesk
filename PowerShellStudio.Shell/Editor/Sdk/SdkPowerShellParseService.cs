using System;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation.Language;
using System.Threading;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    /// <summary>
    /// Provides isolated access to the PowerShell SDK parser without invoking scripts or runspaces.
    /// </summary>
    public sealed class SdkPowerShellParseService
    {
        public SdkPowerShellParseResult ParseScript(
            string operationName,
            string? scriptText,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(operationName))
            {
                throw new ArgumentException("An operation name is required.", nameof(operationName));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            var text = scriptText ?? string.Empty;

            try
            {
                Token[] tokens;
                ParseError[] errors;

                var ast = Parser.ParseInput(text, out tokens, out errors);

                cancellationToken.ThrowIfCancellationRequested();
                stopwatch.Stop();

                return SdkPowerShellParseResult.Success(
                    operationName,
                    stopwatch.Elapsed,
                    errors.Select(SdkPowerShellParseError.FromParseError).ToArray(),
                    tokens.Select(SdkPowerShellTokenInfo.FromToken).ToArray(),
                    ast);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return SdkPowerShellParseResult.Failure(
                    operationName,
                    stopwatch.Elapsed,
                    ex);
            }
        }
    }
}
