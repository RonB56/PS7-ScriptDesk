using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell;

public class DockableToolWindow : Window
{
    private bool _allowClose;
    private bool _dockBackRequestPending;

    public event EventHandler? DockBackRequested;

    public void CloseForDockBack()
    {
        _allowClose = true;
        _dockBackRequestPending = false;
        DeveloperDiagnostics.LogInfo(
            "UI",
            "Dockable floating window close for dock-back requested after lifecycle boundary.",
            new Dictionary<string, object?> { ["windowType"] = GetType().Name, ["windowHash"] = GetHashCode() });
        Close();
    }

    public void CloseForOwnerShutdown()
    {
        _allowClose = true;
        _dockBackRequestPending = false;
        DeveloperDiagnostics.LogInfo(
            "UI",
            "Dockable floating window close requested for owner shutdown.",
            new Dictionary<string, object?> { ["windowType"] = GetType().Name, ["windowHash"] = GetHashCode() });
        Close();
    }

    protected void RequestDockBackFromControl()
    {
        RequestDockBack(deferUntilClosingReturns: false);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        DeveloperDiagnostics.LogInfo(
            "UI",
            "Dockable floating window OnClosing entered.",
            new Dictionary<string, object?>
            {
                ["windowType"] = GetType().Name,
                ["windowHash"] = GetHashCode(),
                ["allowClose"] = _allowClose,
                ["dockBackRequestPending"] = _dockBackRequestPending
            });

        if (!_allowClose)
        {
            e.Cancel = true;
            RequestDockBack(deferUntilClosingReturns: true);
            return;
        }

        base.OnClosing(e);
    }

    private void RequestDockBack(bool deferUntilClosingReturns)
    {
        if (_allowClose || _dockBackRequestPending)
        {
            return;
        }

        _dockBackRequestPending = true;
        DeveloperDiagnostics.LogInfo(
            "UI",
            deferUntilClosingReturns
                ? "Dockable floating window X-close was canceled; dock-back request queued after OnClosing returns."
                : "Dockable floating window dock-back control requested dock-back.",
            new Dictionary<string, object?>
            {
                ["windowType"] = GetType().Name,
                ["windowHash"] = GetHashCode(),
                ["deferred"] = deferUntilClosingReturns
            });

        if (deferUntilClosingReturns)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(RaiseDockBackRequested));
            return;
        }

        RaiseDockBackRequested();
    }

    private void RaiseDockBackRequested()
    {
        _dockBackRequestPending = false;
        if (_allowClose)
        {
            return;
        }

        DockBackRequested?.Invoke(this, EventArgs.Empty);
    }
}
