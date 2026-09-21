using System;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>
/// Boundary for the durable PowerShell/ConPTY session. The current adapter forwards
/// to <see cref="ILiveConsoleService"/>; it does not replace that service.
/// </summary>
public interface ITerminalSession : IDisposable
{
    bool IsRunning { get; }
    int? CurrentGeneration { get; }

    event Action<TerminalSessionLifecycleEventArgs>? Started;
    event Action<TerminalSessionLifecycleEventArgs>? Stopping;
    event Action<TerminalSessionLifecycleEventArgs>? Terminated;
    event Action<TerminalOutputEventArgs>? OutputReceived;

    Task StartAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default);

    Task<bool> StopAsync(CancellationToken cancellationToken = default);
    Task<bool> ShutdownAsync(CancellationToken cancellationToken = default);
    Task WriteInputAsync(string data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
}
