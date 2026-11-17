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
    public void ReconciliationUsesCoordinatorAdmissionForRuntimeAndTerminalIdentity()
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
        Assert.Contains("_runtimeSessionCoordinator!.TryAdmitStructuredPromptReadiness(", reconcile, StringComparison.Ordinal);
        var coordinator = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PowerShellRuntimeSessionCoordinator.cs");
        Assert.Contains("current.SessionId != sessionId || current.Generation != runtimeGeneration", coordinator, StringComparison.Ordinal);
        Assert.Contains("terminalGeneration != _terminal.CurrentSessionGeneration", coordinator, StringComparison.Ordinal);
        Assert.Contains("current.State != PowerShellRuntimeSessionState.Ready", coordinator, StringComparison.Ordinal);
        Assert.Contains("ApplyTerminalPromptReadyObservation(deferred.TerminalGeneration, deferred.Path, completedRequestId)", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrDuplicatePromptObservationCannotPromoteReadiness()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var reconcileStart = source.IndexOf("private void ReconcileDeferredStructuredPromptObservation(", StringComparison.Ordinal);
        var reconcileEnd = source.IndexOf("private void ClearDeferredStructuredPromptObservation(", reconcileStart, StringComparison.Ordinal);
        var reconcile = source[reconcileStart..reconcileEnd];

        var matchingRequestConsumption = reconcile.IndexOf("if (deferred?.RequestId == completedRequestId)", StringComparison.Ordinal);
        var clearConsumedObservation = reconcile.IndexOf("_deferredStructuredPromptObservation = null", StringComparison.Ordinal);
        var discardNonmatchingObservation = reconcile.IndexOf("deferred = null;", clearConsumedObservation, StringComparison.Ordinal);
        var noObservationGuard = reconcile.IndexOf("if (deferred is null)", StringComparison.Ordinal);
        var successGuard = reconcile.IndexOf("var identityMatches = requestCompleted &&", StringComparison.Ordinal);
        var apply = reconcile.IndexOf("ApplyTerminalPromptReadyObservation(deferred.TerminalGeneration, deferred.Path, completedRequestId)", StringComparison.Ordinal);

        Assert.True(matchingRequestConsumption >= 0 && clearConsumedObservation > matchingRequestConsumption);
        Assert.True(discardNonmatchingObservation > clearConsumedObservation && noObservationGuard > discardNonmatchingObservation);
        Assert.True(successGuard > noObservationGuard && apply > successGuard);
        Assert.Contains("if (!identityMatches)", reconcile[..apply], StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryRequiredOutcomeCannotReleaseNativePromptReadiness()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        var coordinatorCompletion = source.IndexOf("structuredRequestCompleted = _structuredPromptReadinessSource is not null &&", StringComparison.Ordinal);
        var adapterCompletion = source.IndexOf("structuredRequestCompleted = !result.RecoveryRequired", StringComparison.Ordinal);
        var recoveryDisposition = source.IndexOf("if (result.RecoveryRequired)", StringComparison.Ordinal);
        var unknownDisposition = source.IndexOf("else if (result.Status == EditorExecutionStatus.Unknown)", StringComparison.Ordinal);

        Assert.True(coordinatorCompletion >= 0 && adapterCompletion > coordinatorCompletion);
        Assert.Contains("!result.RecoveryRequired", source[coordinatorCompletion..source.IndexOf(';', coordinatorCompletion)], StringComparison.Ordinal);
        Assert.Contains("result.Status is EditorExecutionStatus.Completed or EditorExecutionStatus.Failed", source[coordinatorCompletion..source.IndexOf(';', coordinatorCompletion)], StringComparison.Ordinal);
        Assert.Contains("_activeCoordinatorExecutionRequestId == observation.RequestId", source, StringComparison.Ordinal);
        Assert.True(recoveryDisposition >= 0 && unknownDisposition > recoveryDisposition);
        Assert.Contains("runtimeExecutionLease?.MarkRecoveryRequired(", source[recoveryDisposition..unknownDisposition], StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionProjectionRemainsLatchedUntilPromptRequestIdentityIsReleased()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var releaseStart = source.IndexOf("private async Task ExecuteEditorThroughCoordinatorAsync(", StringComparison.Ordinal);
        var releaseEnd = source.IndexOf("private string? TryPrepareSavedScriptPathForVisibleRun(", releaseStart, StringComparison.Ordinal);
        Assert.True(releaseStart >= 0 && releaseEnd > releaseStart);
        var release = source[releaseStart..releaseEnd];

        var projectionRelease = release.IndexOf("IsExecutionRunning = false;", StringComparison.Ordinal);
        var structuredIdentityRelease = release.IndexOf("_activeStructuredExecutionRequestId = null;", projectionRelease, StringComparison.Ordinal);
        var coordinatorIdentityRelease = release.IndexOf("_activeCoordinatorExecutionRequestId = null;", projectionRelease, StringComparison.Ordinal);
        Assert.True(projectionRelease >= 0);
        Assert.True(structuredIdentityRelease > projectionRelease);
        Assert.True(coordinatorIdentityRelease > projectionRelease);
    }

    [Fact]
    public void ResetPromptDeferralRemainsSeparateFromStructuredPromptDeferral()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("DeferredResetPromptObservation? _deferredResetPromptObservation", source, StringComparison.Ordinal);
        Assert.Contains("DeferredStructuredPromptObservation? _deferredStructuredPromptObservation", source, StringComparison.Ordinal);
        Assert.Contains("ReconcileDeferredResetPromptObservation();", source, StringComparison.Ordinal);
        Assert.Contains("ReconcileDeferredStructuredPromptObservation(request.RequestId, structuredRequestCompleted);", source, StringComparison.Ordinal);
    }

}
