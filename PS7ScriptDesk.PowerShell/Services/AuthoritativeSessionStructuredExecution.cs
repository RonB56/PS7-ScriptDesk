using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Structured editor execution over the already-running PSES-owned session.
/// This adapter deliberately has no SMA/runspace/process creation authority.
/// </summary>
public sealed class AuthoritativeSessionStructuredExecutionAdapter : IEditorExecutionAdapter
{
    private readonly IAuthoritativeSessionStructuredExecution _session;
    private readonly EditorExecutionFeatureGate _featureGate;

    public AuthoritativeSessionStructuredExecutionAdapter(
        IAuthoritativeSessionStructuredExecution session,
        EditorExecutionFeatureGate featureGate)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    }

    public PersistentSessionSnapshot Snapshot => new(
        _session.SessionGeneration,
        _session.IsSessionRunning ? PersistentSessionLifecycle.Ready : PersistentSessionLifecycle.Created,
        null,
        _session.CurrentWorkingDirectory,
        false,
        "authoritative-pses-session");

    public event Action<EditorExecutionEvent>? EventPublished;

    public async Task<EditorExecutionResult> ExecuteAsync(
        EditorExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_featureGate.IsStructuredExecutionEnabled)
        {
            return Rejected(request, "Authoritative structured execution is disabled by its feature gate.");
        }

        if (!_session.IsSessionRunning || request.SessionGeneration != _session.SessionGeneration)
        {
            DeveloperDiagnostics.LogDecision(
                "Execution",
                "STRUCTURED_GENERATION_MISMATCH",
                "Authoritative structured execution was rejected because the PSES session is unavailable or stale.",
                "Rejected",
                new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["requestGeneration"] = request.SessionGeneration,
                    ["authoritativeGeneration"] = _session.SessionGeneration,
                    ["authoritativeProcessId"] = _session.ProcessId
                });
            return Rejected(request, "The authoritative PSES session is unavailable or stale.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sequence = 0L;
        var outputs = new List<EditorOutputRecord>();
        void OnOutput(ExecutionOutputRecord output)
        {
            var record = new EditorOutputRecord(
                request.RequestId,
                request.SessionGeneration,
                Interlocked.Increment(ref sequence),
                MapStream(output.StreamKind),
                output.Text,
                new DateTimeOffset(output.Timestamp));
            outputs.Add(record);
            EventPublished?.Invoke(new EditorExecutionEvent(
                EditorExecutionEventKind.Output,
                request.RequestId,
                request.SessionGeneration,
                record.Sequence,
                record,
                Timestamp: record.Timestamp,
                BackendId: AuthoritativeSessionStructuredExecutionBackend.BackendName));
        }

        DeveloperDiagnostics.LogInfo("Phase2CProof", "STRUCTURED_BACKEND_SELECTED.", new Dictionary<string, object?>
        {
            ["requestId"] = request.RequestId,
            ["backendId"] = AuthoritativeSessionStructuredExecutionBackend.BackendName,
            ["processId"] = _session.ProcessId,
            ["sessionGeneration"] = request.SessionGeneration,
            ["scriptLength"] = request.ScriptText.Length,
            ["contentOmitted"] = true
        });
        EventPublished?.Invoke(new EditorExecutionEvent(EditorExecutionEventKind.Accepted, request.RequestId, request.SessionGeneration, 0, Timestamp: startedAt, BackendId: AuthoritativeSessionStructuredExecutionBackend.BackendName));
        EventPublished?.Invoke(new EditorExecutionEvent(EditorExecutionEventKind.Started, request.RequestId, request.SessionGeneration, 0, Timestamp: startedAt, BackendId: AuthoritativeSessionStructuredExecutionBackend.BackendName));

        try
        {
            var result = await _session.ExecuteStructuredScriptAsync(
                request.RequestId,
                request.SessionGeneration,
                request.DocumentDisplayName,
                request.ScriptText,
                request.ExecuteInCurrentScope,
                OnOutput,
                cancellationToken).ConfigureAwait(false);
            var endedAt = DateTimeOffset.UtcNow;
            var executionEvent = result.Status switch
            {
                EditorExecutionStatus.Completed => EditorExecutionEventKind.Completed,
                EditorExecutionStatus.Cancelled => EditorExecutionEventKind.Cancelled,
                EditorExecutionStatus.Rejected => EditorExecutionEventKind.Rejected,
                _ => EditorExecutionEventKind.Failed
            };
            EventPublished?.Invoke(new EditorExecutionEvent(executionEvent, request.RequestId, request.SessionGeneration, sequence, ErrorMessage: result.ErrorMessage, Timestamp: endedAt, BackendId: AuthoritativeSessionStructuredExecutionBackend.BackendName));
            return new EditorExecutionResult(
                request.RequestId,
                request.SessionGeneration,
                result.Status,
                outputs.ToArray(),
                result.CurrentWorkingDirectory,
                null,
                result.ErrorMessage,
                startedAt,
                endedAt,
                AuthoritativeSessionStructuredExecutionBackend.BackendName);
        }
        catch (Exception ex)
        {
            var endedAt = DateTimeOffset.UtcNow;
            DeveloperDiagnostics.LogException("Phase2CProof", ex, "Authoritative structured adapter failed.", new Dictionary<string, object?>
            {
                ["requestId"] = request.RequestId,
                ["processId"] = _session.ProcessId,
                ["sessionGeneration"] = request.SessionGeneration
            });
            EventPublished?.Invoke(new EditorExecutionEvent(EditorExecutionEventKind.Failed, request.RequestId, request.SessionGeneration, sequence, ErrorMessage: ex.Message, Timestamp: endedAt, BackendId: AuthoritativeSessionStructuredExecutionBackend.BackendName));
            return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Failed, outputs.ToArray(), _session.CurrentWorkingDirectory, null, ex.Message, startedAt, endedAt, AuthoritativeSessionStructuredExecutionBackend.BackendName);
        }
    }

    private static EditorOutputStreamKind MapStream(ExecutionOutputStreamKind stream)
        => stream switch
        {
            ExecutionOutputStreamKind.StandardError => EditorOutputStreamKind.Error,
            ExecutionOutputStreamKind.StandardOutput => EditorOutputStreamKind.Success,
            _ => EditorOutputStreamKind.Host
        };

    private static EditorExecutionResult Rejected(EditorExecutionRequest request, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Rejected, Array.Empty<EditorOutputRecord>(), request.WorkingDirectory, null, reason, now, now, AuthoritativeSessionStructuredExecutionBackend.BackendName, reason);
    }
}

public sealed class AuthoritativeSessionStructuredExecutionBackend : IEditorExecutionBackend
{
    public const string BackendName = "authoritative-pses-session";
    private readonly IEditorExecutionAdapter _adapter;

    public AuthoritativeSessionStructuredExecutionBackend(IEditorExecutionAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public string BackendId => BackendName;

    public ExecutionBackendCapabilities Capabilities { get; } = new(
        SupportsInteractiveInput: false,
        SupportsSecureInput: false,
        SupportsCurrentScope: true,
        SupportsScriptCallIsolation: true,
        SupportsTerminalInterrupt: true,
        SupportsStructuredStreams: false,
        SupportsWorkingDirectoryTracking: true,
        SupportsRestart: true,
        SupportsShutdown: true,
        SupportsNativeOutput: true,
        SupportsCancellation: true);

    public ExecutionBackendAvailability Availability => ExecutionBackendAvailability.Available;

    public string? AvailabilityReason => null;

    public event Action<EditorExecutionEvent>? EventPublished
    {
        add => _adapter.EventPublished += value;
        remove => _adapter.EventPublished -= value;
    }

    public Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken)
        => _adapter.ExecuteAsync(request, cancellationToken);
}
