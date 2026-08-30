using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Owns one persistent semantic PowerShell runspace for structured editor execution.
/// It deliberately has no terminal, ConPTY, WebView2, or WPF dependency.
/// </summary>
public sealed class PersistentPowerShellSessionBroker : IEditorExecutionBroker
{
    private readonly SemaphoreSlim _executionAdmission = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _syncRoot = new();
    private readonly string _runtimeIdentity;
    private Runspace? _runspace;
    private int _sessionGeneration = 1;
    private long _eventSequence;
    private PersistentSessionLifecycle _lifecycle = PersistentSessionLifecycle.Created;
    private Guid? _activeRequestId;
    private System.Management.Automation.PowerShell? _activePowerShell;
    private bool _disposed;
    private bool _shutdownRequested;

    private PersistentPowerShellSessionBroker(string runtimeIdentity)
    {
        _runtimeIdentity = string.IsNullOrWhiteSpace(runtimeIdentity) ? "PowerShell runspace" : runtimeIdentity.Trim();
        _runspace = CreateRunspace();
        _lifecycle = PersistentSessionLifecycle.Ready;
    }

    public event Action<EditorExecutionEvent>? EventPublished;

    public PersistentSessionSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return new PersistentSessionSnapshot(
                    _sessionGeneration,
                    _lifecycle,
                    _activeRequestId,
                    TryGetCurrentWorkingDirectory(),
                    _activeRequestId.HasValue,
                    _runtimeIdentity);
            }
        }
    }

    public static Task<PersistentPowerShellSessionBroker> CreateAsync(string runtimeIdentity = "PowerShell runspace")
    {
        return Task.FromResult(new PersistentPowerShellSessionBroker(runtimeIdentity));
    }

    public async Task<EditorExecutionResult> ExecuteAsync(
        EditorExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = DateTimeOffset.UtcNow;
        if (request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("An editor execution request must have a non-empty request ID.", nameof(request));
        }

        if (!IsCurrentGeneration(request.SessionGeneration))
        {
            return Reject(request, startedAt, "The editor execution request belongs to a stale session generation.");
        }

        if (ContainsUnsupportedInteractiveInput(request.ScriptText))
        {
            AppLogger.Warning("EditorExecutionBroker", $"Structured editor execution rejected because host input is not supported. RequestId={request.RequestId:N}, SessionGeneration={request.SessionGeneration}, ContentOmitted=True.");
            return Reject(request, startedAt, "Structured editor execution does not support Read-Host yet. No interactive terminal input was used.");
        }

        try
        {
            await _executionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PublishCancelled(request, startedAt, "Editor execution was cancelled before admission.");
        }

        EditorExecutionArtifact? artifact = null;
        try
        {
            if (!TryBeginExecution(request.SessionGeneration, request.RequestId))
            {
                return Reject(request, startedAt, "The persistent PowerShell session is unavailable.");
            }

            PublishLifecycle(EditorExecutionEventKind.Accepted, request, null, null, null);
            PublishLifecycle(EditorExecutionEventKind.Started, request, null, null, null);

            try
            {
                artifact = EditorExecutionArtifactStore.Prepare(request);
                var invocation = await InvokeAsync(request, artifact, cancellationToken).ConfigureAwait(false);
                var endedAt = DateTimeOffset.UtcNow;
                var currentDirectory = TryGetCurrentWorkingDirectory();

                if (!string.Equals(currentDirectory, invocation.DirectoryBefore, StringComparison.OrdinalIgnoreCase))
                {
                    PublishLifecycle(
                        EditorExecutionEventKind.WorkingDirectoryChanged,
                        request,
                        null,
                        currentDirectory,
                        null);
                }

                if (invocation.WasCancelled || cancellationToken.IsCancellationRequested)
                {
                    PublishLifecycle(EditorExecutionEventKind.Cancelled, request, null, currentDirectory, "Editor execution was cancelled.");
                    return new EditorExecutionResult(
                        request.RequestId,
                        request.SessionGeneration,
                        EditorExecutionStatus.Cancelled,
                        invocation.Outputs,
                        currentDirectory,
                        artifact,
                        "Editor execution was cancelled.",
                        startedAt,
                        endedAt);
                }

                if (invocation.ErrorMessage is not null)
                {
                    PublishLifecycle(EditorExecutionEventKind.Failed, request, null, currentDirectory, invocation.ErrorMessage);
                    return new EditorExecutionResult(
                        request.RequestId,
                        request.SessionGeneration,
                        EditorExecutionStatus.Failed,
                        invocation.Outputs,
                        currentDirectory,
                        artifact,
                        invocation.ErrorMessage,
                        startedAt,
                        endedAt);
                }

                PublishLifecycle(EditorExecutionEventKind.Completed, request, null, currentDirectory, null);
                return new EditorExecutionResult(
                    request.RequestId,
                    request.SessionGeneration,
                    EditorExecutionStatus.Completed,
                    invocation.Outputs,
                    currentDirectory,
                    artifact,
                    null,
                    startedAt,
                    endedAt);
            }
            catch (OperationCanceledException)
            {
                return PublishCancelled(request, startedAt, "Editor execution was cancelled.", artifact);
            }
            catch (Exception ex)
            {
                AppLogger.Error("EditorExecutionBroker", "Structured editor execution failed.", ex);
                PublishLifecycle(EditorExecutionEventKind.Failed, request, null, TryGetCurrentWorkingDirectory(), ex.GetType().Name);
                return new EditorExecutionResult(
                    request.RequestId,
                    request.SessionGeneration,
                    EditorExecutionStatus.Failed,
                    [],
                    TryGetCurrentWorkingDirectory(),
                    artifact,
                    ex.Message,
                    startedAt,
                    DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            if (artifact is not null && artifact.DeleteAfterRun)
            {
                EditorExecutionArtifactStore.TryDelete(artifact.ExecutionPath);
            }

            PersistentSessionLifecycle finalLifecycle;
            lock (_syncRoot)
            {
                finalLifecycle = _disposed
                    ? PersistentSessionLifecycle.Disposed
                    : _shutdownRequested
                        ? PersistentSessionLifecycle.ShuttingDown
                        : _lifecycle == PersistentSessionLifecycle.Restarting
                            ? PersistentSessionLifecycle.Restarting
                        : PersistentSessionLifecycle.Ready;
            }
            SetActive(null, finalLifecycle);
            _executionAdmission.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        BeginLifecycleTransition(PersistentSessionLifecycle.Restarting, shutdown: false);
        RequestStopActiveInvocation();
        try
        {
            await _executionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RestoreAfterCancelledTransition();
            throw;
        }
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _runspace?.Dispose();
                _runspace = CreateRunspace();
                lock (_syncRoot)
                {
                    _sessionGeneration++;
                    _lifecycle = PersistentSessionLifecycle.Ready;
                    _activeRequestId = null;
                }

                AppLogger.Info("EditorExecutionBroker", $"Persistent PowerShell session restarted. SessionGeneration={Snapshot.SessionGeneration}.");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _executionAdmission.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed())
        {
            return;
        }

        BeginLifecycleTransition(PersistentSessionLifecycle.ShuttingDown, shutdown: true);
        RequestStopActiveInvocation();
        try
        {
            await _executionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RestoreAfterCancelledTransition();
            throw;
        }
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _runspace?.Dispose();
                _runspace = null;
                lock (_syncRoot)
                {
                    _activeRequestId = null;
                    _lifecycle = PersistentSessionLifecycle.Disposed;
                    _disposed = true;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _executionAdmission.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed())
        {
            return;
        }

        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            _executionAdmission.Dispose();
            _lifecycleGate.Dispose();
        }
    }

    private async Task<InvocationResult> InvokeAsync(
        EditorExecutionRequest request,
        EditorExecutionArtifact artifact,
        CancellationToken cancellationToken)
    {
        var runspace = _runspace ?? throw new InvalidOperationException("The PowerShell runspace is unavailable.");
        var directoryBefore = TryGetCurrentWorkingDirectory();
        var outputs = new List<EditorOutputRecord>();
        var powerShell = System.Management.Automation.PowerShell.Create();
        powerShell.Runspace = runspace;
        lock (_syncRoot)
        {
            _activePowerShell = powerShell;
        }
        var currentScope = request.Mode == EditorExecutionMode.CurrentScope || request.ExecuteInCurrentScope;
        var invocationOperator = currentScope ? "." : "&";
        var script = BuildInvocationScript(request, artifact, invocationOperator);
        powerShell.AddScript(script, useLocalScope: !currentScope);

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            try
            {
                ((System.Management.Automation.PowerShell)state!).Stop();
            }
            catch
            {
                // Stop is best effort; the invocation state remains authoritative.
            }
        }, powerShell);

        try
        {
            var invocation = await Task.Run(() => powerShell.Invoke(), CancellationToken.None).ConfigureAwait(false);
            AddOutput(outputs, request, EditorOutputStreamKind.Success, invocation);
            AddOutput(outputs, request, EditorOutputStreamKind.Warning, powerShell.Streams.Warning.Select(item => item.Message));
            AddOutput(outputs, request, EditorOutputStreamKind.Verbose, powerShell.Streams.Verbose.Select(item => item.Message));
            AddOutput(outputs, request, EditorOutputStreamKind.Debug, powerShell.Streams.Debug.Select(item => item.Message));
            AddOutput(outputs, request, EditorOutputStreamKind.Information, powerShell.Streams.Information.Select(item => item.MessageData?.ToString() ?? string.Empty));
            AddOutput(outputs, request, EditorOutputStreamKind.Error, powerShell.Streams.Error.Select(item => item.ToString()));

            return new InvocationResult(
                outputs,
                directoryBefore,
                powerShell.InvocationStateInfo.State == PSInvocationState.Stopped,
                powerShell.HadErrors && powerShell.InvocationStateInfo.State == PSInvocationState.Failed
                    ? "PowerShell reported a terminating execution failure."
                    : null);
        }
        catch (PipelineStoppedException)
        {
            return new InvocationResult(outputs, directoryBefore, true, null);
        }
        catch (RuntimeException ex)
        {
            AddOutput(outputs, request, EditorOutputStreamKind.Error, [ex.Message]);
            return new InvocationResult(outputs, directoryBefore, false, ex.Message);
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_activePowerShell, powerShell))
                {
                    _activePowerShell = null;
                }
            }
            powerShell.Dispose();
        }
    }

    private void AddOutput(List<EditorOutputRecord> outputs, EditorExecutionRequest request, EditorOutputStreamKind kind, IEnumerable<string> payloads)
    {
        foreach (var payload in payloads)
        {
            if (string.IsNullOrEmpty(payload))
            {
                continue;
            }

            var record = new EditorOutputRecord(
                request.RequestId,
                request.SessionGeneration,
                Interlocked.Increment(ref _eventSequence),
                kind,
                payload,
                DateTimeOffset.UtcNow);
            outputs.Add(record);
            PublishLifecycle(EditorExecutionEventKind.Output, request, record, null, null);
        }
    }

    private void AddOutput(List<EditorOutputRecord> outputs, EditorExecutionRequest request, EditorOutputStreamKind kind, Collection<PSObject> payloads)
    {
        AddOutput(outputs, request, kind, payloads.Select(item => item?.BaseObject?.ToString() ?? string.Empty));
    }

    private string BuildInvocationScript(EditorExecutionRequest request, EditorExecutionArtifact artifact, string invocationOperator)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            builder.Append("Set-Location ").Append(Quote(request.WorkingDirectory!)).AppendLine();
        }

        builder.Append(invocationOperator).Append(' ').Append(Quote(artifact.ExecutionPath));
        return builder.ToString();
    }

    private void PublishLifecycle(
        EditorExecutionEventKind kind,
        EditorExecutionRequest request,
        EditorOutputRecord? output,
        string? workingDirectory,
        string? errorMessage)
    {
        var published = new EditorExecutionEvent(
            kind,
            request.RequestId,
            request.SessionGeneration,
            Interlocked.Increment(ref _eventSequence),
            output,
            workingDirectory,
            errorMessage,
            DateTimeOffset.UtcNow);

        var handlers = EventPublished;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<EditorExecutionEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(published);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("EditorExecutionBroker", $"Structured execution event subscriber failed. Event={kind}, RequestId={request.RequestId:N}, ErrorType={ex.GetType().Name}.");
            }
        }
    }

    private EditorExecutionResult Reject(EditorExecutionRequest request, DateTimeOffset startedAt, string reason)
    {
        PublishLifecycle(EditorExecutionEventKind.Failed, request, null, null, reason);
        return new EditorExecutionResult(
            request.RequestId,
            request.SessionGeneration,
            EditorExecutionStatus.Rejected,
            [],
            TryGetCurrentWorkingDirectory(),
            null,
            reason,
            startedAt,
            DateTimeOffset.UtcNow);
    }

    private EditorExecutionResult PublishCancelled(EditorExecutionRequest request, DateTimeOffset startedAt, string reason, EditorExecutionArtifact? artifact = null)
    {
        PublishLifecycle(EditorExecutionEventKind.Cancelled, request, null, TryGetCurrentWorkingDirectory(), reason);
        return new EditorExecutionResult(
            request.RequestId,
            request.SessionGeneration,
            EditorExecutionStatus.Cancelled,
            [],
            TryGetCurrentWorkingDirectory(),
            artifact,
            reason,
            startedAt,
            DateTimeOffset.UtcNow);
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_syncRoot)
        {
            return generation == _sessionGeneration;
        }
    }

    private bool TryBeginExecution(int generation, Guid requestId)
    {
        lock (_syncRoot)
        {
            if (_disposed ||
                _shutdownRequested ||
                _lifecycle != PersistentSessionLifecycle.Ready ||
                generation != _sessionGeneration ||
                _runspace is null)
            {
                return false;
            }

            _activeRequestId = requestId;
            _lifecycle = PersistentSessionLifecycle.Executing;
            return true;
        }
    }

    private void SetActive(Guid? requestId, PersistentSessionLifecycle lifecycle)
    {
        lock (_syncRoot)
        {
            _activeRequestId = requestId;
            _lifecycle = lifecycle;
        }
    }

    private void BeginLifecycleTransition(PersistentSessionLifecycle lifecycle, bool shutdown)
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _shutdownRequested = shutdown;
            _lifecycle = lifecycle;
        }
    }

    private void RestoreAfterCancelledTransition()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _shutdownRequested = false;
            _lifecycle = _activeRequestId.HasValue
                ? PersistentSessionLifecycle.Executing
                : PersistentSessionLifecycle.Ready;
        }
    }

    private bool IsDisposed()
    {
        lock (_syncRoot)
        {
            return _disposed;
        }
    }

    private void RequestStopActiveInvocation()
    {
        System.Management.Automation.PowerShell? activePowerShell;
        lock (_syncRoot)
        {
            activePowerShell = _activePowerShell;
        }

        if (activePowerShell is null)
        {
            return;
        }

        try
        {
            activePowerShell.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Warning("EditorExecutionBroker", $"Unable to stop the active structured execution during lifecycle transition. ErrorType={ex.GetType().Name}.");
        }
    }

    private string? TryGetCurrentWorkingDirectory()
    {
        try
        {
            return _runspace?.SessionStateProxy.Path.CurrentLocation.ProviderPath;
        }
        catch
        {
            return null;
        }
    }

    private static Runspace CreateRunspace()
    {
        var initialSessionState = InitialSessionState.CreateDefault2();
        initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(initialSessionState);
        runspace.Open();
        return runspace;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static bool ContainsUnsupportedInteractiveInput(string? scriptText)
    {
        return !string.IsNullOrWhiteSpace(scriptText) &&
               scriptText.Contains("Read-Host", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PersistentPowerShellSessionBroker));
            }
        }
    }

    private sealed record InvocationResult(
        IReadOnlyList<EditorOutputRecord> Outputs,
        string? DirectoryBefore,
        bool WasCancelled,
        string? ErrorMessage);
}

