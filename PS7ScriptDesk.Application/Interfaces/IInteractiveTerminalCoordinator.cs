using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IInteractiveTerminalCoordinator
{
    InteractiveTerminalSnapshot Snapshot { get; }

    InteractiveTerminalState State { get; }

    bool CanStartEditorExecution { get; }

    void SetState(InteractiveTerminalState state, string? reason = null);

    bool TryReplaceGeneration(int generation, InteractiveTerminalState state, string? reason = null);
}
