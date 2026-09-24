using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Canonical adapter over the existing interactive terminal path.</summary>
public sealed class LegacyLiveConsoleExecutionBackend : IEditorExecutionBackend
{
    private readonly ILiveConsoleService _liveConsoleService;

    public LegacyLiveConsoleExecutionBackend(ILiveConsoleService liveConsoleService)
    {
        _liveConsoleService = liveConsoleService ?? throw new ArgumentNullException(nameof(liveConsoleService));
    }

    public string BackendId => "legacy-live-console";

    public ExecutionBackendCapabilities Capabilities { get; } = new(
        SupportsInteractiveInput: true,
        SupportsSecureInput: true,
        SupportsCurrentScope: true,
        SupportsScriptCallIsolation: false,
        SupportsTerminalInterrupt: true,
        SupportsStructuredStreams: false,
        SupportsWorkingDirectoryTracking: true,
        SupportsRestart: true,
        SupportsShutdown: true,
        SupportsNativeOutput: true,
        SupportsCancellation: false);

    public ExecutionBackendAvailability Availability => ExecutionBackendAvailability.Available;

    public string? AvailabilityReason => null;

    public event Action<EditorExecutionEvent>? EventPublished;

    public async Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var outputs = new List<EditorOutputRecord>();
        var sequence = 0L;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted() => completion.TrySetResult();
        void OnOutput(ExecutionOutputRecord output)
        {
            var stream = output.StreamKind switch
            {
                ExecutionOutputStreamKind.StandardError => EditorOutputStreamKind.Error,
                ExecutionOutputStreamKind.StandardOutput => EditorOutputStreamKind.Success,
                _ => EditorOutputStreamKind.Host
            };
            var record = new EditorOutputRecord(request.RequestId, request.SessionGeneration, Interlocked.Increment(ref sequence), stream, output.Text, new DateTimeOffset(output.Timestamp));
            outputs.Add(record);
            Publish(new EditorExecutionEvent(EditorExecutionEventKind.Output, request.RequestId, request.SessionGeneration, record.Sequence, record, Timestamp: record.Timestamp, BackendId: BackendId));
        }

        _liveConsoleService.ScriptExecutionCompleted += OnCompleted;
        try
        {
            Publish(new EditorExecutionEvent(EditorExecutionEventKind.Accepted, request.RequestId, request.SessionGeneration, 0, Timestamp: startedAt, BackendId: BackendId));
            Publish(new EditorExecutionEvent(EditorExecutionEventKind.Started, request.RequestId, request.SessionGeneration, 0, Timestamp: startedAt, BackendId: BackendId));
            var command = await _liveConsoleService.ExecuteScriptAsync(
                request.DocumentDisplayName,
                request.ScriptText,
                OnOutput,
                request.ExecuteInCurrentScope,
                cancellationToken).ConfigureAwait(false);
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var endedAt = DateTimeOffset.UtcNow;
            var result = new EditorExecutionResult(
                request.RequestId,
                request.SessionGeneration,
                command.WasStopped ? EditorExecutionStatus.Cancelled : EditorExecutionStatus.Completed,
                outputs.ToArray(),
                command.CurrentWorkingDirectory,
                null,
                null,
                startedAt,
                endedAt,
                BackendId);
            Publish(new EditorExecutionEvent(command.WasStopped ? EditorExecutionEventKind.Cancelled : EditorExecutionEventKind.Completed, request.RequestId, request.SessionGeneration, sequence, Timestamp: endedAt, BackendId: BackendId));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await _liveConsoleService.StopConsoleAsync(OnOutput).ConfigureAwait(false); } catch { }
            var endedAt = DateTimeOffset.UtcNow;
            Publish(new EditorExecutionEvent(EditorExecutionEventKind.Cancelled, request.RequestId, request.SessionGeneration, sequence, Timestamp: endedAt, BackendId: BackendId));
            return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Cancelled, outputs.ToArray(), _liveConsoleService.CurrentWorkingDirectory, null, "Execution canceled.", startedAt, endedAt, BackendId);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("Execution", ex, "Legacy execution backend failed.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["scriptLength"] = request.ScriptText?.Length ?? 0 });
            var endedAt = DateTimeOffset.UtcNow;
            Publish(new EditorExecutionEvent(EditorExecutionEventKind.Failed, request.RequestId, request.SessionGeneration, sequence, ErrorMessage: ex.Message, Timestamp: endedAt, BackendId: BackendId));
            return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Failed, outputs.ToArray(), _liveConsoleService.CurrentWorkingDirectory, null, ex.Message, startedAt, endedAt, BackendId);
        }
        finally
        {
            _liveConsoleService.ScriptExecutionCompleted -= OnCompleted;
        }
    }

    private void Publish(EditorExecutionEvent executionEvent) => EventPublished?.Invoke(executionEvent);
}
