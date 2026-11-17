using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredExecutionCancellationRaceTests
{
    [Fact]
    public void InterruptBeforeDapStaging_DoesNotInterruptPowerShell()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.RequestCancellation();

        Assert.False(request.RequiresForegroundInterruption);
    }

    [Fact]
    public void InterruptDuringDapStaging_UsesStagingCancellationOnly()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.AdvanceTo(StructuredExecutionPhase.DapStaging);
        request.RequestCancellation();

        Assert.True(request.CancellationRequested);
        Assert.False(request.RequiresForegroundInterruption);
    }

    [Fact]
    public void CleanupCanBeginOnlyAfterTheRequestStagingOperationSettles()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.AdvanceTo(StructuredExecutionPhase.DapStaging);
        request.RequestCancellation();

        Assert.False(request.TryBeginStagedLauncherCleanup());
        Assert.False(request.CleanupAcknowledged);

        request.AdvanceTo(StructuredExecutionPhase.DapStageSettled);
        Assert.True(request.TryBeginStagedLauncherCleanup());
        Assert.False(request.CleanupAcknowledged);
        Assert.True(request.ConfirmStagedLauncherCleanup());
        Assert.True(request.CleanupAcknowledged);
        Assert.False(request.RequiresForegroundInterruption);
    }

    [Fact]
    public void InterruptAfterStagingBeforeLspSubmission_UsesStagedCleanup()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.AdvanceTo(StructuredExecutionPhase.DapStaging);
        request.AdvanceTo(StructuredExecutionPhase.DapStaged);
        request.RequestCancellation();

        Assert.False(request.RequiresForegroundInterruption);
    }

    [Fact]
    public void InterruptAfterLspSubmission_RequiresConfirmedForegroundRecovery()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.AdvanceTo(StructuredExecutionPhase.ForegroundSubmitted);
        request.RequestCancellation();

        Assert.True(request.RequiresForegroundInterruption);
    }

    [Fact]
    public async Task InterruptRacingEvaluationCompletion_UsesWhicheverPhaseWinsTheBarrier()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.AdvanceTo(StructuredExecutionPhase.ForegroundSubmitted);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = Task.Run(async () =>
        {
            await release.Task;
            request.RequestCancellation();
        });
        var completion = Task.Run(async () =>
        {
            await release.Task;
            request.AdvanceTo(StructuredExecutionPhase.PromptReconciled);
        });

        release.SetResult();
        await Task.WhenAll(cancellation, completion);

        Assert.True(request.CancellationRequested);
        Assert.False(request.RequiresForegroundInterruption);
    }

    [Fact]
    public void InterruptAfterEvaluationResponseBeforePromptRecovery_StillRequiresRecoveryEvidence()
    {
        var request = new StructuredExecutionPhaseTracker();
        request.AdvanceTo(StructuredExecutionPhase.ForegroundSubmitted);
        request.AdvanceTo(StructuredExecutionPhase.EvaluationResponded);
        request.RequestCancellation();

        Assert.True(request.RequiresForegroundInterruption);
    }

    [Fact]
    public void RapidRunInterruptRun_OldRequestCannotChangeNewRequestPhase()
    {
        var first = new StructuredExecutionPhaseTracker();
        first.AdvanceTo(StructuredExecutionPhase.DapStaging);
        first.RequestCancellation();
        first.AdvanceTo(StructuredExecutionPhase.Terminal);

        var second = new StructuredExecutionPhaseTracker();
        second.AdvanceTo(StructuredExecutionPhase.DapStaging);
        second.AdvanceTo(StructuredExecutionPhase.DapStaged);

        Assert.Equal(StructuredExecutionPhase.Terminal, first.Current);
        Assert.Equal(StructuredExecutionPhase.DapStaged, second.Current);
        Assert.False(second.RequiresForegroundInterruption);
    }
}
