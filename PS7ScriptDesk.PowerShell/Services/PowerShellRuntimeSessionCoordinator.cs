using System.Diagnostics;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Phase 1 authority for the application's real PowerShell terminal session.
/// LiveConsoleService remains the protected ConPTY/process implementation; this
/// coordinator owns admission, logical identity, lifecycle state, and future
/// terminal/debugger attachment points around it.
/// </summary>
public sealed class PowerShellRuntimeSessionCoordinator : IPowerShellRuntimeSessionCoordinator
{
    private readonly ILiveConsoleService _terminal;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _syncRoot = new();
    private PowerShellRuntimeSessionSnapshot _snapshot = new(
        Guid.Empty, 0, null, PowerShellRuntimeSessionState.NotStarted, false, false, false, null, null);
    private bool _disposed;

    public PowerShellRuntimeSessionCoordinator(ILiveConsoleService terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _terminal.TerminalSessionStarted += OnTerminalStarted;
        _terminal.TerminalSessionStopping += OnTerminalStopping;
        _terminal.SessionTerminated += OnTerminalTerminated;
        DeveloperDiagnostics.LogInfo("RuntimeBroker", "PowerShell runtime session coordinator created.", Metadata());
    }

    public event Action<PowerShellRuntimeSessionSnapshot>? StateChanged;

    public PowerShellRuntimeSessionSnapshot Snapshot
    {
        get { lock (_syncRoot) return _snapshot; }
    }

    public async Task<PowerShellRuntimeSessionSnapshot> AcquireAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(onOutput);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = Snapshot;
            if (_terminal.IsSessionRunning)
            {
                if (!SameRuntime(current.Runtime, runtime))
                    throw new InvalidOperationException("The authoritative PowerShell session is already running a different runtime.");
                EnsureTerminalReady();
                if (current.State is PowerShellRuntimeSessionState.Executing or PowerShellRuntimeSessionState.Debugging ||
                    current.LifecyclePhase != PowerShellRuntimeLifecyclePhase.Stable)
                {
                    DeveloperDiagnostics.LogDecision("RuntimeBroker", "RuntimeReacquisitionPreservedOwner",
                        "Runtime reacquisition returned the existing state without overwriting active execution or lifecycle ownership.",
                        "ActiveOwner", Metadata());
                    return current;
                }

                if (current.State != PowerShellRuntimeSessionState.Ready || current.ProcessId != _terminal.ProcessId)
                    throw new InvalidOperationException("The running terminal is not in a reacquirable authoritative state.");

                return current;
            }

