using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public enum PowerShellRuntimeSessionState
{
    NotStarted,
    Starting,
    Ready,
    Executing,
    Debugging,
    Stopping,
    Faulted,
    Stopped
}

/// <summary>Lifecycle intent is separate from terminal readiness so a reset can drain an active lease without disabling its input.</summary>
public enum PowerShellRuntimeLifecyclePhase
{
    Stable,
    ResetPending,
    Draining,
    TerminalShutdown,
    Replacing,
    Failed
}

public sealed record PowerShellRuntimeSessionSnapshot(
    Guid SessionId,
    long Generation,
    int? ProcessId,
    PowerShellRuntimeSessionState State,
    bool TerminalTransportAttached,
    bool InputWritable,
    bool DebuggerTransportAttached,
    PowerShellRuntimeInfo? Runtime,
    string? FaultMessage,
    PowerShellRuntimeLifecyclePhase LifecyclePhase = PowerShellRuntimeLifecyclePhase.Stable);

public interface IPowerShellRuntimeExecutionLease : IAsyncDisposable
{
    void MarkReadinessUnverified(string reason);
}

public interface IPowerShellRuntimeSessionCoordinator : IAsyncDisposable
{
    PowerShellRuntimeSessionSnapshot Snapshot { get; }

    event Action<PowerShellRuntimeSessionSnapshot>? StateChanged;

    Task<PowerShellRuntimeSessionSnapshot> AcquireAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default);

    Task<PowerShellRuntimeSessionSnapshot> RestartAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default);

    Task<bool> ShutdownAsync(CancellationToken cancellationToken = default);

    Task WriteRawInputAsync(
        Guid sessionId,
        long generation,
        string data,
        CancellationToken cancellationToken = default);

    bool IsCurrent(Guid sessionId, long generation);

    Task<IPowerShellRuntimeExecutionLease> AcquireExecutionAsync(
        Guid sessionId,
        long generation,
        bool debugger,
        CancellationToken cancellationToken = default);
}
