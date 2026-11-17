using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Tests;

public sealed class DebuggerOwnershipSnapshotTests
{
    [Theory]
    [InlineData(DebuggerLifecycleObservation.Starting, true)]
    [InlineData(DebuggerLifecycleObservation.Running, true)]
    [InlineData(DebuggerLifecycleObservation.Suspended, true)]
    [InlineData(DebuggerLifecycleObservation.Resuming, true)]
    [InlineData(DebuggerLifecycleObservation.Stopping, true)]
    [InlineData(DebuggerLifecycleObservation.Unknown, true)]
    [InlineData(DebuggerLifecycleObservation.Stale, true)]
    [InlineData(DebuggerLifecycleObservation.None, false)]
    [InlineData(DebuggerLifecycleObservation.Terminated, false)]
    public void CandidateIsBlockedUnlessCurrentSnapshotConfirmsNoDebugger(
        DebuggerLifecycleObservation state, bool blocksCandidate)
    {
        var snapshot = new DebuggerOwnershipSnapshot(Guid.NewGuid(), 4, 7, 1234,
            state is DebuggerLifecycleObservation.None or DebuggerLifecycleObservation.Unknown ? null : Guid.NewGuid(),
            state, IsValid: true);

        Assert.Equal(blocksCandidate, snapshot.MayOwnSession);
    }

    [Fact]
    public void InvalidNoDebuggerSnapshotFailsClosed()
    {
        var snapshot = new DebuggerOwnershipSnapshot(Guid.NewGuid(), 4, 7, 1234, null,
            DebuggerLifecycleObservation.None, IsValid: false);

        Assert.True(snapshot.MayOwnSession);
    }

    [Fact]
    public void SnapshotCarriesRuntimeAndTerminalGenerationsAndDebuggerIdentity()
    {
        var runtimeId = Guid.NewGuid();
        var debuggerId = Guid.NewGuid();
        var snapshot = new DebuggerOwnershipSnapshot(runtimeId, 12, 8, 1234, debuggerId,
            DebuggerLifecycleObservation.Suspended, IsValid: true);

        Assert.Equal(runtimeId, snapshot.RuntimeSessionId);
        Assert.Equal(12, snapshot.RuntimeGeneration);
        Assert.Equal(8, snapshot.TerminalGeneration);
        Assert.Equal(debuggerId, snapshot.DebuggerSessionId);
        Assert.True(snapshot.MayOwnSession);
    }

    [Fact]
    public void StartingObservationCannotReplaceAnExistingDebuggerOwner()
    {
        var existing = Snapshot(DebuggerLifecycleObservation.Suspended, Guid.NewGuid());

        Assert.False(DebuggerOwnershipTransitionPolicy.TryBegin(existing, existing.RuntimeSessionId,
            existing.RuntimeGeneration!.Value, existing.TerminalGeneration, existing.ProcessId,
            Guid.NewGuid(), out var next));
        Assert.Same(existing, next);
        Assert.Equal(DebuggerLifecycleObservation.Suspended, next.State);
    }

    [Fact]
    public void DuplicateStartingForTheSameDebuggerIsIdempotent()
    {
        var debuggerId = Guid.NewGuid();
        var existing = Snapshot(DebuggerLifecycleObservation.Starting, debuggerId);

        Assert.True(DebuggerOwnershipTransitionPolicy.TryBegin(existing, existing.RuntimeSessionId,
            existing.RuntimeGeneration!.Value, existing.TerminalGeneration, existing.ProcessId,
            debuggerId, out var next));
        Assert.Same(existing, next);
    }

    [Fact]
    public void FreshStartingIsAcceptedFromCurrentConfirmedNoDebuggerSnapshot()
    {
        var current = Snapshot(DebuggerLifecycleObservation.None, null);
        var debuggerId = Guid.NewGuid();

        Assert.True(DebuggerOwnershipTransitionPolicy.TryBegin(current, current.RuntimeSessionId,
            current.RuntimeGeneration!.Value, current.TerminalGeneration, current.ProcessId,
            debuggerId, out var next));
        Assert.Equal(debuggerId, next.DebuggerSessionId);
        Assert.Equal(DebuggerLifecycleObservation.Starting, next.State);
        Assert.True(next.IsValid);
    }

