using System.Text;
using System.Reflection;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredEchoFilterContractTests
{
    [Fact]
    public void PromptPrefixedMultilineEcho_IsSuppressedWithoutSuppressingOutput()
    {
        const string script = "Write-Output \"CLEAN_START_20\"\n# clean filler 1\n# clean filler 2\nWrite-Output \"CLEAN_DONE_20\"";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var raw = "PS C:\\Work> " + script.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nCLEAN_START_20\r\nCLEAN_DONE_20\r\nPS C:\\Work> ";

        var visible = new StringBuilder(filter.Filter(raw));
        visible.Append(filter.Flush());

        Assert.DoesNotContain("CLEAN_START_20\"", visible.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("# clean filler", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("CLEAN_START_20\r\nCLEAN_DONE_20", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptPrefixedEchoSplitAcrossChunks_IsSuppressed()
    {
        const string script = "Write-Output \"START\"\n# filler\nWrite-Output \"DONE\"";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var raw = "PS C:\\Work> " + script.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nSTART\r\nDONE\r\n";
        var split = raw.IndexOf("# filler", StringComparison.Ordinal);
        var visible = new StringBuilder(filter.Filter(raw[..split]));
        visible.Append(filter.Filter(raw[split..]));
        visible.Append(filter.Flush());

        Assert.DoesNotContain("# filler", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("START\r\nDONE", visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptPrefixedEchoWithCollapsedLineWhitespace_IsSuppressed()
    {
        const string script = "param(\n    [int]$DurationSeconds = 3\n)\nWrite-Output 'DONE'";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var echoed = "PS C:\\Work> param( [int]$DurationSeconds = 3 ) Write-Output 'DONE'\r\nDONE\r\n";

        var visible = filter.Filter(echoed) + filter.Flush();

        Assert.DoesNotContain("DurationSeconds", visible, StringComparison.Ordinal);
        Assert.Contains("DONE\r\n", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptPrefixedWrappedEcho_IsHeldUntilTheWholeEchoCanBeRemoved()
    {
        const string script = "param(\n    [int]$DurationSeconds = 3\n)\nWrite-Output 'DONE'";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var firstChunk = "PS C:\\Work> param( [int]$DurationSeconds =\r\n 3 ) Write-Output 'DONE'\r\n";

        Assert.Empty(filter.Filter(firstChunk));
        var visible = filter.Filter("DONE\r\n") + filter.Flush();

        Assert.DoesNotContain("DurationSeconds", visible, StringComparison.Ordinal);
        Assert.Contains("DONE\r\n", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIdenticalLegitimateOutput_RemainsVisibleWithoutPromptPrefix()
    {
        const string script = "Write-Output 'source-identical'";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);

        var visible = filter.Filter("source-identical\r\n") + filter.Flush();

        Assert.Equal("source-identical\r\n", visible);
    }

    [Fact]
    public void GeneratedInvocationEcho_SuppressesFragmentedCommandAndPreservesOutputAndVt()
    {
        var requestId = Guid.NewGuid();
        var invocation = "& $global:__ps7sd_editor_" + requestId.ToString("N");
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(requestId, preservePromptPrefix: true, invocation);
        var raw = "PS C:\\Work> " + invocation + "\r\n\u001b[32mUSER_OUTPUT\u001b[0m\r\nPS C:\\Work> ";
        var visible = new StringBuilder();

        foreach (var chunk in raw.Chunk(7))
        {
            visible.Append(filter.Filter(new string(chunk)));
        }
        visible.Append(filter.Flush());

        Assert.DoesNotContain(invocation, visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("USER_OUTPUT", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[32m", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileGeneratedCommand_IsSuppressedWithOutputAndPromptPreserved()
    {
        var requestId = Guid.NewGuid();
        var invocation = "& $global:__ps7sd_editor_" + requestId.ToString("N");
        var forms = PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(invocation, executeInCurrentScope: false);
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(requestId, preservePromptPrefix: true, forms);
        var raw = "PS C:\\Work> " + invocation + "\r\nFILE_OUTPUT\r\nPS C:\\Work> ";

        var visible = filter.Filter(raw) + filter.Flush();

        Assert.Single(forms);
        Assert.DoesNotContain(invocation, visible, StringComparison.Ordinal);
        Assert.Contains("FILE_OUTPUT", visible, StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSelectionOperandEcho_IsSuppressedAndSelectedOutputRemainsVisible()
    {
        var requestId = Guid.NewGuid();
        var operand = "$global:__ps7sd_editor_" + requestId.ToString("N");
        var invocation = ". " + operand;
        var forms = PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(invocation, executeInCurrentScope: true);
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(requestId, preservePromptPrefix: true, forms);
        var raw = "PS C:\\Work> " + operand + "\r\nSELECTED_EXPRESSION_OUTPUT\r\nPS C:\\Work> ";

        var visible = filter.Filter(raw) + filter.Flush();

        Assert.Equal(new[] { invocation, operand }, forms);
        Assert.DoesNotContain(operand, visible, StringComparison.Ordinal);
        Assert.Contains("SELECTED_EXPRESSION_OUTPUT", visible, StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSelectionOperandEcho_SplitAcrossChunksWithVtControls_IsSuppressedWithoutLosingControls()
    {
        var requestId = Guid.NewGuid();
        var operand = "$global:__ps7sd_editor_" + requestId.ToString("N");
        var forms = PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(". " + operand, executeInCurrentScope: true);
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(requestId, preservePromptPrefix: true, forms);
        var echoed = operand.Insert(12, "\u001b[0m");
        var raw = "PS C:\\Work> " + echoed + "\r\nVT_SELECTION_OUTPUT\r\nPS C:\\Work> ";
        var visible = new StringBuilder();

        foreach (var chunk in raw.Chunk(5)) visible.Append(filter.Filter(new string(chunk)));
        visible.Append(filter.Flush());

        Assert.DoesNotContain(operand, visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("VT_SELECTION_OUTPUT", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UserOutputContainingRequestTokenWithoutPromptPrefix_IsPreserved()
    {
        var requestId = Guid.NewGuid();
        var operand = "$global:__ps7sd_editor_" + requestId.ToString("N");
        var forms = PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(". " + operand, executeInCurrentScope: true);
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(requestId, preservePromptPrefix: true, forms);
        var userOutput = "USER_PRINTED_TOKEN=" + operand + "\r\n";

        var visible = filter.Filter(userOutput) + filter.Flush();

        Assert.Contains(userOutput, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsecutiveRunSelectionsAndRunFileAfterSelection_DoNotShareEchoState()
    {
        var selectionOneId = Guid.NewGuid();
        var selectionOne = "$global:__ps7sd_editor_" + selectionOneId.ToString("N");
        var selectionOneFilter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(
            selectionOneId, preservePromptPrefix: true,
            PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(". " + selectionOne, true));
        var firstVisible = selectionOneFilter.Filter("PS> " + selectionOne + "\r\nFIRST_SELECTION\r\n") + selectionOneFilter.Flush();

        var selectionTwoId = Guid.NewGuid();
        var selectionTwo = "$global:__ps7sd_editor_" + selectionTwoId.ToString("N");
        var selectionTwoFilter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(
            selectionTwoId, preservePromptPrefix: true,
            PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(". " + selectionTwo, true));
        var secondVisible = selectionTwoFilter.Filter("PS> " + selectionTwo + "\r\nSECOND_SELECTION\r\n") + selectionTwoFilter.Flush();

        var runFileId = Guid.NewGuid();
        var runFile = "& $global:__ps7sd_editor_" + runFileId.ToString("N");
        var runFileFilter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(
            runFileId, preservePromptPrefix: true,
            PsesOwnedInteractiveSessionService.BuildInvocationEchoForms(runFile, false));
        var runFileVisible = runFileFilter.Filter("PS> " + runFile + "\r\nRUNFILE_AFTER_SELECTION\r\n") + runFileFilter.Flush();

        Assert.DoesNotContain(selectionOne, firstVisible, StringComparison.Ordinal);
        Assert.DoesNotContain(selectionTwo, secondVisible, StringComparison.Ordinal);
        Assert.DoesNotContain(runFile, runFileVisible, StringComparison.Ordinal);
        Assert.Contains("FIRST_SELECTION", firstVisible, StringComparison.Ordinal);
        Assert.Contains("SECOND_SELECTION", secondVisible, StringComparison.Ordinal);
        Assert.Contains("RUNFILE_AFTER_SELECTION", runFileVisible, StringComparison.Ordinal);
    }

    [Fact]
    public void SequentialGeneratedInvocations_UseIndependentFilters()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstInvocation = "& $global:__ps7sd_editor_" + firstId.ToString("N");
        var secondInvocation = ". $global:__ps7sd_editor_" + secondId.ToString("N");
        var firstFilter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(firstId, preservePromptPrefix: true, firstInvocation);
        var secondFilter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(secondId, preservePromptPrefix: true, secondInvocation);

        var firstVisible = firstFilter.Filter("PS> " + firstInvocation + "\r\nfirst output\r\n") + firstFilter.Flush();
        var secondVisible = secondFilter.Filter("PS> " + secondInvocation + "\r\nsecond output\r\n") + secondFilter.Flush();

        Assert.DoesNotContain(firstInvocation, firstVisible, StringComparison.Ordinal);
        Assert.DoesNotContain(secondInvocation, secondVisible, StringComparison.Ordinal);
        Assert.Contains("first output", firstVisible, StringComparison.Ordinal);
        Assert.Contains("second output", secondVisible, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedInvocationWithInterleavedVtControl_PreservesControlAndPrompt()
    {
        var requestId = Guid.NewGuid();
        var invocation = "& $global:__ps7sd_editor_" + requestId.ToString("N");
        var split = invocation.Length / 2;
        var echoed = invocation[..split] + "\u001b[0m" + invocation[split..];
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(requestId, preservePromptPrefix: true, invocation);

        var visible = filter.Filter("PS C:\\Work> " + echoed + "\r\nUSER_OUTPUT\r\nPS C:\\Work> ") + filter.Flush();

        Assert.DoesNotContain(invocation, visible, StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", visible, StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible, StringComparison.Ordinal);
        Assert.Contains("USER_OUTPUT", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedInvocationEcho_CompletedEchoReleasesShortLiveOutputImmediately()
    {
        var invocation = "& $global:__ps7sd_editor_0123456789abcdef0123456789abcdef";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(
            Guid.NewGuid(), preservePromptPrefix: true, invocation);

        var visible = filter.Filter("PS C:\\> " + invocation + "\r\nR38 LIVE\r\n");

        Assert.Contains("R38 LIVE", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(invocation, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPromptObserverReceivesOutputBeforeDisplayFilter()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");

        Assert.Contains("OutputObservedBeforeDisplayFiltering += rawHandler", source, StringComparison.Ordinal);
        var preFilterIndex = source.IndexOf("handler(generation, text)", StringComparison.Ordinal);
        var structuredFilterIndex = source.IndexOf("echoFilter.Filter(string.Concat(executionState.Process(text)))", StringComparison.Ordinal);
        var directFilterIndex = source.IndexOf("_activeStructuredEchoFilter?.Filter(text)", StringComparison.Ordinal);
        Assert.True(preFilterIndex >= 0 && structuredFilterIndex > preFilterIndex && directFilterIndex > preFilterIndex,
            "The request invocation observer must receive ConPTY text before the display echo filter runs.");
    }

    [Fact]
    public void PromptSubscriberException_DoesNotPreventLaterRequestObserver()
    {
        using var service = new PsesOwnedInteractiveSessionService();
        var laterSubscriberCalled = false;
        service.PromptReadyObserved += ThrowingSubscriber;
        service.PromptReadyObserved += (_, _) => laterSubscriberCalled = true;

        var notify = typeof(PsesOwnedInteractiveSessionService).GetMethod("NotifyPromptReadyObserved", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(notify);
        notify.Invoke(service, [1, "PS C:\\Work>"]);

        Assert.True(laterSubscriberCalled);
    }

    [Fact]
    public void StructuredEchoFilter_IsRequestScopedAndClearedByAllExecutionExitPaths()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var cleanupEnd = source.IndexOf("TryDeleteFile(statusPath);", StringComparison.Ordinal);
        Assert.True(cleanupEnd >= 0);
        var cleanupStart = source.LastIndexOf("finally", cleanupEnd, StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task<bool> StopConsoleAsync", cleanupEnd, StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0 && methodEnd > cleanupEnd);
        var cleanup = source[cleanupStart..methodEnd];

        Assert.Contains("requestEchoFilter.Flush()", cleanup, StringComparison.Ordinal);
        Assert.Contains("_activeStructuredEchoFilter = null", cleanup, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.IsCancellationRequested", cleanup, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_activeStructuredEchoFilter, requestEchoFilter)", cleanup, StringComparison.Ordinal);
        Assert.Contains("_activeStructuredEchoFilter = requestEchoFilter", source, StringComparison.Ordinal);
    }

    private static void ThrowingSubscriber(int _, string __) => throw new InvalidOperationException("diagnostic test");
}
