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
/// Coordinates publication of validated diagnostics for a document revision.
/// </summary>
public interface IDiagnosticsCoordinator
{
    Task<bool> PublishAsync(DiagnosticPublication publication, CancellationToken cancellationToken = default);
}