    [Fact]
    public void CurrentOwnerTransitionsThroughRunningSuspendedAndTerminationBeforeRestart()
    {
        var firstDebuggerId = Guid.NewGuid();
        var noDebugger = Snapshot(DebuggerLifecycleObservation.None, null);
        Assert.True(DebuggerOwnershipTransitionPolicy.TryBegin(noDebugger,
            noDebugger.RuntimeSessionId, noDebugger.RuntimeGeneration!.Value,
            noDebugger.TerminalGeneration, noDebugger.ProcessId,
            firstDebuggerId, out var current));

        foreach (var state in new[]
                 {
                     DebuggerLifecycleObservation.Running,
                     DebuggerLifecycleObservation.Suspended,
                     DebuggerLifecycleObservation.Resuming,
                     DebuggerLifecycleObservation.Running,
                     DebuggerLifecycleObservation.Stopping,
                     DebuggerLifecycleObservation.Terminated
                 })
        {
            Assert.True(DebuggerOwnershipTransitionPolicy.TryApplyObservation(current, current.RuntimeSessionId,
                current.RuntimeGeneration!.Value, current.TerminalGeneration, current.ProcessId,
                firstDebuggerId, state, out current));
            Assert.Equal(state, current.State);
        }

        var secondDebuggerId = Guid.NewGuid();
        Assert.True(DebuggerOwnershipTransitionPolicy.TryBegin(current, current.RuntimeSessionId,
            current.RuntimeGeneration!.Value, current.TerminalGeneration, current.ProcessId,
            secondDebuggerId, out var restarted));
        Assert.Equal(secondDebuggerId, restarted.DebuggerSessionId);
        Assert.Equal(DebuggerLifecycleObservation.Starting, restarted.State);
    }

    [Theory]
    [InlineData(DebuggerLifecycleObservation.Unknown, false)]
    [InlineData(DebuggerLifecycleObservation.Stale, false)]
    [InlineData(DebuggerLifecycleObservation.Running, true)]
    [InlineData(DebuggerLifecycleObservation.Resuming, true)]
    [InlineData(DebuggerLifecycleObservation.Stopping, true)]
    public void StartingFailsClosedUnlessNoDebuggerIsConfirmed(DebuggerLifecycleObservation state, bool valid)
    {
        var current = Snapshot(state, Guid.NewGuid()) with { IsValid = valid };

        Assert.False(DebuggerOwnershipTransitionPolicy.TryBegin(current, current.RuntimeSessionId,
            current.RuntimeGeneration!.Value, current.TerminalGeneration, current.ProcessId,
            Guid.NewGuid(), out var next));
        Assert.Same(current, next);
    }

    [Fact]
    public void LateTerminationFromOldDebuggerCannotTerminateNewOwner()
    {
        var active = Snapshot(DebuggerLifecycleObservation.Running, Guid.NewGuid());

        Assert.False(DebuggerOwnershipTransitionPolicy.TryApplyObservation(active, active.RuntimeSessionId,
            active.RuntimeGeneration!.Value, active.TerminalGeneration, active.ProcessId,
            Guid.NewGuid(), DebuggerLifecycleObservation.Terminated, out var next));
        Assert.Same(active, next);
        Assert.Equal(DebuggerLifecycleObservation.Running, next.State);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void LifecycleObservationRejectsStaleIdentityAndGenerations(
        bool runtimeIdentityMatches, bool runtimeGenerationMatches, bool terminalGenerationMatches, bool processIdMatches)
    {
        var active = Snapshot(DebuggerLifecycleObservation.Running, Guid.NewGuid());
        var runtimeId = runtimeIdentityMatches ? active.RuntimeSessionId : Guid.NewGuid();
        var runtimeGeneration = runtimeGenerationMatches ? active.RuntimeGeneration!.Value : active.RuntimeGeneration!.Value + 1;
        var terminalGeneration = terminalGenerationMatches ? active.TerminalGeneration : active.TerminalGeneration + 1;
        var processId = processIdMatches ? active.ProcessId : active.ProcessId + 1;

        Assert.False(DebuggerOwnershipTransitionPolicy.TryApplyObservation(active, runtimeId, runtimeGeneration,
            terminalGeneration, processId, active.DebuggerSessionId!.Value,
            DebuggerLifecycleObservation.Terminated, out var next));
        Assert.Same(active, next);
    }

    private static DebuggerOwnershipSnapshot Snapshot(DebuggerLifecycleObservation state, Guid? debuggerId) =>
        new(Guid.NewGuid(), 4, 7, 1234, debuggerId, state,
            state is not (DebuggerLifecycleObservation.Unknown or DebuggerLifecycleObservation.Stale));
}
