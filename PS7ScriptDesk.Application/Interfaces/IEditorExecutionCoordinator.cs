using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IEditorExecutionCoordinator : IDisposable
{
    bool IsShadowMode { get; }

    bool IsRollbackEnabled { get; }

    ActiveEditorExecutionSnapshot? ActiveExecution { get; }

    event Action<EditorExecutionEvent>? EventPublished;

    event Action? ActiveExecutionChanged;

    ExecutionRoutingDecision Route(EditorExecutionRequest request);

    Task<ExecutionRoutingDecision> ObserveAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default);

    Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default);
}

public sealed record ExecutionRoutingDecision(
    Guid RequestId,
    string BackendId,
    ExecutionBackendCapabilities Capabilities,
    bool Accepted,
    bool IsShadowMode,
    string? Reason,
    bool GateRequested = false,
    ExecutionBackendAvailability StructuredAvailability = ExecutionBackendAvailability.Unavailable,
    ExecutionCompatibilityState StructuredCompatibility = ExecutionCompatibilityState.NotRequested,
    ExecutionRequestClassification Classification = ExecutionRequestClassification.Unknown,
    ExecutionCapabilityRequirements? RequiredCapabilities = null,
    string? SelectionReason = null,
    string? FallbackOrDowngradeReason = null,
    DateTimeOffset? Timestamp = null)
{
    public string SelectedBackend => BackendId;
}
