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
                Publish(PowerShellRuntimeSessionState.Ready, current.SessionId, current.Generation, current.Runtime, null);
                return Snapshot;
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
        try
        {
            ThrowIfDisposed();
            var current = Snapshot;
            Publish(PowerShellRuntimeSessionState.Stopping, current.SessionId, current.Generation, current.Runtime, null);
            // Replacement must honor the same execution admission boundary as
            // shutdown; otherwise an active structured/debug execution can be
            // torn down underneath its lease.
            await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            executionDrained = true;
            await _terminal.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            Publish(PowerShellRuntimeSessionState.Stopped, Guid.Empty, current.Generation, null, null);
            return await AcquireCoreAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken).ConfigureAwait(false);
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
        try
        {
            if (_disposed) return true;
            var current = Snapshot;
            if (current.State is PowerShellRuntimeSessionState.Stopped or PowerShellRuntimeSessionState.NotStarted)
                return true;
            Publish(PowerShellRuntimeSessionState.Stopping, current.SessionId, current.Generation, current.Runtime, null);
            // Do not dispose the authoritative terminal while an execution lease
            // still owns the execution gate. Disposal used to race the lease's
            // finally/release path and could leave work running against a torn-down
            // session or release a disposed semaphore.
            await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            executionDrained = true;
            var clean = await _terminal.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            Publish(clean ? PowerShellRuntimeSessionState.Stopped : PowerShellRuntimeSessionState.Faulted,
                clean ? Guid.Empty : current.SessionId, current.Generation, clean ? null : current.Runtime,
                clean ? null : "Terminal shutdown did not drain all resources.");
            DeveloperDiagnostics.LogInfo("RuntimeBroker", "Authoritative PowerShell runtime shutdown completed.", Metadata());
            return clean;
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
               current.State is PowerShellRuntimeSessionState.Ready or PowerShellRuntimeSessionState.Executing or PowerShellRuntimeSessionState.Debugging &&
               current.ProcessId.HasValue && current.InputWritable;
    }

    public async Task WriteRawInputAsync(Guid sessionId, long generation, string data, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(sessionId, generation))
            throw new InvalidOperationException("The terminal input belongs to a stale or non-writable PowerShell runtime session.");

        var expectedProcessId = Snapshot.ProcessId;
        await _terminal.WriteRawInputAsync(data, cancellationToken).ConfigureAwait(false);

        if (!IsCurrent(sessionId, generation) || Snapshot.ProcessId != expectedProcessId)
            throw new InvalidOperationException("The PowerShell runtime session changed while terminal input was being forwarded.");
    }

    public async Task<IAsyncDisposable> AcquireExecutionAsync(Guid sessionId, long generation, bool debugger, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(sessionId, generation))
            throw new InvalidOperationException("The requested PowerShell runtime session is stale or unavailable.");
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(sessionId, generation))
        {
            _executionGate.Release();
            throw new InvalidOperationException("The PowerShell runtime was replaced before execution admission.");
        }
        Publish(debugger ? PowerShellRuntimeSessionState.Debugging : PowerShellRuntimeSessionState.Executing,
            sessionId, generation, Snapshot.Runtime, null);
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
        Publish(PowerShellRuntimeSessionState.Starting, sessionId, generation, runtime, null);
        try
        {
            await _terminal.StartSessionAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken).ConfigureAwait(false);
            EnsureTerminalReady();
            Publish(PowerShellRuntimeSessionState.Ready, sessionId, generation, runtime, null);
            return Snapshot;
        }
        catch (Exception ex)
        {
            Publish(ex is OperationCanceledException ? PowerShellRuntimeSessionState.Stopped : PowerShellRuntimeSessionState.Faulted,
                ex is OperationCanceledException ? Guid.Empty : sessionId, generation, ex is OperationCanceledException ? null : runtime, ex.Message);
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

    private void Publish(PowerShellRuntimeSessionState state, Guid sessionId, long generation, PowerShellRuntimeInfo? runtime, string? fault)
    {
        PowerShellRuntimeSessionSnapshot next;
        PowerShellRuntimeSessionState previousState;
        lock (_syncRoot)
        {
            previousState = _snapshot.State;
            next = new(sessionId, generation, _terminal.ProcessId, state, _terminal.IsHostAttached, _terminal.IsInputWritable, _snapshot.DebuggerTransportAttached, runtime, fault);
            _snapshot = next;
        }
        DeveloperDiagnostics.LogStateTransition("RuntimeBroker", "PowerShellRuntimeSession", previousState.ToString(), state.ToString(), "Runtime broker state changed.", Metadata());
        try { StateChanged?.Invoke(next); } catch (Exception ex) { AppLogger.Warning("RuntimeBroker", $"Runtime state subscriber failed: {ex.GetType().Name}."); }
    }

    private Dictionary<string, object?> Metadata(Dictionary<string, object?>? extra = null)
    {
        var s = Snapshot;
        var result = new Dictionary<string, object?> { ["sessionId"] = s.SessionId, ["generation"] = s.Generation, ["processId"] = s.ProcessId, ["state"] = s.State.ToString(), ["terminalAttached"] = s.TerminalTransportAttached, ["inputWritable"] = s.InputWritable, ["debuggerAttached"] = s.DebuggerTransportAttached };
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

    private sealed class ExecutionLease : IAsyncDisposable
    {
        private readonly PowerShellRuntimeSessionCoordinator _owner;
        private readonly Guid _sessionId;
        private readonly long _generation;
        private int _released;
        public ExecutionLease(PowerShellRuntimeSessionCoordinator owner, Guid sessionId, long generation) { _owner = owner; _sessionId = sessionId; _generation = generation; }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                var current = _owner.Snapshot;
                if (_owner.IsCurrent(_sessionId, _generation)) _owner.Publish(PowerShellRuntimeSessionState.Ready, _sessionId, _generation, current.Runtime, null);
                _owner._executionGate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
