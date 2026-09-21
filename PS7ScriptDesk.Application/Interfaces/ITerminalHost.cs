using System;

namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>
/// Boundary for the WebView2/xterm renderer. The current adapter observes and
/// forwards the existing <c>TerminalControl</c>; it does not own renderer policy.
/// </summary>
public interface ITerminalHost : IDisposable
{
    int? CurrentGeneration { get; }
    bool IsReady { get; }

    event Action<TerminalHostLifecycleEventArgs>? Ready;
    event Action<TerminalHostLifecycleEventArgs>? Unavailable;
    event Action<TerminalHostLifecycleEventArgs>? Disposed;
    event Action<TerminalGeometryProposal>? GeometryProposed;

    void WriteRaw(int sessionGeneration, string data);
    void Focus();
}
