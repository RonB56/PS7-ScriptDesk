using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IEditorExecutionBackend
{
    string BackendId { get; }

    ExecutionBackendCapabilities Capabilities { get; }

    ExecutionBackendAvailability Availability { get; }

    string? AvailabilityReason { get; }

    event Action<EditorExecutionEvent>? EventPublished;

    Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken);
}
