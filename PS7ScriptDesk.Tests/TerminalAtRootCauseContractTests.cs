namespace PS7ScriptDesk.Tests;

public sealed class TerminalAtRootCauseContractTests
{
    [Fact]
    public void RendererOnlyClearUsesSingleXtermResetOperation()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");

        Assert.Contains("try { term.reset(); }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("term.write('\\x1b[2J\\x1b[3J\\x1b[H')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearPrefersAuthoritativeInputTransportOverRendererFallback()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        var clearMethod = ExtractSection(
            source,
            "private async Task OnClearConsoleAsync()",
            "private async Task OnRefreshRuntimesAsync()");
        Assert.DoesNotContain("_clearTerminalSink", clearMethod, StringComparison.Ordinal);
        Assert.Contains("TryBeginInternalTerminalCommand(clearRequestId", clearMethod, StringComparison.Ordinal);
        Assert.Contains("ExecuteConsoleCommandAsync(\"cls\", AppendExecutionOutput, clearRequestId)", clearMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteRawInputAsync(\"cls\\r\")", clearMethod, StringComparison.Ordinal);
        Assert.Contains("PowerShell session unavailable. Use Reset Console", clearMethod, StringComparison.Ordinal);

        var lifecycleOutput = ExtractSection(
            source,
            "private void AppendExecutionOutput(ExecutionOutputRecord record)",
            "private void OnStructuredEditorExecutionEvent(");
        Assert.Contains("_clearTerminalSink", lifecycleOutput, StringComparison.Ordinal);
        Assert.Contains("TerminalDisplayText = string.Empty", lifecycleOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoritativeClearReconcilesViewportWithoutResettingSession()
    {
        var terminalSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");
        var viewModelSource = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("reconcile_authoritative_clear", terminalSource, StringComparison.Ordinal);
        Assert.Contains("term.scrollToBottom()", terminalSource, StringComparison.Ordinal);
        Assert.Contains("preservesSession", terminalSource, StringComparison.Ordinal);
        Assert.Contains("_reconcileAuthoritativeClearSink?.Invoke()", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredCompletionWaitsForPostEvaluateIdleSynchronization()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.PowerShell", "Services", "PsesLanguageServiceClient.cs");

        Assert.Contains("WaitForPostEvaluateIdleAsync", source, StringComparison.Ordinal);
        Assert.Contains("_lastIdleNotificationSequence", source, StringComparison.Ordinal);
        Assert.Contains("evaluationStartNotificationSequence", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PsesOwnedLegacyEditorExecutionUsesStructuredProtocolAndTracksInterruptibleState()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");

        Assert.Contains("PSES-owned ExecuteScriptAsync entered through DAP staging and foreground PSES execution.", source, StringComparison.Ordinal);
        Assert.Contains("rawMultilineTerminalInjection", source, StringComparison.Ordinal);
        Assert.Contains("_commandInProgress", source, StringComparison.Ordinal);
        Assert.Contains("_activeCommandCancellation", source, StringComparison.Ordinal);
        Assert.Contains("activeCommandCancellation?.Cancel()", source, StringComparison.Ordinal);
        Assert.Contains("ExecuteStructuredScriptAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectConsoleCommandsRemainInteractiveConPtyInput()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");

        Assert.Contains("Direct terminal commands are intentionally interactive ConPTY input.", source, StringComparison.Ordinal);
        Assert.Contains("Direct console command routed through interactive ConPTY input.", source, StringComparison.Ordinal);
        Assert.Contains("return ExecuteTextAsync(commandText, onOutput, requestId, cancellationToken);", source, StringComparison.Ordinal);
        var executeText = source[source.IndexOf("private async Task<LiveConsoleCommandResult> ExecuteTextAsync", StringComparison.Ordinal)..];
        Assert.DoesNotContain("CommandExecutionCompleted?.Invoke(generation)", executeText, StringComparison.Ordinal);
        Assert.Contains("LiveConsoleCommandOutcome.Submitted", executeText, StringComparison.Ordinal);
        Assert.Contains("CompleteInteractiveCommand", executeText, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptDeliveryAloneDoesNotResolveExecutionOrReportGracefulCompletion()
    {
        var service = ReadRepositoryFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var viewModel = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var stop = ExtractSection(
            service,
            "private async Task<InterruptRecoveryResult> StopConsoleForGenerationAsync(int expectedGeneration)",
            "private sealed record InterruptRecoveryResult");

        Assert.Contains("LiveConsoleInterruptOutcome.RecoveredAtPrompt", stop, StringComparison.Ordinal);
        Assert.Contains("LiveConsoleInterruptOutcome.TimedOutUnverified", stop, StringComparison.Ordinal);
        Assert.Contains("await promptRecoveryWaiter.Task.WaitAsync(PromptRecoveryTimeout)", stop, StringComparison.Ordinal);
        Assert.Contains("_interactiveReadinessUnverified = true", stop, StringComparison.Ordinal);

        var interruptDelivery = ExtractSection(
            service,
            "public async Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(",
            "public void Dispose()");
        Assert.Contains("var recovery = await StopConsoleForGenerationAsync(CurrentSessionGeneration)", interruptDelivery, StringComparison.Ordinal);
        Assert.Contains("outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt", interruptDelivery, StringComparison.Ordinal);
        Assert.Contains("recovery.SignalDelivered", interruptDelivery, StringComparison.Ordinal);

        var interruptStart = viewModel.IndexOf("private async Task OnStopAsync()", StringComparison.Ordinal);
        var interruptEnd = viewModel.IndexOf("private async Task OnRestartConsoleAsync()", interruptStart, StringComparison.Ordinal);
        Assert.True(interruptStart >= 0 && interruptEnd > interruptStart, "Could not locate the OnStopAsync source section.");
        var interrupt = viewModel[interruptStart..interruptEnd];
        Assert.Contains("_executionCoordinator?.ActiveExecution is null", interrupt, StringComparison.Ordinal);
        Assert.Contains("interruptResult.CompletedGracefully ||", interrupt, StringComparison.Ordinal);
        Assert.Contains("interruptResult.SessionRestarted ||", interrupt, StringComparison.Ordinal);
        Assert.Contains("IsExecutionRunning = !executionResolved", interrupt, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredEditorExecutionIsProductionDefaultWithExplicitRollbackOnly()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Application", "Services", "EditorExecutionFeatureGate.cs");

        Assert.Contains("structuredExecutionEnabled = true", source, StringComparison.Ordinal);
        Assert.Contains("var disabled =", source, StringComparison.Ordinal);
        Assert.Contains("return new EditorExecutionFeatureGate(enabled);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StopCommandRemainsAvailableForInteractivePsesTerminalOperations()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("InteractiveTerminalState.InteractiveInputEditing or InteractiveTerminalState.InteractiveCommandRunning", source, StringComparison.Ordinal);
        Assert.Contains("interactiveOperationInProgress", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
        => TestRepositoryPaths.ReadFile(segments);

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find source marker: {startMarker}");

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find source marker after '{startMarker}': {endMarker}");
        return source[start..end];
    }
}
