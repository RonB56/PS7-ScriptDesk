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

            // Output readers can observe the first prompt before the owning
            // session-start lifecycle event is published. Do not regress an
            // already-ready generation back to Starting; doing so leaves the
            // editor execution command disabled until another prompt event
            // (historically Ctrl+C) arrives.
            if (generation == _snapshot.Generation &&
                _snapshot.State == InteractiveTerminalState.InteractiveIdleAtPrompt &&
                state == InteractiveTerminalState.Starting)
            {
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "PreservePromptReadyState",
                    "Ignored a late session-start transition that would regress an already prompt-ready generation.",
                    "PromptReadyAlreadyObserved",
                    new Dictionary<string, object?>
                    {
                        ["generation"] = generation,
                        ["currentState"] = _snapshot.State.ToString(),
                        ["requestedState"] = state.ToString()
                    });
                return true;
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