            var sessionId = Guid.NewGuid();
            var generation = current.Generation + 1;
            Publish(PowerShellRuntimeSessionState.Starting, sessionId, generation, runtime, null);
            try
            {
                await _terminal.StartSessionAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken).ConfigureAwait(false);
                EnsureTerminalReady();
                Publish(PowerShellRuntimeSessionState.Ready, sessionId, generation, runtime, null);
                DeveloperDiagnostics.LogInfo("RuntimeBroker", "Authoritative PowerShell runtime acquired.", Metadata());
                return Snapshot;
            }
            catch (OperationCanceledException)
            {
                Publish(PowerShellRuntimeSessionState.Stopped, Guid.Empty, generation, null, "Startup cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                Publish(PowerShellRuntimeSessionState.Faulted, sessionId, generation, runtime, ex.Message);
                DeveloperDiagnostics.LogException("RuntimeBroker", ex, "Authoritative PowerShell runtime startup failed.", Metadata());
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<PowerShellRuntimeSessionSnapshot> RestartAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var executionDrained = false;
        var resetPending = false;
        try
        {
            ThrowIfDisposed();
            var current = Snapshot;
            if (current.State is PowerShellRuntimeSessionState.NotStarted or PowerShellRuntimeSessionState.Stopped)
                return await AcquireCoreAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken).ConfigureAwait(false);

            // Reset admission is distinct from committing terminal teardown. Keep
            // the old generation writable while its current owner drains so Read-Host
            // and debugger protocol traffic can finish normally.
            Publish(current.State, current.SessionId, current.Generation, current.Runtime, current.FaultMessage,
                PowerShellRuntimeLifecyclePhase.ResetPending);
            resetPending = true;
            DeveloperDiagnostics.LogInfo("RuntimeBroker", "Runtime replacement requested; preserving active generation during lease drain.", Metadata());
            if (!_executionGate.Wait(0))
            {
                Publish(current.State, current.SessionId, current.Generation, current.Runtime, current.FaultMessage,
                    PowerShellRuntimeLifecyclePhase.Draining);
                await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            executionDrained = true;
            current = Snapshot;
            Publish(PowerShellRuntimeSessionState.Stopping, current.SessionId, current.Generation, current.Runtime, null,
                PowerShellRuntimeLifecyclePhase.TerminalShutdown);
            // The lease wait is the cancellable pre-commit boundary. Once terminal
            // teardown is published, finish that bounded operation atomically.
            var shutdownClean = await _terminal.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            if (!shutdownClean)
                throw new InvalidOperationException("The authoritative PowerShell terminal did not drain cleanly; replacement was not started.");
            Publish(PowerShellRuntimeSessionState.Stopped, Guid.Empty, current.Generation, null, null,
                PowerShellRuntimeLifecyclePhase.Replacing);
            return await AcquireCoreAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (resetPending && !executionDrained)
        {
            RestoreStablePhase("Runtime replacement was cancelled before terminal shutdown committed.");
            throw;
        }
        catch
        {
            if (resetPending) MarkLifecycleFailed("Runtime replacement failed after reset admission.");
            throw;
        }
        finally
        {
            if (executionDrained) _executionGate.Release();
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var executionDrained = false;
        var shutdownPending = false;
        try
        {
            if (_disposed) return true;
            var current = Snapshot;
            if (current.State is PowerShellRuntimeSessionState.Stopped or PowerShellRuntimeSessionState.NotStarted)
                return true;
            Publish(current.State, current.SessionId, current.Generation, current.Runtime, current.FaultMessage,
                PowerShellRuntimeLifecyclePhase.ResetPending);
            shutdownPending = true;
            // Do not dispose the authoritative terminal while an execution lease
            // still owns the execution gate. Disposal used to race the lease's
            // finally/release path and could leave work running against a torn-down
            // session or release a disposed semaphore.
            if (!_executionGate.Wait(0))
            {
                Publish(current.State, current.SessionId, current.Generation, current.Runtime, current.FaultMessage,
                    PowerShellRuntimeLifecyclePhase.Draining);
                await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            executionDrained = true;
            current = Snapshot;
            Publish(PowerShellRuntimeSessionState.Stopping, current.SessionId, current.Generation, current.Runtime, null,
                PowerShellRuntimeLifecyclePhase.TerminalShutdown);
            // Cancellation is honored while waiting for lease ownership. After
            // committing terminal shutdown, complete the terminal's bounded drain.
            var clean = await _terminal.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(clean ? PowerShellRuntimeSessionState.Stopped : PowerShellRuntimeSessionState.Faulted,
                clean ? Guid.Empty : current.SessionId, current.Generation, clean ? null : current.Runtime,
                clean ? null : "Terminal shutdown did not drain all resources.",
                clean ? PowerShellRuntimeLifecyclePhase.Stable : PowerShellRuntimeLifecyclePhase.Failed);
            DeveloperDiagnostics.LogInfo("RuntimeBroker", "Authoritative PowerShell runtime shutdown completed.", Metadata());
            return clean;
        }
        catch (OperationCanceledException) when (shutdownPending && !executionDrained)
        {
            RestoreStablePhase("Runtime shutdown was cancelled before terminal shutdown committed.");
            throw;
        }
        catch
        {
            if (shutdownPending) MarkLifecycleFailed("Runtime shutdown failed after reset admission.");
            throw;
        }
        finally
        {
            if (executionDrained) _executionGate.Release();
            _lifecycleGate.Release();
        }
    }

    public bool IsCurrent(Guid sessionId, long generation)
    {
        var current = Snapshot;
        return current.SessionId == sessionId && current.Generation == generation &&
               current.LifecyclePhase is PowerShellRuntimeLifecyclePhase.Stable or PowerShellRuntimeLifecyclePhase.ResetPending or PowerShellRuntimeLifecyclePhase.Draining &&
               current.State is PowerShellRuntimeSessionState.Ready or PowerShellRuntimeSessionState.Executing or PowerShellRuntimeSessionState.Debugging &&
               current.ProcessId.HasValue && current.InputWritable &&
               _terminal.IsSessionRunning && _terminal.IsInputWritable &&
               _terminal.ProcessId == current.ProcessId;
    }

    public async Task WriteRawInputAsync(Guid sessionId, long generation, string data, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(sessionId, generation))
        {
            DeveloperDiagnostics.LogDecision("RuntimeBroker", "TerminalInputRejected",
                "Runtime input was rejected before transport because the request or backing terminal is stale or non-writable.",
                "BeforeWrite", InputMetadata(sessionId, generation, data.Length));
            throw new InvalidOperationException("The terminal input belongs to a stale or non-writable PowerShell runtime session.");
        }

        var expectedProcessId = Snapshot.ProcessId;
        await _terminal.WriteRawInputAsync(data, cancellationToken).ConfigureAwait(false);

        if (!IsCurrent(sessionId, generation) || Snapshot.ProcessId != expectedProcessId)
        {
            DeveloperDiagnostics.LogDecision("RuntimeBroker", "TerminalInputRejected",
                "Runtime input was written to the previous transport, but the session changed before forwarding completed.",
                "ChangedAfterWrite", InputMetadata(sessionId, generation, data.Length));
            throw new InvalidOperationException("The PowerShell runtime session changed while terminal input was being forwarded.");
        }
    }

    private Dictionary<string, object?> InputMetadata(Guid requestedSessionId, long requestedGeneration, int inputLength)
    {
        var current = Snapshot;
        return new Dictionary<string, object?>
        {
            ["requestedSessionId"] = requestedSessionId,
            ["requestedGeneration"] = requestedGeneration,
            ["sessionId"] = current.SessionId,
            ["generation"] = current.Generation,
            ["runtimeState"] = current.State.ToString(),
            ["snapshotProcessId"] = current.ProcessId,
            ["snapshotInputWritable"] = current.InputWritable,
            ["terminalProcessId"] = _terminal.ProcessId,
            ["terminalRunning"] = _terminal.IsSessionRunning,
            ["terminalInputWritable"] = _terminal.IsInputWritable,
            ["inputLength"] = inputLength,
            ["contentOmitted"] = true
        };
    }

    public async Task<IPowerShellRuntimeExecutionLease> AcquireExecutionAsync(Guid sessionId, long generation, bool debugger, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(sessionId, generation) || Snapshot.LifecyclePhase != PowerShellRuntimeLifecyclePhase.Stable)
            throw new InvalidOperationException("The requested PowerShell runtime session is stale or unavailable.");
        if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            DeveloperDiagnostics.LogDecision("RuntimeBroker", "ExecutionLeaseRejected",
                "Runtime operation admission was rejected because another editor or debugger operation owns the session.",
                "ExecutionGateBusy", Metadata(new Dictionary<string, object?> { ["debugger"] = debugger }));
            throw new InvalidOperationException("Another editor or debugger operation already owns the PowerShell runtime session.");
        }
        if (!TryPublishExecutionAdmission(sessionId, generation, debugger))
        {
            _executionGate.Release();
            throw new InvalidOperationException("The PowerShell runtime was replaced or entered reset admission before execution admission.");
        }
        DeveloperDiagnostics.LogInfo("RuntimeBroker", "Runtime execution admitted.", Metadata(new Dictionary<string, object?> { ["debugger"] = debugger }));
        return new ExecutionLease(this, sessionId, generation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await ShutdownAsync().ConfigureAwait(false); }
        finally
        {
            _terminal.TerminalSessionStarted -= OnTerminalStarted;
            _terminal.TerminalSessionStopping -= OnTerminalStopping;
            _terminal.SessionTerminated -= OnTerminalTerminated;
            lock (_syncRoot) _disposed = true;
            _lifecycleGate.Dispose();
            _executionGate.Dispose();
        }
    }

    private async Task<PowerShellRuntimeSessionSnapshot> AcquireCoreAsync(PowerShellRuntimeInfo runtime, Action<ExecutionOutputRecord> onOutput, string? startupWorkingDirectory, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        var generation = Snapshot.Generation + 1;
        Publish(PowerShellRuntimeSessionState.Starting, sessionId, generation, runtime, null,
            PowerShellRuntimeLifecyclePhase.Replacing);
        try
        {
            await _terminal.StartSessionAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken).ConfigureAwait(false);
            EnsureTerminalReady();
            Publish(PowerShellRuntimeSessionState.Ready, sessionId, generation, runtime, null,
                PowerShellRuntimeLifecyclePhase.Stable);
            return Snapshot;
        }
        catch (Exception ex)
        {
            Publish(ex is OperationCanceledException ? PowerShellRuntimeSessionState.Stopped : PowerShellRuntimeSessionState.Faulted,
                ex is OperationCanceledException ? Guid.Empty : sessionId, generation, ex is OperationCanceledException ? null : runtime, ex.Message,
                ex is OperationCanceledException ? PowerShellRuntimeLifecyclePhase.Stable : PowerShellRuntimeLifecyclePhase.Failed);
            throw;
        }
    }

    private void OnTerminalStarted(int terminalGeneration)
    {
        var current = Snapshot;
        if (current.State != PowerShellRuntimeSessionState.Starting || !_terminal.IsSessionRunning || !_terminal.IsInputWritable || !_terminal.ProcessId.HasValue)
        {
            DeveloperDiagnostics.LogWarning("RuntimeBroker", "Ignored terminal-start notification until the terminal is writable.", Metadata(new Dictionary<string, object?>
            {
                ["terminalGeneration"] = terminalGeneration,
                ["terminalRunning"] = _terminal.IsSessionRunning,
                ["inputWritable"] = _terminal.IsInputWritable,
                ["terminalProcessId"] = _terminal.ProcessId
            }));
            return;
        }

        Publish(PowerShellRuntimeSessionState.Ready, current.SessionId, current.Generation, current.Runtime, null);
    }
    private void OnTerminalStopping(int _) { if (Snapshot.State is not PowerShellRuntimeSessionState.Stopping) Publish(PowerShellRuntimeSessionState.Stopping, Snapshot.SessionId, Snapshot.Generation, Snapshot.Runtime, null); }
    private void OnTerminalTerminated() { var current = Snapshot; if (current.State is not PowerShellRuntimeSessionState.Stopping) Publish(PowerShellRuntimeSessionState.Faulted, current.SessionId, current.Generation, current.Runtime, "The authoritative PowerShell process terminated unexpectedly."); }

    private void Publish(PowerShellRuntimeSessionState state, Guid sessionId, long generation, PowerShellRuntimeInfo? runtime, string? fault,
        PowerShellRuntimeLifecyclePhase? lifecyclePhase = null)
    {
        PowerShellRuntimeSessionSnapshot next;
        PowerShellRuntimeSessionState previousState;
        lock (_syncRoot)
        {
            previousState = _snapshot.State;
            next = new(sessionId, generation, _terminal.ProcessId, state, _terminal.IsHostAttached, _terminal.IsInputWritable, _snapshot.DebuggerTransportAttached, runtime, fault,
                lifecyclePhase ?? _snapshot.LifecyclePhase);
            _snapshot = next;
        }
        NotifyStateChanged(next, previousState, state);
    }

    private bool TryPublishExecutionAdmission(Guid sessionId, long generation, bool debugger)
    {
        PowerShellRuntimeSessionSnapshot next;
        PowerShellRuntimeSessionState previousState;
        var state = debugger ? PowerShellRuntimeSessionState.Debugging : PowerShellRuntimeSessionState.Executing;
        lock (_syncRoot)
        {
            var current = _snapshot;
            if (current.SessionId != sessionId || current.Generation != generation ||
                current.LifecyclePhase != PowerShellRuntimeLifecyclePhase.Stable ||
                current.State != PowerShellRuntimeSessionState.Ready ||
                !_terminal.IsSessionRunning || !_terminal.IsInputWritable ||
                !_terminal.ProcessId.HasValue || _terminal.ProcessId != current.ProcessId)
            {
                return false;
            }

            previousState = current.State;
            next = new(current.SessionId, current.Generation, _terminal.ProcessId, state,
                _terminal.IsHostAttached, _terminal.IsInputWritable, current.DebuggerTransportAttached,
                current.Runtime, null, PowerShellRuntimeLifecyclePhase.Stable);
            _snapshot = next;
        }

        NotifyStateChanged(next, previousState, state);
        return true;
    }

    private void NotifyStateChanged(PowerShellRuntimeSessionSnapshot next,
        PowerShellRuntimeSessionState previousState, PowerShellRuntimeSessionState state)
    {
        DeveloperDiagnostics.LogStateTransition("RuntimeBroker", "PowerShellRuntimeSession", previousState.ToString(), state.ToString(), "Runtime broker state changed.", Metadata());
        try { StateChanged?.Invoke(next); } catch (Exception ex) { AppLogger.Warning("RuntimeBroker", $"Runtime state subscriber failed: {ex.GetType().Name}."); }
    }

    private Dictionary<string, object?> Metadata(Dictionary<string, object?>? extra = null)
    {
        var s = Snapshot;
        var result = new Dictionary<string, object?> { ["sessionId"] = s.SessionId, ["generation"] = s.Generation, ["processId"] = s.ProcessId, ["state"] = s.State.ToString(), ["lifecyclePhase"] = s.LifecyclePhase.ToString(), ["terminalAttached"] = s.TerminalTransportAttached, ["inputWritable"] = s.InputWritable, ["debuggerAttached"] = s.DebuggerTransportAttached };
        if (extra is not null) foreach (var pair in extra) result[pair.Key] = pair.Value;
        return result;
    }

    private static bool SameRuntime(PowerShellRuntimeInfo? left, PowerShellRuntimeInfo right) => left is not null && string.Equals(left.LaunchExecutablePath, right.LaunchExecutablePath, StringComparison.OrdinalIgnoreCase);
    private void EnsureTerminalReady()
    {
        if (!_terminal.IsSessionRunning || !_terminal.IsInputWritable || !_terminal.ProcessId.HasValue)
            throw new InvalidOperationException("The authoritative PowerShell terminal did not reach a writable ready state.");
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PowerShellRuntimeSessionCoordinator)); }

    private void RestoreStablePhase(string reason)
    {
        var current = Snapshot;
        Publish(current.State, current.SessionId, current.Generation, current.Runtime, current.FaultMessage,
            PowerShellRuntimeLifecyclePhase.Stable);
        DeveloperDiagnostics.LogDecision("RuntimeBroker", "LifecycleWaitCancelled", reason, "RestoredPriorSession", Metadata());
    }

    private void MarkLifecycleFailed(string reason)
    {
        var current = Snapshot;
        Publish(PowerShellRuntimeSessionState.Faulted, current.SessionId, current.Generation, current.Runtime, reason,
            PowerShellRuntimeLifecyclePhase.Failed);
    }

    private sealed class ExecutionLease : IPowerShellRuntimeExecutionLease
    {
        private readonly PowerShellRuntimeSessionCoordinator _owner;
        private readonly Guid _sessionId;
        private readonly long _generation;
        private int _released;
        private string? _readinessUnverifiedReason;
        public ExecutionLease(PowerShellRuntimeSessionCoordinator owner, Guid sessionId, long generation) { _owner = owner; _sessionId = sessionId; _generation = generation; }
        public void MarkReadinessUnverified(string reason)
            => Interlocked.CompareExchange(ref _readinessUnverifiedReason, string.IsNullOrWhiteSpace(reason) ? "Execution readiness was not verified." : reason, null);
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                var current = _owner.Snapshot;
                if (current.SessionId == _sessionId && current.Generation == _generation &&
                    current.State is PowerShellRuntimeSessionState.Executing or PowerShellRuntimeSessionState.Debugging)
                {
                    var unverifiedReason = Volatile.Read(ref _readinessUnverifiedReason);
                    if (unverifiedReason is null && _owner.IsCurrent(_sessionId, _generation))
                        _owner.Publish(PowerShellRuntimeSessionState.Ready, _sessionId, _generation, current.Runtime, null);
                    else
                        _owner.Publish(PowerShellRuntimeSessionState.Faulted, _sessionId, _generation, current.Runtime,
                            unverifiedReason ?? "The PowerShell input transport is no longer writable after execution.");
                }
                _owner._executionGate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
