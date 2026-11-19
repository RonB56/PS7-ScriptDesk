using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Routes a single editor snapshot to the live PSES provider and, when needed, the
    /// persistent completion helper. It does not own the editor, popup, or execution state.
    /// </summary>
    internal sealed class IntelliSenseProviderCoordinator
    {
        internal sealed record QueryResult(CompletionServiceResult? Result, string Source, bool TimedOut);

        public async Task<QueryResult> QueryAsync(
            bool queryPses,
            bool queryLegacy,
            int documentLength,
            Func<CancellationToken, Task<CompletionServiceResult?>> psesQuery,
            Func<CancellationToken, Task<CompletionServiceResult?>> legacyQuery,
            CancellationToken cancellationToken)
        {
            var timedOut = false;
            if (queryPses)
            {
                var result = await TryQueryAsync("PSES", psesQuery, documentLength, cancellationToken)
                    .ConfigureAwait(true);
                timedOut |= result.TimedOut;
                if (result.Result is { HasItems: true })
                    return new QueryResult(result.Result, "authoritative-live", timedOut);
            }

            if (queryLegacy)
            {
                var result = await TryQueryAsync("persistent-helper", legacyQuery, documentLength, cancellationToken)
                    .ConfigureAwait(true);
                timedOut |= result.TimedOut;
                if (result.Result is { HasItems: true })
                    return new QueryResult(result.Result, "engine", timedOut);
            }

            return new QueryResult(null, "none", timedOut);
        }

        private static async Task<(CompletionServiceResult? Result, bool TimedOut)> TryQueryAsync(
            string provider,
            Func<CancellationToken, Task<CompletionServiceResult?>> query,
            int documentLength,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await query(cancellationToken).ConfigureAwait(true);
                if (result is null || !result.HasItems)
                    return (null, false);

                if (result.ReplacementIndex < 0 || result.ReplacementLength < 0 ||
                    result.ReplacementIndex > documentLength ||
                    result.ReplacementLength > documentLength - result.ReplacementIndex)
                {
                    DeveloperDiagnostics.LogWarning("EditorCompletion", "Completion provider returned an invalid replacement range.",
                        new Dictionary<string, object?>
                        {
                            ["provider"] = provider,
                            ["documentLength"] = documentLength,
                            ["replacementIndex"] = result.ReplacementIndex,
                            ["replacementLength"] = result.ReplacementLength
                        });
                    return (null, false);
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var items = result.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.CompletionText) &&
                                   seen.Add($"{item.Kind}:{item.CompletionText}:{item.ListItemText}"))
                    .OrderBy(item => item.ListItemText, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.CompletionText, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Kind)
                    .ToList();
                return (items.Count == 0
                    ? null
                    : new CompletionServiceResult(result.ReplacementIndex, result.ReplacementLength, items), false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogWarning("EditorCompletion", "Completion provider failed; fallback remains available.",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = provider,
                        ["exceptionType"] = ex.GetType().Name
                    });
                return (null, false);
            }
        }
    }
}
