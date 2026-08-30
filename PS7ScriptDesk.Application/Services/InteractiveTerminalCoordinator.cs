using PS7ScriptDesk.Application.Interfaces;
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

    public void SetState(InteractiveTerminalState state, string? reason = null)
    {
        lock (_syncRoot)
        {
            _snapshot = _snapshot with
            {
                State = state,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    public bool TryReplaceGeneration(int generation, InteractiveTerminalState state, string? reason = null)
    {
        lock (_syncRoot)
        {
            if (generation < _snapshot.Generation)
            {
                return false;
            }

            _snapshot = new InteractiveTerminalSnapshot(
                generation,
                state,
                reason,
                DateTimeOffset.UtcNow);
            return true;
        }
    }
}
