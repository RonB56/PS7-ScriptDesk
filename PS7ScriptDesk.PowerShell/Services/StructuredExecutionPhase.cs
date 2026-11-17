namespace PS7ScriptDesk.PowerShell.Services;

internal enum StructuredExecutionPhase
{
    Admitted,
    DapStaging,
    DapStageSettled,
    DapStaged,
    StagedLauncherCleanupPending,
    StagedLauncherCleanupAcknowledged,
    ForegroundSubmitted,
    EvaluationResponded,
    PromptReconciled,
    Terminal
}

/// <summary>Request-local phase tracking used to route cancellation safely.</summary>
internal sealed class StructuredExecutionPhaseTracker
{
    private int _phase = (int)StructuredExecutionPhase.Admitted;
    private int _cancellationRequested;

    public StructuredExecutionPhase Current => (StructuredExecutionPhase)Volatile.Read(ref _phase);
    public bool CancellationRequested => Volatile.Read(ref _cancellationRequested) != 0;

    public bool StagingSettled => Current >= StructuredExecutionPhase.DapStageSettled;

    public bool StagingCompleted => Current >= StructuredExecutionPhase.DapStaged;

    public bool CleanupAcknowledged => Current >= StructuredExecutionPhase.StagedLauncherCleanupAcknowledged;

    public bool TryBeginStagedLauncherCleanup()
    {
        while (true)
        {
            var current = Volatile.Read(ref _phase);
            if (current < (int)StructuredExecutionPhase.DapStageSettled ||
                current >= (int)StructuredExecutionPhase.ForegroundSubmitted)
                return false;
            if (current >= (int)StructuredExecutionPhase.StagedLauncherCleanupPending)
                return current == (int)StructuredExecutionPhase.StagedLauncherCleanupPending;
            if (Interlocked.CompareExchange(ref _phase, (int)StructuredExecutionPhase.StagedLauncherCleanupPending, current) == current)
                return true;
        }
    }

    public bool ConfirmStagedLauncherCleanup()
        => Interlocked.CompareExchange(ref _phase, (int)StructuredExecutionPhase.StagedLauncherCleanupAcknowledged,
            (int)StructuredExecutionPhase.StagedLauncherCleanupPending) == (int)StructuredExecutionPhase.StagedLauncherCleanupPending;

    public void AdvanceTo(StructuredExecutionPhase phase)
    {
        while (true)
        {
            var current = Volatile.Read(ref _phase);
            if ((int)phase <= current) return;
            if (Interlocked.CompareExchange(ref _phase, (int)phase, current) == current) return;
        }
    }

    public void RequestCancellation() => Interlocked.Exchange(ref _cancellationRequested, 1);

    public bool RequiresForegroundInterruption
        => Current >= StructuredExecutionPhase.ForegroundSubmitted && Current < StructuredExecutionPhase.PromptReconciled;
}