public sealed class StructuredEditorExecutionAdapter : IEditorExecutionAdapter
{
    private readonly IEditorExecutionBroker _broker;
    private readonly EditorExecutionFeatureGate _featureGate;

    public StructuredEditorExecutionAdapter(IEditorExecutionBroker broker, EditorExecutionFeatureGate featureGate)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    }

    public PersistentSessionSnapshot Snapshot => _broker.Snapshot;

    public event Action<EditorExecutionEvent>? EventPublished
    {
        add => _broker.EventPublished += value;
        remove => _broker.EventPublished -= value;
    }

    public Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_featureGate.IsStructuredExecutionEnabled)
        {
            throw new InvalidOperationException("Structured editor execution is disabled by its feature gate. No legacy terminal fallback is attempted.");
        }

        return _broker.ExecuteAsync(request, cancellationToken);
    }
}

internal static class EditorExecutionArtifactStore
{
    private static readonly string RootDirectory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution");

    public static EditorExecutionArtifact Prepare(EditorExecutionRequest request)
    {
        if (!request.IsRunSelection && request.IsSavedClean && !string.IsNullOrWhiteSpace(request.SavedScriptPath))
        {
            var savedPath = Path.GetFullPath(request.SavedScriptPath!);
            if (File.Exists(savedPath))
            {
                return new EditorExecutionArtifact(request.RequestId, request.SessionGeneration, savedPath, savedPath, false, false);
            }
        }

        Directory.CreateDirectory(RootDirectory);
        var safeName = string.IsNullOrWhiteSpace(request.DocumentDisplayName)
            ? "Untitled"
            : Path.GetFileNameWithoutExtension(request.DocumentDisplayName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalid, '_');
        }

        var path = Path.Combine(RootDirectory, $"editor-{request.RequestId:N}-{safeName}.ps1");
        File.WriteAllText(path, request.ScriptText ?? string.Empty, new UTF8Encoding(false));
        return new EditorExecutionArtifact(
            request.RequestId,
            request.SessionGeneration,
            path,
            request.SavedScriptPath,
            true,
            true);
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Warning("EditorExecutionBroker", $"Unable to delete structured editor execution artifact. PathHash={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12]}, ErrorType={ex.GetType().Name}.");
        }
    }
}
