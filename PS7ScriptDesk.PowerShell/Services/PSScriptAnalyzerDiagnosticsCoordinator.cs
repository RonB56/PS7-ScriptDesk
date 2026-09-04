using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Owns analyzer invocation, normalization, correlation, and store publication.</summary>
public sealed class PSScriptAnalyzerDiagnosticsCoordinator : IDiagnosticsCoordinator
{
    private readonly IPSScriptAnalyzerService _service;
    private readonly ScriptDiagnosticStore _store;

    public PSScriptAnalyzerDiagnosticsCoordinator(IPSScriptAnalyzerService service, ScriptDiagnosticStore store)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<bool> AnalyzeAndPublishAsync(PSScriptAnalyzerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Analyzer normalization/publication started.", new Dictionary<string, object?>
        {
            ["requestId"] = request.RequestId,
            ["documentId"] = request.DocumentId,
            ["revision"] = request.Revision
        });
        if (!Guid.TryParse(request.DocumentId, out var documentId))
        {
            DeveloperDiagnostics.LogDecision("PSScriptAnalyzer", "AnalyzeAndPublishAsync", "Publication rejected because the document identity was invalid.", "InvalidDocumentId", new Dictionary<string, object?> { ["requestId"] = request.RequestId });
            return false;
        }

        var result = await _service.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.RequestId, result.RequestId, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(result.Error))
        {
            DeveloperDiagnostics.LogDecision("PSScriptAnalyzer", "AnalyzeAndPublishAsync", "Analyzer result was not published because correlation or execution failed.", "ResultRejected", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["resultRequestId"] = result.RequestId, ["hasError"] = !string.IsNullOrWhiteSpace(result.Error) });
            return false;
        }

        var normalized = PSScriptAnalyzerResultNormalizer.Normalize(request, result);
        var published = _store.ReplaceDiagnostics(documentId, ScriptDiagnosticSource.PSScriptAnalyzer, request.Revision, normalized.Diagnostics);
        DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Analyzer diagnostics publication completed.", new Dictionary<string, object?>
        {
            ["requestId"] = request.RequestId,
            ["documentId"] = request.DocumentId,
            ["revision"] = request.Revision,
            ["rawFindingCount"] = result.Findings?.Count ?? 0,
            ["normalizedFindingCount"] = normalized.Diagnostics.Count,
            ["rejectedFindingCount"] = normalized.RejectedFindingCount,
            ["published"] = published
        });
        return published;
    }

    public Task<bool> PublishAsync(DiagnosticPublication publication, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publication.SourceId != ScriptDiagnosticSource.PSScriptAnalyzer ||
            publication.Document.DocumentId == Guid.Empty ||
            publication.Diagnostics.Any(diagnostic => diagnostic.SourceId != ScriptDiagnosticSource.PSScriptAnalyzer ||
                                                     diagnostic.DocumentId != publication.Document.DocumentId ||
                                                     diagnostic.DocumentRevision != publication.Document.DocumentRevision ||
                                                     !string.Equals(diagnostic.RequestId, publication.RequestId, StringComparison.Ordinal)))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_store.ReplaceDiagnostics(
            publication.Document.DocumentId,
            publication.SourceId,
            publication.Document.DocumentRevision,
            publication.Diagnostics));
    }
}
