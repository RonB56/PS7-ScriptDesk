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
            StartupEnablementForensicLog.DependencyChanged(
                "InteractiveTerminalCoordinator.State",
                previous.State,
                state,
                "InteractiveTerminalCoordinator.SetState");
            StartupEnablementForensicLog.DependencyChanged(
                "InteractiveTerminalCoordinator.CanStartEditorExecution",
                EditorExecutionAdmissionPolicy.CanStart(previous.State),
                EditorExecutionAdmissionPolicy.CanStart(state),
                "InteractiveTerminalCoordinator.SetState");
            AdmissionForensicLog.SetTerminalGeneration(_snapshot.Generation);
            _snapshot = _snapshot with
            {
                State = state,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            };
            AdmissionForensicLog.Write("APPLICATION_COORDINATOR_TRANSITION", new Dictionary<string, object?>
            {
                ["previousState"] = previous.State,
                ["newState"] = state,
                ["reason"] = reason ?? "(none)",
                ["terminalGeneration"] = _snapshot.Generation
            });
            if (state == InteractiveTerminalState.InteractiveIdleAtPrompt)
            {
                AdmissionForensicLog.Write("TERMINAL_IDLE_REACHED", new Dictionary<string, object?>
                {
                    ["terminalGeneration"] = _snapshot.Generation,
                    ["reason"] = reason ?? "(none)"
                });
            }

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
            AdmissionForensicLog.SetTerminalGeneration(generation);
            if (generation < _snapshot.Generation)
            {
                AdmissionForensicLog.Write("APPLICATION_COORDINATOR_TRANSITION_REJECTED", new Dictionary<string, object?>
                {
                    ["previousState"] = _snapshot.State,
                    ["newState"] = state,
                    ["reason"] = reason ?? "(none)",
                    ["terminalGeneration"] = generation,
                    ["currentGeneration"] = _snapshot.Generation
                });
                return false;
            }

            var previous = _snapshot;
            StartupEnablementForensicLog.DependencyChanged(
                "InteractiveTerminalCoordinator.State",
                previous.State,
                state,
                "InteractiveTerminalCoordinator.TryReplaceGeneration");
            StartupEnablementForensicLog.DependencyChanged(
                "InteractiveTerminalCoordinator.CanStartEditorExecution",
                EditorExecutionAdmissionPolicy.CanStart(previous.State),
                EditorExecutionAdmissionPolicy.CanStart(state),
                "InteractiveTerminalCoordinator.TryReplaceGeneration");
            _snapshot = new InteractiveTerminalSnapshot(
                generation,
                state,
                reason,
                DateTimeOffset.UtcNow);
            AdmissionForensicLog.Write("APPLICATION_COORDINATOR_TRANSITION", new Dictionary<string, object?>
            {
                ["previousState"] = previous.State,
                ["newState"] = state,
                ["reason"] = reason ?? "(none)",
                ["terminalGeneration"] = generation
            });
            if (state == InteractiveTerminalState.InteractiveIdleAtPrompt)
            {
                AdmissionForensicLog.Write("TERMINAL_IDLE_REACHED", new Dictionary<string, object?>
                {
                    ["terminalGeneration"] = generation,
                    ["reason"] = reason ?? "(none)"
                });
            }
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
        StartupEnablementForensicLog.Write("COORDINATOR_STATE_CHANGED", new Dictionary<string, object?>
        {
            ["coordinatorInstanceId"] = InstanceId,
            ["state"] = State,
            ["canStartEditorExecution"] = CanStartEditorExecution,
            ["terminalGeneration"] = Snapshot.Generation
        });
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
