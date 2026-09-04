using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Application.Interfaces;

public sealed record DiagnosticRequest(
    ScriptDocumentSnapshot Document,
    string? Path,
    string ScriptText,
    string RequestId,
    ScriptDiagnosticSource SourceId);

public sealed record DiagnosticPublication(
    ScriptDocumentSnapshot Document,
    ScriptDiagnosticSource SourceId,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    string RequestId);

/// <summary>
/// Boundary for future debounce, cancellation, stale-result validation, and publication coordination.
/// Phase 0 defines the seam; producers and scheduling remain outside this interface for now.
/// </summary>
public interface IDiagnosticsCoordinator
{
    Task<bool> PublishAsync(DiagnosticPublication publication, CancellationToken cancellationToken = default);
}
