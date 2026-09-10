using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class InteractiveTerminalCoordinator : IInteractiveTerminalCoordinator
{
    private readonly object _syncRoot = new();
    private InteractiveTerminalSnapshot _snapshot = new(
        0,
        InteractiveTerminalState.Unavailable,
        "Terminal state has not been initialized.",
        DateTimeOffset.UtcNow);
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public InteractiveTerminalSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _snapshot;
            }
        }
    }

    public InteractiveTerminalState State
    {
        get
        {
            lock (_syncRoot)
            {
                return _snapshot.State;
            }
        }
    }

    public bool CanStartEditorExecution => EditorExecutionAdmissionPolicy.CanStart(State);

    public event EventHandler? StateChanged;

    public void SetState(InteractiveTerminalState state, string? reason = null)
    {
        var stateChanged = false;
        lock (_syncRoot)
        {
            var previous = _snapshot;
            _snapshot = _snapshot with
            {
                State = state,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            };
            stateChanged = previous.State != _snapshot.State || previous.Generation != _snapshot.Generation;
        }

        if (stateChanged)
        {
            RaiseStateChanged();
        }
    }

    public bool TryReplaceGeneration(int generation, InteractiveTerminalState state, string? reason = null)
    {
        var stateChanged = false;
        lock (_syncRoot)
        {
            if (generation < _snapshot.Generation)
            {
                return false;
            }

            var previous = _snapshot;
            _snapshot = new InteractiveTerminalSnapshot(
                generation,
                state,
                reason,
                DateTimeOffset.UtcNow);
            stateChanged = previous.State != _snapshot.State || previous.Generation != _snapshot.Generation;
        }

        if (stateChanged)
        {
            RaiseStateChanged();
        }

        return true;
    }

    private void RaiseStateChanged()
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Interactive terminal state notification subscriber failed; coordinator state was retained.");
            }
        }
    }
}
