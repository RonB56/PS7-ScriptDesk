namespace PS7ScriptDesk.Tests;

public sealed class StructuredPromptReconciliationTests
{
    [Fact]
    public void BackendPublishesOnlyAfterRequestSpecificPromptPredicateCompletes()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var restoreStart = source.IndexOf("private async Task<bool> RestoreInteractivePromptAsync(", StringComparison.Ordinal);
        var restoreEnd = source.IndexOf("private static IReadOnlyDictionary<string, object?> ToFrameDiagnosticProperties", restoreStart, StringComparison.Ordinal);
        Assert.True(restoreStart >= 0 && restoreEnd > restoreStart);
        var restore = source[restoreStart..restoreEnd];

        var predicate = restore.IndexOf("await promptSignal.Completion.WaitAsync", StringComparison.Ordinal);
        var publish = restore.IndexOf("NotifyStructuredPromptReadyObserved", StringComparison.Ordinal);
        Assert.True(predicate >= 0 && publish > predicate);
        Assert.Contains("ObservePrompt(generation, statusFileExists, invocationEchoRemoved, cursorVisible)", restore, StringComparison.Ordinal);
        Assert.Contains("new StructuredPromptReadyObservation(", restore[publish..], StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationRequiresMatchingSuccessfulRequestAndCurrentSessionIdentity()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var reconcileStart = source.IndexOf("private void ReconcileDeferredStructuredPromptObservation(", StringComparison.Ordinal);
        var reconcileEnd = source.IndexOf("private void ClearDeferredStructuredPromptObservation(", reconcileStart, StringComparison.Ordinal);
        Assert.True(reconcileStart >= 0 && reconcileEnd > reconcileStart);
        var reconcile = source[reconcileStart..reconcileEnd];

        Assert.Contains("if (deferred is null)", reconcile, StringComparison.Ordinal);
        Assert.Contains("requestCompleted &&", reconcile, StringComparison.Ordinal);
        Assert.Contains("deferred.RequestId == completedRequestId", reconcile, StringComparison.Ordinal);
        Assert.Contains("deferred.TerminalGeneration == _liveConsoleService.CurrentSessionGeneration", reconcile, StringComparison.Ordinal);
        Assert.Contains("deferred.ProcessId == _liveConsoleService.ProcessId", reconcile, StringComparison.Ordinal);
        Assert.Contains("runtime.SessionId == deferred.RuntimeSessionId", reconcile, StringComparison.Ordinal);
        Assert.Contains("_runtimeSessionCoordinator!.IsCurrent(runtime.SessionId, runtime.Generation)", reconcile, StringComparison.Ordinal);
        Assert.Contains("ApplyTerminalPromptReadyObservation(deferred.TerminalGeneration, deferred.Path)", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrDuplicatePromptObservationCannotPromoteReadiness()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var reconcileStart = source.IndexOf("private void ReconcileDeferredStructuredPromptObservation(", StringComparison.Ordinal);
        var reconcileEnd = source.IndexOf("private void ClearDeferredStructuredPromptObservation(", reconcileStart, StringComparison.Ordinal);
        var reconcile = source[reconcileStart..reconcileEnd];

        var consume = reconcile.IndexOf("if (deferred?.RequestId == completedRequestId)", StringComparison.Ordinal);
        var missingObservationGuard = reconcile.IndexOf("if (deferred is null)", StringComparison.Ordinal);
        var apply = reconcile.IndexOf("ApplyTerminalPromptReadyObservation(deferred.TerminalGeneration, deferred.Path)", StringComparison.Ordinal);

        Assert.True(consume >= 0 && missingObservationGuard > consume && apply > missingObservationGuard);
        Assert.Contains("_deferredStructuredPromptObservation = null", reconcile[..missingObservationGuard], StringComparison.Ordinal);
    }

    [Fact]
    public void ResetPromptDeferralRemainsSeparateFromStructuredPromptDeferral()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("DeferredResetPromptObservation? _deferredResetPromptObservation", source, StringComparison.Ordinal);
        Assert.Contains("DeferredStructuredPromptObservation? _deferredStructuredPromptObservation", source, StringComparison.Ordinal);
        Assert.Contains("ReconcileDeferredResetPromptObservation();", source, StringComparison.Ordinal);
        Assert.Contains("ReconcileDeferredStructuredPromptObservation(request.RequestId, structuredExecutionSucceeded);", source, StringComparison.Ordinal);
    }

}
