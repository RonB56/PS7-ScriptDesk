using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class RenderedPromptReadinessContractTests
{
    [Fact]
    public void StartupPromptAdmitsOnlyCurrentWritableReadySession()
    {
        var facts = ReadyFacts();

        Assert.True(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.Starting, true, facts));
        Assert.True(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveIdleAtPrompt, facts));
        Assert.False(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.Starting, false, facts));
        Assert.False(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveIdleAtPrompt, facts with { RuntimeIdentityCurrent = false }));
    }

    [Fact]
    public void PromptObservationCannotReleaseAnEditorLeaseOrEnableAnotherRun()
    {
        var executing = ReadyFacts() with
        {
            RuntimeState = PowerShellRuntimeSessionState.Executing,
            EditorExecutionRunning = true
        };

        Assert.False(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.InteractiveCommandRunning, true, executing));
        Assert.False(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveIdleAtPrompt, executing));
    }

    [Fact]
    public void PromptCallbacksFromOldGenerationOrNewerOwnerAreRejected()
    {
        var ready = ReadyFacts();
        Assert.False(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.Starting, false, ready));

        var newerOwner = ready with
        {
            RuntimeState = PowerShellRuntimeSessionState.Debugging,
            DebugSessionActive = true
        };
        Assert.False(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.InteractiveCommandRunning, true, newerOwner));
    }

    [Fact]
    public void NewCurrentTerminalGenerationCanPromoteBeforeItsLifecycleCallback()
    {
        Assert.True(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.InteractiveIdleAtPrompt,
            observationMatchesCurrentTerminalGeneration: true,
            facts: ReadyFacts(),
            isNewerTerminalGeneration: true));
    }

    [Fact]
    public void TerminalCompletionCannotClearAnUnrelatedEditorOperation()
    {
        var editorOwner = ReadyFacts() with
        {
            RuntimeState = PowerShellRuntimeSessionState.Executing,
            EditorExecutionRunning = true
        };
        Assert.False(EditorExecutionAdmissionPolicy.CanApplyInteractiveCommandCompletion(
            callbackMatchesCurrentTerminalSession: true,
            hasActiveEditorOwner: true,
            editorOwner));
        Assert.False(EditorExecutionAdmissionPolicy.CanApplyInteractiveCommandCompletion(
            callbackMatchesCurrentTerminalSession: true,
            hasActiveEditorOwner: false,
            editorOwner));
    }

    [Fact]
    public void UnfinishedConsoleLineBlocksAdmissionAndCancelRestoresIt()
    {
        var ready = ReadyFacts();
        Assert.False(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveInputEditing, ready));
        Assert.True(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveIdleAtPrompt, ready));
    }

    [Theory]
    [InlineData(PowerShellRuntimeSessionState.Faulted, PowerShellRuntimeLifecyclePhase.Stable)]
    [InlineData(PowerShellRuntimeSessionState.Debugging, PowerShellRuntimeLifecyclePhase.Stable)]
    [InlineData(PowerShellRuntimeSessionState.Executing, PowerShellRuntimeLifecyclePhase.Stable)]
    [InlineData(PowerShellRuntimeSessionState.Ready, PowerShellRuntimeLifecyclePhase.ResetPending)]
    [InlineData(PowerShellRuntimeSessionState.Ready, PowerShellRuntimeLifecyclePhase.Draining)]
    public void PromptAndRendererObservationCannotOverrideProtectedRuntimeState(
        PowerShellRuntimeSessionState runtimeState,
        PowerShellRuntimeLifecyclePhase lifecyclePhase)
    {
        var facts = ReadyFacts() with
        {
            RuntimeState = runtimeState,
            LifecyclePhase = lifecyclePhase,
            EditorExecutionRunning = runtimeState is PowerShellRuntimeSessionState.Executing or PowerShellRuntimeSessionState.Debugging,
            DebugSessionActive = runtimeState == PowerShellRuntimeSessionState.Debugging
        };

        Assert.False(EditorExecutionAdmissionPolicy.CanAcceptPromptObservation(
            InteractiveTerminalState.Starting, true, facts));
        Assert.False(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveIdleAtPrompt, facts));
    }

    [Fact]
    public void ResetDrainKeepsExistingInputSeparateFromNewEditorAdmission()
    {
        var draining = ReadyFacts() with
        {
            RuntimeState = PowerShellRuntimeSessionState.Executing,
            LifecyclePhase = PowerShellRuntimeLifecyclePhase.Draining,
            EditorExecutionRunning = true
        };

        Assert.False(EditorExecutionAdmissionPolicy.CanStart(
            InteractiveTerminalState.InteractiveIdleAtPrompt, draining));
        // The admission policy is intentionally not in the raw keyboard path;
        // writable transport remains available to the draining Read-Host owner.
        Assert.True(draining.TransportWritable);
    }

    [Fact]
    public void MultiplexerUsesTheSameRuntimeAwareAdmissionPolicy()
    {
        var mux = new TerminalOutputMultiplexer();
        mux.TryReplaceInteractiveGeneration(4, InteractiveTerminalState.InteractiveIdleAtPrompt, "ready");

        var requestId = Guid.NewGuid();
        Assert.True(mux.TryBeginEditorExecution(requestId, ReadyFacts(), out _));
        mux.EndEditorExecution(requestId);

        Assert.False(mux.TryBeginEditorExecution(
            Guid.NewGuid(),
            ReadyFacts() with { LifecyclePhase = PowerShellRuntimeLifecyclePhase.ResetPending },
            out _));
    }

    [Fact]
    public void InternalCommandReservationBlocksEditorAdmissionAndRejectsStaleGeneration()
    {
        var mux = new TerminalOutputMultiplexer();
        mux.TryReplaceInteractiveGeneration(4, InteractiveTerminalState.InteractiveIdleAtPrompt, "ready");
        var requestId = Guid.NewGuid();

        Assert.False(mux.TryBeginInternalCommand(requestId, 3, ReadyFacts(), out _));
        Assert.True(mux.TryBeginInternalCommand(requestId, 4, ReadyFacts(), out _));
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, mux.InteractiveState);
        Assert.False(mux.TryBeginEditorExecution(Guid.NewGuid(), ReadyFacts(), out _));

        Assert.True(mux.EndInternalCommand(requestId, 4, InteractiveTerminalState.InteractiveIdleAtPrompt, "prompt observed"));
        Assert.True(mux.TryBeginEditorExecution(Guid.NewGuid(), ReadyFacts(), out _));
    }

    private static EditorExecutionAdmissionFacts ReadyFacts() => new(
        InteractiveState: InteractiveTerminalState.InteractiveIdleAtPrompt,
        SessionRunning: true,
        TransportWritable: true,
        RuntimeIdentityCurrent: true,
        RuntimeState: PowerShellRuntimeSessionState.Ready,
        LifecyclePhase: PowerShellRuntimeLifecyclePhase.Stable);
}
