using System;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Shell.Controls;

/// <summary>Adapts the existing TerminalControl renderer to the terminal host contract.</summary>
public sealed class TerminalControlHostAdapter : ITerminalHost
{
    private readonly TerminalControl _control;
    private long _geometrySequence;
    private bool _disposed;
    private bool _isReady;

    public TerminalControlHostAdapter(TerminalControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _control.TerminalReady += OnReady;
        _control.TerminalRendererUnavailable += OnUnavailable;
        _control.TerminalResized += OnResized;
    }

    public int? CurrentGeneration => _control.RendererGeneration > 0 ? _control.RendererGeneration : null;
    public bool IsReady => _isReady;
    public event Action<TerminalHostLifecycleEventArgs>? Ready;
    public event Action<TerminalHostLifecycleEventArgs>? Unavailable;
    public event Action<TerminalHostLifecycleEventArgs>? Disposed;
    public event Action<TerminalGeometryProposal>? GeometryProposed;

    public void WriteRaw(int sessionGeneration, string data) => _control.WriteRaw(sessionGeneration, data);
    public void Focus() => _control.FocusTerminal();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _control.TerminalReady -= OnReady;
        _control.TerminalRendererUnavailable -= OnUnavailable;
        _control.TerminalResized -= OnResized;
        if (CurrentGeneration is { } generation) Disposed?.Invoke(new(generation, "AdapterDisposed"));
    }

    private void OnReady()
    {
        if (_disposed || CurrentGeneration is not { } generation) return;
        _isReady = true;
        Ready?.Invoke(new(generation, "TerminalControl"));
    }

    private void OnUnavailable(string reason)
    {
        if (_disposed || CurrentGeneration is not { } generation) return;
        _isReady = false;
        Unavailable?.Invoke(new(generation, reason));
    }

    private void OnResized(int columns, int rows)
    {
        if (_disposed || CurrentGeneration is not { } generation) return;
        GeometryProposed?.Invoke(new(new TerminalGeometry(0, 0, true, "TerminalControl.ResizeObserver", columns, rows), generation, ++_geometrySequence));
    }
}
