using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed class StructuredEditorExecutionBackend : IEditorExecutionBackend
{
    private readonly IEditorExecutionAdapter _adapter;
    private readonly ExecutionBackendAvailability _availability;
    private readonly string? _availabilityReason;

    public StructuredEditorExecutionBackend(
        IEditorExecutionAdapter adapter,
        ExecutionBackendAvailability availability = ExecutionBackendAvailability.Available,
        string? availabilityReason = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _availability = availability;
        _availabilityReason = availabilityReason;
    }

    public string BackendId => "structured-session-broker";

    public ExecutionBackendCapabilities Capabilities { get; } = new(
        SupportsInteractiveInput: false,
        SupportsSecureInput: false,
        SupportsCurrentScope: true,
        SupportsScriptCallIsolation: true,
        SupportsTerminalInterrupt: false,
        SupportsStructuredStreams: true,
        SupportsWorkingDirectoryTracking: true,
        SupportsRestart: true,
        SupportsShutdown: true,
        SupportsNativeOutput: true,
        SupportsCancellation: true);

    public ExecutionBackendAvailability Availability => _availability;

    public string? AvailabilityReason => _availabilityReason;

    public event Action<EditorExecutionEvent>? EventPublished
    {
        add => _adapter.EventPublished += value;
        remove => _adapter.EventPublished -= value;
    }

    public Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken)
        => ExecuteCoreAsync(request, cancellationToken);

    private async Task<EditorExecutionResult> ExecuteCoreAsync(EditorExecutionRequest request, CancellationToken cancellationToken)
    {
        var result = await _adapter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return result with { BackendId = BackendId };
    }
}
