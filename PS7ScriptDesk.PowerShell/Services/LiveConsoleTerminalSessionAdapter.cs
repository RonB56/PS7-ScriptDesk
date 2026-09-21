using System;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Adapts the existing live console service to the terminal session contract.</summary>
public sealed class LiveConsoleTerminalSessionAdapter : ITerminalSession
{
    private readonly ILiveConsoleService _service;
    private bool _disposed;
    private long _outputSequence;
    private int? _currentGeneration;

    public LiveConsoleTerminalSessionAdapter(ILiveConsoleService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.TerminalSessionStarted += OnStarted;
        _service.TerminalSessionStopping += OnStopping;
        _service.SessionTerminated += OnTerminated;
        _service.RawOutputReceived += OnOutput;
    }

    public bool IsRunning => _service.IsSessionRunning;
    public int? CurrentGeneration => _currentGeneration;
    public event Action<TerminalSessionLifecycleEventArgs>? Started;
    public event Action<TerminalSessionLifecycleEventArgs>? Stopping;
    public event Action<TerminalSessionLifecycleEventArgs>? Terminated;
    public event Action<TerminalOutputEventArgs>? OutputReceived;

    public Task StartAsync(PowerShellRuntimeInfo runtime, Action<ExecutionOutputRecord> onOutput, string? startupWorkingDirectory = null, CancellationToken cancellationToken = default)
        => _service.StartSessionAsync(runtime, onOutput, startupWorkingDirectory, cancellationToken);

    public Task<bool> StopAsync(CancellationToken cancellationToken = default)
        => _service.StopConsoleAsync();

    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
        => _service.ShutdownAsync(cancellationToken);

    public Task WriteInputAsync(string data, CancellationToken cancellationToken = default)
        => _service.WriteRawInputAsync(data, cancellationToken);

    public void Resize(int columns, int rows) => _service.ResizeConsole(columns, rows);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.TerminalSessionStarted -= OnStarted;
        _service.TerminalSessionStopping -= OnStopping;
        _service.SessionTerminated -= OnTerminated;
        _service.RawOutputReceived -= OnOutput;
    }

    private void OnStarted(int generation) { _currentGeneration = generation; _outputSequence = 0; Started?.Invoke(new(generation, "LiveConsoleService")); }
    private void OnStopping(int generation) { if (_currentGeneration == generation) Stopping?.Invoke(new(generation, "LiveConsoleService")); }
    private void OnTerminated() { if (_currentGeneration is { } generation) Terminated?.Invoke(new(generation, "LiveConsoleService")); }
    private void OnOutput(int generation, string data) { if (_disposed) return; OutputReceived?.Invoke(new(generation, data, ++_outputSequence)); }
}
