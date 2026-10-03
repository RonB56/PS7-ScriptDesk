using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

public sealed class DebuggerExecutionControlGateTests
{
    [Fact]
    public void FirstCommandAcquiresAndSecondCommandIsRejectedWithoutQueueing()
    {
        var gate = new DebuggerExecutionControlGate();
        var session = new object();

        Assert.True(gate.TryAcquire(session, "StepOver", out var first));
        Assert.False(gate.TryAcquire(session, "Continue", out _));
        Assert.True(gate.IsBusy);

        Assert.True(gate.Release(first));
        Assert.False(gate.IsBusy);
        Assert.True(gate.TryAcquire(session, "Continue", out var second));
        Assert.True(gate.Release(second));
    }

    [Fact]
    public void OneGateProtectsEveryExecutionControlCommand()
    {
        var gate = new DebuggerExecutionControlGate();
        var session = new object();

        Assert.True(gate.TryAcquire(session, "StepOver", out var lease));
        Assert.False(gate.TryAcquire(session, "StepInto", out _));
        Assert.False(gate.TryAcquire(session, "StepOut", out _));
        Assert.False(gate.TryAcquire(session, "Continue", out _));

        Assert.True(gate.Release(lease));
    }

    [Fact]
    public void StaleLeaseCannotReleaseNewSessionLease()
    {
        var gate = new DebuggerExecutionControlGate();
        var oldSession = new object();
        var newSession = new object();

        Assert.True(gate.TryAcquire(oldSession, "StepOver", out var oldLease));
        Assert.True(gate.ReleaseForSession(oldSession));
        Assert.True(gate.TryAcquire(newSession, "Continue", out var newLease));

        Assert.False(gate.Release(oldLease));
        Assert.True(gate.IsBusy);
        Assert.False(gate.ReleaseForSession(oldSession));
        Assert.True(gate.IsBusy);
        Assert.True(gate.Release(newLease));
    }

    [Fact]
    public void ReleaseForSessionDoesNotClearDifferentActiveSession()
    {
        var gate = new DebuggerExecutionControlGate();
        var activeSession = new object();
        var unrelatedSession = new object();

        Assert.True(gate.TryAcquire(activeSession, "StepInto", out var lease));
        Assert.False(gate.ReleaseForSession(unrelatedSession));
        Assert.True(gate.IsBusy);
        Assert.True(gate.Release(lease));
    }
}
