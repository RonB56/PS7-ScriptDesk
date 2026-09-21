using System;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Owns one observation-only controller and its two adapters for the lifetime of
/// the terminal owner. Attach is idempotent; Dispose removes subscriptions without
/// shutting down or otherwise commanding the underlying terminal.
/// </summary>
public sealed class TerminalObserverLifetime : IDisposable
{
    private readonly ITerminalSession _session;
    private readonly ITerminalHost _host;
    private readonly Action<TerminalControllerDiagnostic>? _diagnostic;
    private TerminalController? _controller;
    private bool _disposed;

    public TerminalObserverLifetime(
        ITerminalSession session,
        ITerminalHost host,
        Action<TerminalControllerDiagnostic>? diagnostic = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _diagnostic = diagnostic;
    }

    public bool IsAttached => _controller is not null;

    public TerminalController Attach()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TerminalObserverLifetime));
        }

        return _controller ??= new TerminalController(_session, _host, _diagnostic);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _controller?.Dispose();
        }
        finally
        {
            try
            {
                _host.Dispose();
            }
            finally
            {
                _session.Dispose();
            }
        }
    }
}
