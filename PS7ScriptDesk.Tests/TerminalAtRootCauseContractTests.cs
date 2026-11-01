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

        Assert.Contains("if (_liveConsoleService.IsInputWritable)", source, StringComparison.Ordinal);
        Assert.Contains("Authoritative terminal clear failed.", source, StringComparison.Ordinal);
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
        Assert.Contains("WriteRawInputAsync(\"cls\\r\")", viewModelSource, StringComparison.Ordinal);
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
        Assert.Contains("return ExecuteTextAsync(commandText, onOutput, cancellationToken);", source, StringComparison.Ordinal);
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
}
