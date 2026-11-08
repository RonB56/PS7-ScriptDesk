using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PromptRestorationSignalTests
{
    [Fact]
    public async Task StatusBeforePrompt_CompletesRestoration()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 7);

        Assert.True(signal.ObservePrompt(7, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.True(await signal.Completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void PromptLookingOutputBeforeStatus_CannotRestoreTheTerminal()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 7);

        Assert.True(signal.ObservePrompt(7, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true));
        Assert.False(signal.Completion.IsCompleted);
        signal.ObserveStatusFileVisible();

        Assert.False(signal.Completion.IsCompleted);
        Assert.False(signal.ObservePrompt(7, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.True(signal.Completion.IsCompleted);
    }

    [Fact]
    public async Task PromptPrintedByRunningScriptCannotCompleteButLaterPromptCanRecover()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 21);

        // Simulates `Write-Host 'PS C:\\>'` while the request-owned script is
        // still executing. A status boundary and a subsequent prompt are both
        // required before readiness can be published.
        Assert.True(signal.ObservePrompt(21, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true));
        Assert.False(signal.Completion.IsCompleted);
        signal.ObserveStatusFileVisible();
        Assert.False(signal.Completion.IsCompleted);

        var recovered = signal.Completion;
        Assert.False(recovered.IsCompleted);
        signal.ObservePrompt(21, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true);

        Assert.True(await recovered.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void StructuredPromptTimeoutIsDiagnosticOnlyAndRetainsLateRecoveryHandlers()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var restoreStart = source.IndexOf("private async Task<bool> RestoreInteractivePromptAsync(", StringComparison.Ordinal);
        var restoreEnd = source.IndexOf("private static IReadOnlyDictionary<string, object?> ToFrameDiagnosticProperties", restoreStart, StringComparison.Ordinal);
        Assert.True(restoreStart >= 0 && restoreEnd > restoreStart);
        var restore = source[restoreStart..restoreEnd];

        Assert.Contains("await promptSignal.Completion.WaitAsync(PromptRecoveryTimeout, cancellationToken)", restore, StringComparison.Ordinal);
        Assert.Contains("await promptSignal.Completion.WaitAsync(cancellationToken)", restore, StringComparison.Ordinal);
        Assert.Contains("recoveryWaitContinues\"] = true", restore, StringComparison.Ordinal);
        Assert.Contains("owner.ProcessId != expectedProcessId.Value", restore, StringComparison.Ordinal);
        Assert.Contains("_activeStructuredRequestId == requestId && File.Exists(statusPath)", restore, StringComparison.Ordinal);
        Assert.True(restore.IndexOf("PromptReadyObserved -= promptHandler", StringComparison.Ordinal) >
                    restore.IndexOf("await promptSignal.Completion.WaitAsync(cancellationToken)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateAndStaleEvents_CompleteOnlyCurrentExecutionOnce()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 12);
        var completion = signal.Completion;
        using var simultaneousStart = new Barrier(2);

        Assert.False(signal.ObservePrompt(11, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.False(signal.ObserveCursor(11, visible: true));
        await Task.WhenAll(
            Task.Run(() =>
            {
                simultaneousStart.SignalAndWait();
                return signal.ObservePrompt(12, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true);
            }),
            Task.Run(() =>
            {
                simultaneousStart.SignalAndWait();
                return signal.ObserveStatusFileVisible();
            }));

        if (!completion.IsCompleted)
            signal.ObservePrompt(12, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true);
        Assert.True(completion.IsCompleted);
        Assert.True(signal.ObserveCursor(12, visible: true));
        Assert.False(signal.ObservePrompt(12, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.Same(completion, signal.Completion);

        Assert.True(await completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task CancellationDuringRestoration_IsBoundedAndDoesNotCompleteSignal()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 3);
        using var cancellation = new CancellationTokenSource();
        var wait = signal.Completion.WaitAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(signal.Completion.IsCompleted);
    }

    [Fact]
    public void PredicateMatrix_RecordsMissingInvocationHiddenCursorAndStaleGeneration()
    {
        var missingInvocation = new PromptRestorationSignal(expectedGeneration: 4);
        Assert.False(missingInvocation.ObservePrompt(4, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true));
        Assert.False(missingInvocation.Snapshot().PromptBoundaryObserved);
        Assert.False(missingInvocation.Completion.IsCompleted);

        var hiddenCursor = new PromptRestorationSignal(expectedGeneration: 4);
        Assert.True(hiddenCursor.ObservePrompt(4, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: false));
        Assert.True(hiddenCursor.Snapshot().PostScriptPromptObserved);
        Assert.False(hiddenCursor.Completion.IsCompleted);
        Assert.True(hiddenCursor.ObserveCursor(4, visible: true));
        Assert.True(hiddenCursor.Completion.IsCompleted);

        var staleGeneration = new PromptRestorationSignal(expectedGeneration: 4);
        Assert.False(staleGeneration.ObservePrompt(3, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.False(staleGeneration.Snapshot().PromptBoundaryObserved);
        Assert.False(staleGeneration.Completion.IsCompleted);
    }

    [Fact]
    public void MissingStatusFile_RequiresAnotherPromptAfterStatusTransition()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 9);

        Assert.True(signal.ObservePrompt(9, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true));
        var beforeStatus = signal.Snapshot();
        Assert.True(beforeStatus.PromptBoundaryObserved);
        Assert.False(beforeStatus.StatusFileObserved);
        Assert.False(beforeStatus.Completed);

        Assert.True(signal.ObserveStatusFileVisible());
        Assert.False(signal.Snapshot().Completed);
        signal.ObservePrompt(9, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true);
        Assert.True(signal.Snapshot().Completed);
    }

    [Fact]
    public void PromptDiagnostics_AreCorrelatedAndTimeoutDoesNotClaimNoPromptWasSeen()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");

        Assert.Contains("Request prompt-handler callback entered.", source, StringComparison.Ordinal);
        Assert.Contains("Request prompt-handler callback completed.", source, StringComparison.Ordinal);
        Assert.Contains("Request-specific prompt pattern recognized and predicate evaluated.", source, StringComparison.Ordinal);
        Assert.Contains("rejectionReason", source, StringComparison.Ordinal);
        Assert.Contains("elapsedMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("Interactive readiness remains unverified after request-correlated prompt verification failed", source, StringComparison.Ordinal);
        Assert.Contains("sessionPreservedAfterTimeout", source, StringComparison.Ordinal);
        Assert.Contains("Prompt-ready subscriber callback failed", source, StringComparison.Ordinal);
        Assert.Contains("request-specific prompt verification ended without a verified ready prompt", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed without an observable ConPTY prompt boundary", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingVisualPromptPreservesSessionAndMarksInputReadinessUnverified()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var start = source.IndexOf("if (!foregroundCompleted)", StringComparison.Ordinal);
        var end = source.IndexOf("if (foregroundRequested && File.Exists(statusPath))", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var timeoutBranch = source[start..end];

        Assert.Contains("_interactiveReadinessUnverified = true", timeoutBranch, StringComparison.Ordinal);
        Assert.Contains("shutdownDecision\"] = \"preserve-session", timeoutBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("ShutdownAsync", timeoutBranch, StringComparison.Ordinal);
    }
}
