using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Admission facts shared by the Run command and terminal output admission.
/// Terminal generations are deliberately absent: they are backend identities,
/// while the runtime broker owns a separate session generation.
/// </summary>
public sealed record EditorExecutionAdmissionFacts(
    InteractiveTerminalState InteractiveState,
    bool SessionRunning,
    bool TransportWritable,
    bool RuntimeIdentityCurrent,
    PowerShellRuntimeSessionState? RuntimeState,
    PowerShellRuntimeLifecyclePhase? LifecyclePhase,
    bool EditorExecutionRunning = false,
    bool StopInProgress = false,
    bool DebugSessionActive = false);

public static class EditorExecutionAdmissionPolicy
{
    /// <summary>
    /// Runtime ownership and session identity are authoritative for execution.
    /// InteractiveIdleAtPrompt remains the user-input editing guard; it is not
    /// evidence that PSReadLine has consumed an input command.
    /// </summary>
    public static bool CanStart(InteractiveTerminalState state, EditorExecutionAdmissionFacts facts) =>
        state == InteractiveTerminalState.InteractiveIdleAtPrompt &&
        facts.SessionRunning &&
        facts.TransportWritable &&
        facts.RuntimeIdentityCurrent &&
        !facts.EditorExecutionRunning &&
        !facts.StopInProgress &&
        !facts.DebugSessionActive &&
        (facts.RuntimeState is null or PowerShellRuntimeSessionState.Ready) &&
        (facts.LifecyclePhase is null or PowerShellRuntimeLifecyclePhase.Stable);

    public static bool CanStart(InteractiveTerminalState state) =>
        state == InteractiveTerminalState.InteractiveIdleAtPrompt;

    /// <summary>Prompt observations can describe an idle console only for the current writable owner-free session.</summary>
    public static bool CanAcceptPromptObservation(
        InteractiveTerminalState currentState,
        bool observationMatchesCurrentTerminalGeneration,
        EditorExecutionAdmissionFacts facts,
        bool isNewerTerminalGeneration = false) =>
        observationMatchesCurrentTerminalGeneration &&
        (isNewerTerminalGeneration ||
         currentState is InteractiveTerminalState.Unavailable or InteractiveTerminalState.Starting or InteractiveTerminalState.InteractiveCommandRunning) &&
        facts.SessionRunning && facts.TransportWritable && facts.RuntimeIdentityCurrent &&
        !facts.EditorExecutionRunning && !facts.StopInProgress && !facts.DebugSessionActive &&
        (facts.RuntimeState is null or PowerShellRuntimeSessionState.Ready) &&
        (facts.LifecyclePhase is null or PowerShellRuntimeLifecyclePhase.Stable);

    /// <summary>Uncorrelated terminal completion cannot mutate state while an editor/debugger owner or reset exists.</summary>
    public static bool CanApplyInteractiveCommandCompletion(
        bool callbackMatchesCurrentTerminalSession,
        bool hasActiveEditorOwner,
        EditorExecutionAdmissionFacts facts) =>
        callbackMatchesCurrentTerminalSession &&
        !hasActiveEditorOwner &&
        !facts.EditorExecutionRunning && !facts.StopInProgress && !facts.DebugSessionActive &&
        facts.SessionRunning && facts.TransportWritable && facts.RuntimeIdentityCurrent &&
        (facts.RuntimeState is null or PowerShellRuntimeSessionState.Ready) &&
        (facts.LifecyclePhase is null or PowerShellRuntimeLifecyclePhase.Stable);

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
