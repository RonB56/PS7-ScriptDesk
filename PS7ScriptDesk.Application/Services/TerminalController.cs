using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Serialized terminal architecture coordinator. In the current production path it
/// correlates the existing session and renderer paths without owning their operations.
///
/// State is protected by one monitor. Adapter callbacks may arrive from the WPF
/// dispatcher, WebView2, or session I/O threads; callbacks first validate their
/// generation under this monitor and are rejected after disposal.
/// </summary>
public sealed class TerminalController : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly ITerminalSession _session;
    private readonly ITerminalHost _host;
    private readonly Action<TerminalControllerDiagnostic>? _diagnostic;
    private long _nextOperationId;
    private long _nextResizeGeneration;
    private long _lastOutputSequence;
    private int? _sessionGeneration;
    private int? _rendererGeneration;
    private TerminalControllerLifecycleState _state = TerminalControllerLifecycleState.Detached;
    private bool _disposed;
    private Task<bool>? _shutdownTask;

    public TerminalController(ITerminalSession session, ITerminalHost host, Action<TerminalControllerDiagnostic>? diagnostic = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _diagnostic = diagnostic;
        _session.Started += OnSessionStarted;
        _session.Stopping += OnSessionStopping;
        _session.Terminated += OnSessionTerminated;
        _session.OutputReceived += OnOutputReceived;
        _host.Ready += OnHostReady;
        _host.Unavailable += OnHostUnavailable;
        _host.Disposed += OnHostDisposed;
        _host.GeometryProposed += OnGeometryProposed;
    }

    public TerminalControllerSnapshot Snapshot
    {
        get { lock (_syncRoot) return new(_state, _sessionGeneration, _rendererGeneration, _nextResizeGeneration, _lastOutputSequence, _disposed); }
    }

    public async Task StartSessionAsync(PowerShellRuntimeInfo runtime, Action<ExecutionOutputRecord> onOutput, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        TerminalOperationIdentity identity;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            identity = NewIdentityLocked();
            _state = TerminalControllerLifecycleState.SessionStarting;
            EmitLocked("SessionStartRequested", identity);
        }

        await _session.StartAsync(runtime, onOutput, workingDirectory, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> StopSessionAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_disposed) return Task.FromResult(false);
            _state = TerminalControllerLifecycleState.ShutdownRequested;
            EmitLocked("SessionStopRequested", NewIdentityLocked());
        }
        return _session.StopAsync(cancellationToken);
    }

    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_disposed) return Task.FromResult(true);
            _shutdownTask ??= ShutdownCoreAsync(cancellationToken);
            return _shutdownTask;
        }
    }

    public TerminalOperationResult TryWriteInput(string data, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            var identity = NewIdentityLocked();
            if (_disposed) return TerminalOperationResult.Failed(TerminalOperationFailureKind.Disposed, identity);
            if (_state == TerminalControllerLifecycleState.ShutdownRequested) return TerminalOperationResult.Failed(TerminalOperationFailureKind.SessionStopping, identity);
            if (_sessionGeneration is null || !_session.IsRunning) return TerminalOperationResult.Failed(TerminalOperationFailureKind.SessionNotRunning, identity);
            _ = _session.WriteInputAsync(data, cancellationToken);
            EmitLocked("InputAccepted", identity with { SessionGeneration = _sessionGeneration });
            return TerminalOperationResult.Success(identity with { SessionGeneration = _sessionGeneration });
        }
    }

    public TerminalOperationResult TryResize(int columns, int rows)
    {
        lock (_syncRoot)
        {
            var identity = NewIdentityLocked() with { ResizeGeneration = ++_nextResizeGeneration };
            if (_disposed) return TerminalOperationResult.Failed(TerminalOperationFailureKind.Disposed, identity);
            if (_sessionGeneration is null || !_session.IsRunning) return TerminalOperationResult.Failed(TerminalOperationFailureKind.SessionNotRunning, identity);
            if (columns <= 0 || rows <= 0) return TerminalOperationResult.Failed(TerminalOperationFailureKind.ResizeRejected, identity, "Grid must be positive.");
            _session.Resize(columns, rows);
            EmitLocked("ResizeForwarded", identity);
            return TerminalOperationResult.Success(identity with { SessionGeneration = _sessionGeneration, RendererGeneration = _rendererGeneration });
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _state = TerminalControllerLifecycleState.Disposed;
            _session.Started -= OnSessionStarted;
            _session.Stopping -= OnSessionStopping;
            _session.Terminated -= OnSessionTerminated;
            _session.OutputReceived -= OnOutputReceived;
            _host.Ready -= OnHostReady;
            _host.Unavailable -= OnHostUnavailable;
            _host.Disposed -= OnHostDisposed;
            _host.GeometryProposed -= OnGeometryProposed;
            EmitLocked("Disposed", NewIdentityLocked());
        }
    }

    private async Task<bool> ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_disposed) return true;
            _state = TerminalControllerLifecycleState.ShutdownRequested;
            EmitLocked("ShutdownRequested", NewIdentityLocked());
        }
        var result = await _session.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        lock (_syncRoot)
        {
            if (!_disposed) _state = TerminalControllerLifecycleState.Stopped;
            EmitLocked("ShutdownCompleted", NewIdentityLocked());
        }
        return result;
    }

    private void OnSessionStarted(TerminalSessionLifecycleEventArgs e) { lock (_syncRoot) { if (RejectSessionLocked(e.Generation)) return; _sessionGeneration = e.Generation; _state = _host.IsReady ? TerminalControllerLifecycleState.SessionActiveHostReady : TerminalControllerLifecycleState.SessionActiveHostUnavailable; EmitLocked("SessionStarted", e.Identity); } }
    private void OnSessionStopping(TerminalSessionLifecycleEventArgs e) { lock (_syncRoot) { if (RejectSessionLocked(e.Generation)) return; _state = TerminalControllerLifecycleState.ShutdownRequested; EmitLocked("SessionStopping", e.Identity); } }
    private void OnSessionTerminated(TerminalSessionLifecycleEventArgs e) { lock (_syncRoot) { if (RejectSessionLocked(e.Generation)) return; _state = TerminalControllerLifecycleState.Stopped; EmitLocked("SessionTerminated", e.Identity); } }
    private void OnOutputReceived(TerminalOutputEventArgs e) { lock (_syncRoot) { if (_disposed || _sessionGeneration != e.SessionGeneration || e.Sequence <= _lastOutputSequence) { EmitLocked("StaleOutputRejected", new(0, e.SessionGeneration, _rendererGeneration, null, e.Sequence)); return; } _lastOutputSequence = e.Sequence; if (e.Sequence == 1 || e.Sequence % 64 == 0) EmitLocked("OutputObservedSample", new(0, e.SessionGeneration, _rendererGeneration, null, e.Sequence), $"characters={e.CharacterCount};sampled=true"); } }
    private void OnHostReady(TerminalHostLifecycleEventArgs e) { lock (_syncRoot) { if (RejectRendererLocked(e.Generation)) return; _rendererGeneration = e.Generation; if (_sessionGeneration is not null) _state = TerminalControllerLifecycleState.SessionActiveHostReady; EmitLocked("HostReady", e.Identity); } }
    private void OnHostUnavailable(TerminalHostLifecycleEventArgs e) { lock (_syncRoot) { if (RejectRendererLocked(e.Generation)) return; _rendererGeneration = e.Generation; if (_sessionGeneration is not null) _state = TerminalControllerLifecycleState.SessionActiveHostUnavailable; EmitLocked("HostUnavailable", e.Identity); } }
    private void OnHostDisposed(TerminalHostLifecycleEventArgs e) { lock (_syncRoot) { if (RejectRendererLocked(e.Generation)) return; _rendererGeneration = e.Generation; EmitLocked("HostDisposed", e.Identity); } }
    private void OnGeometryProposed(TerminalGeometryProposal e) { lock (_syncRoot) { if (_disposed || _rendererGeneration != e.RendererGeneration) { EmitLocked("StaleGeometryRejected", new(0, _sessionGeneration, e.RendererGeneration, null, null)); return; } EmitLocked("GeometryObserved", new(0, _sessionGeneration, e.RendererGeneration, null, null), $"sequence={e.Sequence};source={e.Geometry.Source}"); } }

    private bool RejectSessionLocked(int generation) => _disposed || (_sessionGeneration is not null && generation < _sessionGeneration);
    private bool RejectRendererLocked(int generation) => _disposed || (_rendererGeneration is not null && generation < _rendererGeneration);
    private TerminalOperationIdentity NewIdentityLocked() => new(++_nextOperationId, _sessionGeneration, _rendererGeneration, null, null);
    private void EmitLocked(string name, TerminalOperationIdentity identity, string? detail = null)
    {
        if (_diagnostic is null)
        {
            return;
        }

        try
        {
            _diagnostic(new(name, identity, _state, detail));
        }
        catch
        {
            // Observation diagnostics must never break the live terminal path.
        }
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(TerminalController)); }
}
