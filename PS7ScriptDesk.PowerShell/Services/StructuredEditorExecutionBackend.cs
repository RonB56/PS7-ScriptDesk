using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Application.Diagnostics;

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

    public string BackendId => EditorExecutionCoordinator.StructuredBackendId;

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
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "Structured editor backend entered.",
            new Dictionary<string, object?>
            {
                ["requestId"] = request.RequestId,
                ["backendId"] = BackendId,
                ["adapterType"] = _adapter.GetType().FullName,
                ["scriptLength"] = request.ScriptText?.Length ?? 0,
                ["sessionGeneration"] = request.SessionGeneration,
                ["contentOmitted"] = true
            });

        try
        {
            var result = await _adapter.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            DeveloperDiagnostics.LogInfo(
                "Phase2CProof",
                "Structured editor backend exited.",
                new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["backendId"] = BackendId,
                    ["resultStatus"] = result.Status.ToString(),
                    ["outputCount"] = result.Outputs.Count
                });
            return result with { BackendId = BackendId };
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "Phase2CProof",
                ex,
                "Structured editor backend threw.",
                new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["backendId"] = BackendId,
                    ["adapterType"] = _adapter.GetType().FullName
                });
            throw;
        }
    }
}
