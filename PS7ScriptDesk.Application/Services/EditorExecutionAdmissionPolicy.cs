using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public static class EditorExecutionAdmissionPolicy
{
    public static bool CanStart(InteractiveTerminalState state) =>
        state == InteractiveTerminalState.InteractiveIdleAtPrompt;

    public static string ExplainRejection(InteractiveTerminalState state) => state switch
    {
        InteractiveTerminalState.Starting => "Editor execution is unavailable while the interactive terminal is starting.",
        InteractiveTerminalState.InteractiveInputEditing => "Editor execution is deferred while the interactive terminal has an unfinished input line.",
        InteractiveTerminalState.InteractiveCommandRunning => "Editor execution is deferred while an interactive terminal command is running.",
        InteractiveTerminalState.Stopping => "Editor execution is unavailable while the interactive terminal is stopping.",
        InteractiveTerminalState.Unavailable => "Editor execution is unavailable because the interactive terminal is unavailable.",
        _ => "Editor execution is not admitted in the current interactive terminal state."
    };
}
