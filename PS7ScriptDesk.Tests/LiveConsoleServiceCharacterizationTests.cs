using System.Reflection;
using System.Runtime.ExceptionServices;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class LiveConsoleServiceCharacterizationTests
{
    [Fact]
    public void InitialPromptRoute_PreservesPowerShellCursorControlSequences()
    {
        using var service = new LiveConsoleService();
        var rawOutput = new List<string>();
        service.RawOutputReceived += (_, output) => rawOutput.Add(output);

        const string initialPrompt = "\x1b[2J\x1b[HPS C:\\PowerShell Scripts>\x1b[1C\x1b[?25h";
        Publish(service, initialPrompt, _ => { });

        Assert.Equal(initialPrompt, string.Concat(rawOutput));
    }

    [Fact]
    public void RawOutputRoute_PreservesAnsiAndChunkOrder_WhileRemovingNullBytes()
    {
        using var service = new LiveConsoleService();
        var rawOutput = new List<string>();
        var fallbackOutput = new List<ExecutionOutputRecord>();
        service.RawOutputReceived += (_, output) => rawOutput.Add(output);

        Publish(service, "first\0", fallbackOutput.Add);
        Publish(service, "\x1b[31msecond\x1b[0m", fallbackOutput.Add);
        Publish(service, "\r\nthird", fallbackOutput.Add);

        Assert.Equal("first\x1b[31msecond\x1b[0m\r\nthird", string.Concat(rawOutput));
        Assert.Empty(fallbackOutput);
    }

    [Fact]
    public void RawOutputSubscriberFailure_IsContainedAndDoesNotStopLaterSubscribers()
    {
        using var service = new LiveConsoleService();
        var rawOutput = new List<string>();
        service.RawOutputReceived += (_, _) => throw new InvalidOperationException("renderer callback failed");
        service.RawOutputReceived += (_, output) => rawOutput.Add(output);

        Publish(service, "PS C:\\> ", _ => { });
        Publish(service, "after failure", _ => { });

        Assert.Equal("PS C:\\> after failure", string.Concat(rawOutput));
    }

    [Fact]
    public void PromptObservation_RaisesSeparateBackendSignalWithoutCompletingCommand()
    {
        using var service = new LiveConsoleService();
        var prompts = new List<(int Generation, string Path)>();
        SetField(service, "_terminalSessionGeneration", 5);
        SetField(service, "_isCommandInProgress", true);
        service.PromptReadyObserved += (generation, path) => prompts.Add((generation, path));

        InvokePrivate(service, "UpdateCurrentDirectoryFromPrompt", "noise\r\nPS C:\\PromptReady>");

        var prompt = Assert.Single(prompts);
        Assert.Equal(5, prompt.Generation);
        Assert.Equal(@"C:\PromptReady", prompt.Path);
        Assert.True(GetField<bool>(service, "_isCommandInProgress"));
    }

    [Fact]
    public void FragmentedDispatchTokens_AreHidden_WhileScriptOutputRemainsOrderedAndVisible()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_characterization";
        const string completionToken = "##PSSTUDIO_EXEC_DONE_characterization";
        const string locationToken = "##PSSTUDIO_LOCATION_characterization_";
        var rawOutput = new List<string>();
        var commandCompletions = 0;
        var scriptCompletions = 0;

        ConfigureScriptDispatch(service, startToken, completionToken, locationToken);
        service.RawOutputReceived += (_, output) => rawOutput.Add(output);
        service.CommandExecutionCompleted += () => commandCompletions++;
        service.ScriptExecutionCompleted += () => scriptCompletions++;

        Publish(service, "PS C:\\> hidden dispatch echo\r\n##PSSTUDIO_EXEC_STA", _ => { });
        Assert.Empty(rawOutput);

        Publish(service, "RT_characterization\r\nscript line one", _ => { });
        var encodedLocation = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"C:\Work"));
        Publish(service, $"\r\nscript line two\r\n{locationToken}{encodedLocation}\r\n##PSSTUDIO_EXEC_DONE_char", _ => { });
        Publish(service, "acterization\r\nPS C:\\Work>", _ => { });

        var visible = string.Concat(rawOutput);
        Assert.DoesNotContain("hidden dispatch echo", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("##PSSTUDIO_", visible, StringComparison.Ordinal);
        Assert.True(
            visible.IndexOf("script line one", StringComparison.Ordinal) <
            visible.IndexOf("script line two", StringComparison.Ordinal));
        Assert.Equal(1, commandCompletions);
        Assert.Equal(1, scriptCompletions);
        Assert.Equal(@"C:\Work", service.CurrentWorkingDirectory);
        Assert.False(GetField<bool>(service, "_isCommandInProgress"));
    }

    [Fact]
    public void PromptRecognition_RecordsLastPromptAsHeuristicWithoutCompletingCommandState()
    {
        using var service = new LiveConsoleService();
        var commandCompletions = 0;
        SetField(service, "_isCommandInProgress", true);
        SetField(service, "_currentCommandIsScript", false);
        SetField<string?>(service, "_pendingCompletionToken", null);
        service.CommandExecutionCompleted += () => commandCompletions++;

        InvokePrivate(
            service,
            "UpdateCurrentDirectoryFromPrompt",
            "noise\r\nPS C:\\First>\r\nmore\r\nPS D:\\Last>");

        Assert.Null(service.CurrentWorkingDirectory);
        Assert.Equal(@"D:\Last", GetField<string>(service, "_lastPromptHeuristicDirectory"));
        Assert.Equal(0, commandCompletions);
        Assert.True(GetField<bool>(service, "_isCommandInProgress"));
    }

    [Fact]
    public void PromptRecognition_DoesNotCompleteSentinelTrackedOperation()
    {
        using var service = new LiveConsoleService();
        var commandCompletions = 0;
        SetField(service, "_isCommandInProgress", true);
        SetField(service, "_currentCommandIsScript", false);
        SetField(service, "_pendingCompletionToken", "##PSSTUDIO_EXEC_DONE_pending");
        service.CommandExecutionCompleted += () => commandCompletions++;

        InvokePrivate(service, "UpdateCurrentDirectoryFromPrompt", "PS C:\\Work>");

        Assert.Null(service.CurrentWorkingDirectory);
        Assert.Equal(@"C:\Work", GetField<string>(service, "_lastPromptHeuristicDirectory"));
        Assert.Equal(0, commandCompletions);
        Assert.True(GetField<bool>(service, "_isCommandInProgress"));
    }

    [Fact]
    public void StaleSessionOutput_CannotReachRendererOrMutateCurrentSessionState()
    {
        using var service = new LiveConsoleService();
        var rawOutput = new List<string>();
        SetField(service, "_terminalSessionGeneration", 8);
        SetField(service, "_terminalSessionTeardownInProgress", false);
        service.RawOutputReceived += (_, output) => rawOutput.Add(output);

        InvokePrivate(
            service,
            "PublishTerminalChunkForSession",
            "PS C:\\Stale>",
            ExecutionOutputStreamKind.StandardOutput,
            new Action<ExecutionOutputRecord>(_ => { }),
            7);

        Assert.Empty(rawOutput);
        Assert.Null(service.CurrentWorkingDirectory);
    }

    [Fact]
    public async Task RepeatedStop_IsIdempotentAndClearsAllProtocolState()
    {
        using var service = new LiveConsoleService();
        SetField(service, "_terminalSessionGeneration", 4);
        SetField(service, "_terminalSessionTeardownInProgress", false);
        SetField(service, "_isCommandInProgress", true);
        SetField(service, "_currentCommandIsScript", true);
        SetField(service, "_pendingStartToken", "start");
        SetField(service, "_pendingCompletionToken", "done");
        SetField(service, "_hiddenOutputBuffer", "partial");
        GetField<List<string>>(service, "_pendingHiddenOutputFragments").Add("hidden");

        Assert.True(await service.StopConsoleAsync());
        Assert.True(await service.StopConsoleAsync());

        Assert.True(GetField<bool>(service, "_terminalSessionTeardownInProgress"));
        Assert.False(GetField<bool>(service, "_isCommandInProgress"));
        Assert.False(GetField<bool>(service, "_currentCommandIsScript"));
        Assert.Null(GetFieldValue(service, "_pendingStartToken"));
        Assert.Null(GetFieldValue(service, "_pendingCompletionToken"));
        Assert.Equal(string.Empty, GetField<string>(service, "_hiddenOutputBuffer"));
        Assert.Empty(GetField<List<string>>(service, "_pendingHiddenOutputFragments"));
    }

    [Fact]
    public void DispatchCommand_UsesUniqueHelperSnapshotInsteadOfMutableGlobalHelper()
    {
        using var service = new LiveConsoleService();
        var command = Assert.IsType<string>(InvokePrivate(
            service,
            "BuildScriptDispatchCommand",
            @"C:\Temp\psh-helper.ps1",
            @"C:\Temp\psi-instruction.ps1",
            false));
        var startup = Assert.IsType<string>(InvokePrivate(
            service,
            "BuildTerminalStartupCommand"));

        Assert.Contains("psh-helper.ps1", command, StringComparison.Ordinal);
        Assert.Contains("psi-instruction.ps1", command, StringComparison.Ordinal);
        Assert.DoesNotContain("__psstudioRun", command, StringComparison.Ordinal);
        Assert.DoesNotContain("__psstudioRun", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("__psstudioSnapshotRoot", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalStartup_InstallsMarkedPsReadLineHistoryFilterAndChainsExistingHandler()
    {
        using var service = new LiveConsoleService();
        var startup = Assert.IsType<string>(InvokePrivate(service, "BuildTerminalStartupCommand"));

        Assert.Contains("AddToHistoryHandler", startup, StringComparison.Ordinal);
        Assert.Contains("SkipAdding", startup, StringComparison.Ordinal);
        Assert.Contains("__pssdPreviousAddToHistoryHandler", startup, StringComparison.Ordinal);
        Assert.Contains("TerminalSnapshots", startup, StringComparison.Ordinal);
        Assert.Contains("(?:psd|psh|psi)-[0-9a-f]{32}", startup, StringComparison.Ordinal);
        Assert.Contains("#PS7SDi", startup, StringComparison.Ordinal);
        Assert.Contains("return 'MemoryAndFile'", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("HistorySaveStyle", startup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Clear-History", startup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-History", startup, StringComparison.OrdinalIgnoreCase);
    }



    [Fact]
    public void CursorNeutralOscDispatchFrames_DoNotCreateHiddenProtocolRows()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_osc";
        const string completionToken = "##PSSTUDIO_EXEC_DONE_osc";
        const string locationToken = "##PSSTUDIO_LOCATION_osc_";
        ConfigureScriptDispatch(service, startToken, completionToken, locationToken);
        var visible = new List<string>();
        var completions = 0;
        service.RawOutputReceived += (_, output) => visible.Add(output);
        service.CommandExecutionCompleted += () => completions++;
        var encodedLocation = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"D:\OscConfirmed"));

        var startFrame = $"\x1b]7777;PS7SD;START;{startToken}\a";
        var locationFrame = $"\x1b]7777;PS7SD;LOCATION;{locationToken}{encodedLocation}\a";
        var doneFrame = $"\x1b]7777;PS7SD;DONE;{completionToken}\a";

        Publish(service, $"hidden short dispatch\r\n{startFrame}script output\r\n{locationFrame}{doneFrame}PS D:\\OscConfirmed> ", _ => { });

        var rendered = string.Concat(visible);
        Assert.Equal("\r\nscript output\r\nPS D:\\OscConfirmed> ", rendered);
        Assert.DoesNotContain("PS7SD", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PSSTUDIO", rendered, StringComparison.Ordinal);
        Assert.Equal(1, completions);
        Assert.Equal(@"D:\OscConfirmed", service.CurrentWorkingDirectory);
    }

    [Fact]
    public void FragmentedCursorNeutralOscCompletion_IsHeldUntilTerminator()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_oscfragment";
        const string completionToken = "##PSSTUDIO_EXEC_DONE_oscfragment";
        ConfigureScriptDispatch(service, startToken, completionToken);
        var visible = new List<string>();
        var completions = 0;
        service.RawOutputReceived += (_, output) => visible.Add(output);
        service.CommandExecutionCompleted += () => completions++;

        var startFrame = $"\x1b]7777;PS7SD;START;{startToken}\a";
        var doneFrame = $"\x1b]7777;PS7SD;DONE;{completionToken}\a";
        var split = doneFrame.Length - 3;

        Publish(service, $"hidden\r\n{startFrame}output\r\n{doneFrame[..split]}", _ => { });
        Assert.Equal(0, completions);
        Publish(service, doneFrame[split..] + "PS C:\\> ", _ => { });

        var rendered = string.Concat(visible);
        Assert.Equal("\r\noutput\r\nPS C:\\> ", rendered);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void DispatchHelper_UsesCursorNeutralOscFramesInsteadOfPrivateWriteLines()
    {
        using var service = new LiveConsoleService();
        var helperPath = Assert.IsType<string>(InvokePrivate(service, "CreateDispatchHelperSnapshot"));
        try
        {
            var helper = File.ReadAllText(helperPath);
            Assert.Contains("]7777;PS7SD;", helper, StringComparison.Ordinal);
            Assert.Contains("[Console]::Out.Write($__pssdOscPrefix + 'START;'", helper, StringComparison.Ordinal);
            Assert.Contains("[Console]::Out.Write($__pssdOscPrefix + 'LOCATION;'", helper, StringComparison.Ordinal);
            Assert.Contains("[Console]::Out.Write($__pssdOscPrefix + 'DONE;'", helper, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteLine($__pssdStart)", helper, StringComparison.Ordinal);
            Assert.DoesNotContain("##PSSTUDIO_DISPATCH_DIAG## begin", helper, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(helperPath);
        }
    }

    [Fact]
    public void DispatchHelper_HistoryProbeIsReadOnlyBoundedAndContentFree()
    {
        using var service = new LiveConsoleService();
        var helperPath = Assert.IsType<string>(InvokePrivate(service, "CreateDispatchHelperSnapshot"));
        try
        {
            var helper = File.ReadAllText(helperPath);
            Assert.Contains("Get-History -Count 64", helper, StringComparison.Ordinal);
            Assert.Contains("HISTORY;", helper, StringComparison.Ordinal);
            Assert.Contains("engineHistoryMatchingEntryHashes", helper, StringComparison.Ordinal);
            Assert.Contains("operationId", helper, StringComparison.Ordinal);
            Assert.Contains("dispatchHash", helper, StringComparison.Ordinal);
            Assert.DoesNotContain("Clear-History", helper, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Remove-History", helper, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Set-PSReadLineOption", helper, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CommandLine =", helper, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(helperPath);
        }
    }

    [Fact]
    public void HistoryProbeMetadata_IsOmittedWhenDeveloperDiagnosticsAreDisabled()
    {
        DeveloperDiagnostics.Disable("characterization test");
        using var service = new LiveConsoleService();
        var instructionPath = Assert.IsType<string>(InvokePrivate(
            service,
            "CreateDispatchInstructionSnapshot",
            "script.ps1",
            "start",
            "done",
            "location_",
            true));
        try
        {
            InvokePrivate(
                service,
                "WriteHistoryProbeMetadata",
                instructionPath,
                "operation-123",
                @"C:\Temp\TerminalSnapshots\psd-0123456789abcdef0123456789abcdef.ps1",
                "ABCDEF",
                12);

            Assert.Equal(5, File.ReadAllLines(instructionPath).Length);
        }
        finally
        {
            File.Delete(instructionPath);
        }
    }

    [Fact]
    public void ShortDispatchCommand_UsesSingleSnapshotPathToAvoidPsReadLineWrapping()
    {
        using var service = new LiveConsoleService();
        var command = Assert.IsType<string>(InvokePrivate(
            service,
            "BuildShortDispatchCommand",
            @"C:\Users\rbarn\AppData\Local\PS7ScriptDesk\Temp\TerminalSnapshots\psd-0123456789abcdef0123456789abcdef.ps1",
            false));

        Assert.StartsWith("& '", command, StringComparison.Ordinal);
        Assert.Contains("psd-0123456789abcdef0123456789abcdef.ps1", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psh-", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psi-", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HiddenProtocolFiltering_PreservesCursorAffectingLineTerminators()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_state";
        const string completionToken = "##PSSTUDIO_EXEC_DONE_state";
        ConfigureScriptDispatch(service, startToken, completionToken);
        var visible = new List<string>();
        service.RawOutputReceived += (_, output) => visible.Add(output);

        Publish(service, $"hidden dispatch echo\r\n{startToken}\r\nreal output\r\n{completionToken}\r\nPS C:\\> ", _ => { });

        var rendered = string.Concat(visible);
        Assert.DoesNotContain("hidden dispatch echo", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PSSTUDIO", rendered, StringComparison.Ordinal);
        Assert.Contains("real output", rendered, StringComparison.Ordinal);
        Assert.True(rendered.Count(character => character == '\n') >= 4);
    }

    [Fact]
    public void PreStartOutputBuffer_IsCappedUntilStartAcknowledgementArrives()
    {
        using var service = new LiveConsoleService();
        ConfigureScriptDispatch(
            service,
            "##PSSTUDIO_EXEC_START_missing",
            "##PSSTUDIO_EXEC_DONE_missing");
        service.RawOutputReceived += (_, _) => { };

        Publish(service, new string('x', 80 * 1024), _ => { });

        Assert.Equal(64 * 1024, GetField<string>(service, "_hiddenOutputBuffer").Length);
        Assert.True(GetField<bool>(service, "_preStartBufferTruncated"));
    }

    [Fact]
    public void LargeAcknowledgedChunk_DoesNotLoseStartTokenOrVisibleOutput()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_large";
        ConfigureScriptDispatch(
            service,
            startToken,
            "##PSSTUDIO_EXEC_DONE_large");
        var visible = new List<string>();
        service.RawOutputReceived += (_, output) => visible.Add(output);
        var scriptOutput = new string('x', 80 * 1024);

        Publish(service, $"hidden echo\r\n{startToken}\r\n{scriptOutput}", _ => { });

        Assert.Null(GetFieldValue(service, "_pendingStartToken"));
        Assert.Equal(scriptOutput.Length + 4, string.Concat(visible).Length);
        Assert.Contains(scriptOutput, string.Concat(visible), StringComparison.Ordinal);
    }

    [Fact]
    public void PreStartRecovery_ReleasesBusyStateAndRevealsOnlyNonProtocolFailureOutput()
    {
        using var service = new LiveConsoleService();
        const int dispatchGeneration = 7;
        const int sessionGeneration = 3;
        const string hiddenCommand = "& 'C:\\Temp\\psh.ps1' 'C:\\Temp\\psi.ps1'";
        ConfigureScriptDispatch(
            service,
            "##PSSTUDIO_EXEC_START_missing",
            "##PSSTUDIO_EXEC_DONE_missing");
        SetField(service, "_terminalSessionGeneration", sessionGeneration);
        SetField(service, "_terminalSessionTeardownInProgress", false);
        SetField(service, "_hiddenOutputBuffer", $"PS C:\\> {hiddenCommand}\r\nPS7 ScriptDesk dispatch helper failed before execution started: injected failure\r\n");
        GetField<List<string>>(service, "_pendingHiddenOutputFragments").Add(hiddenCommand);
        var visible = new List<string>();
        var lifecycle = new List<ExecutionOutputRecord>();
        var completions = 0;
        service.RawOutputReceived += (_, output) => visible.Add(output);
        service.CommandExecutionCompleted += () => completions++;

        InvokePrivate(
            service,
            "RecoverUnconfirmedDispatch",
            dispatchGeneration,
            sessionGeneration,
            true,
            "test.ps1",
            new Action<ExecutionOutputRecord>(lifecycle.Add));

        Assert.False(GetField<bool>(service, "_isCommandInProgress"));
        Assert.Null(GetFieldValue(service, "_pendingStartToken"));
        Assert.Equal(1, completions);
        Assert.Contains("injected failure", string.Concat(visible), StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenCommand, string.Concat(visible), StringComparison.Ordinal);
        Assert.Contains(lifecycle, record => record.Text.Contains("released", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreStartRecovery_DefersSnapshotDeletionUntilTerminalTeardown()
    {
        using var service = new LiveConsoleService();
        const int dispatchGeneration = 7;
        const int sessionGeneration = 3;
        ConfigureScriptDispatch(
            service,
            "##PSSTUDIO_EXEC_START_missing",
            "##PSSTUDIO_EXEC_DONE_missing");
        SetField(service, "_terminalSessionGeneration", sessionGeneration);
        SetField(service, "_terminalSessionTeardownInProgress", false);
        var snapshotPath = Assert.IsType<string>(InvokePrivate(
            service,
            "CreateExecutionSnapshot",
            "stale.ps1",
            "'queued input should not see a missing file'"));
        GetField<Queue<string>>(service, "_pendingSnapshotPaths").Enqueue(snapshotPath);

        InvokePrivate(
            service,
            "RecoverUnconfirmedDispatch",
            dispatchGeneration,
            sessionGeneration,
            true,
            "stale.ps1",
            new Action<ExecutionOutputRecord>(_ => { }));

        Assert.False(GetField<bool>(service, "_isCommandInProgress"));
        Assert.Empty(GetField<Queue<string>>(service, "_pendingSnapshotPaths"));
        Assert.Contains(snapshotPath, GetField<Queue<string>>(service, "_deferredSnapshotPaths"));
        Assert.True(File.Exists(snapshotPath));

        Assert.True(await service.StopConsoleAsync());

        Assert.Empty(GetField<Queue<string>>(service, "_deferredSnapshotPaths"));
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public void ExplicitLocationFrame_UpdatesConfirmedDirectoryAndNeverReachesRenderer()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_location";
        const string completionToken = "##PSSTUDIO_EXEC_DONE_location";
        const string locationToken = "##PSSTUDIO_LOCATION_location_";
        ConfigureScriptDispatch(service, startToken, completionToken, locationToken);
        var visible = new List<string>();
        service.RawOutputReceived += (_, output) => visible.Add(output);
        var encodedLocation = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"D:\Confirmed"));

        Publish(service, $"{startToken}\r\n{locationToken}{encodedLocation}\r\n{completionToken}\r\n", _ => { });

        Assert.Equal(@"D:\Confirmed", service.CurrentWorkingDirectory);
        Assert.DoesNotContain("PSSTUDIO_LOCATION", string.Concat(visible), StringComparison.Ordinal);
    }

    private static void ConfigureScriptDispatch(
        LiveConsoleService service,
        string startToken,
        string completionToken,
        string? locationToken = null)
    {
        SetField(service, "_isCommandInProgress", true);
        SetField(service, "_currentCommandIsScript", true);
        SetField(service, "_commandDispatchGeneration", 7);
        SetField(service, "_pendingStartToken", startToken);
        SetField(service, "_pendingCompletionToken", completionToken);
        SetField(service, "_pendingLocationToken", locationToken);
    }

    private static void Publish(
        LiveConsoleService service,
        string text,
        Action<ExecutionOutputRecord> fallbackOutput)
    {
        InvokePrivate(
            service,
            "PublishTerminalChunk",
            text,
            ExecutionOutputStreamKind.StandardOutput,
            fallbackOutput);
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private static object? GetFieldValue(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(target);
    }

    private static void SetField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}

public sealed class TerminalSessionEventPolicyTests
{
    [Theory]
    [InlineData(3, 3, false, true)]
    [InlineData(3, 2, false, false)]
    [InlineData(3, 3, true, false)]
    public void SessionGenerationPolicy_RejectsStaleAndStoppingSessionEvents(
        int currentGeneration,
        int observedGeneration,
        bool teardownInProgress,
        bool expected)
    {
        Assert.Equal(
            expected,
            TerminalSessionEventPolicy.IsCurrentSession(
                currentGeneration,
                observedGeneration,
                teardownInProgress));
    }

    [Theory]
    [InlineData(true, 4, 4, true)]
    [InlineData(false, 4, 4, false)]
    [InlineData(true, 5, 4, false)]
    public void DispatchGenerationPolicy_AcceptsOnlyActiveMatchingGeneration(
        bool commandInProgress,
        int currentGeneration,
        int observedGeneration,
        bool expected)
    {
        Assert.Equal(
            expected,
            TerminalSessionEventPolicy.IsCurrentDispatch(
                commandInProgress,
                currentGeneration,
                observedGeneration));
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, true)]
    public void InterruptRecoveryPolicy_RequiresCommandAndOwnedWindowToBeFinished(
        bool commandInProgress,
        bool processRunning,
        bool hasVisibleOwnedWindow,
        bool expectedRecovered)
    {
        Assert.Equal(
            expectedRecovered,
            TerminalSessionEventPolicy.IsInterruptRecoveryComplete(
                commandInProgress,
                processRunning,
                hasVisibleOwnedWindow));
    }

    [Theory]
    [InlineData(false, false, null, null, null, true)]
    [InlineData(false, true, null, null, null, false)]
    [InlineData(true, false, 10, 11, null, true)]
    [InlineData(true, false, 11, 11, 11, true)]
    [InlineData(true, false, 11, 11, null, false)]
    public void ProcessExitPolicy_RejectsDetachedDuplicateAndPriorSessionEvents(
        bool hasTrackedProcess,
        bool commandInProgress,
        int? exitedProcessId,
        int? currentProcessId,
        int? handledProcessId,
        bool expectedIgnored)
    {
        Assert.Equal(
            expectedIgnored,
            TerminalSessionEventPolicy.ShouldIgnoreProcessExit(
                hasTrackedProcess,
                commandInProgress,
                exitedProcessId,
                currentProcessId,
                handledProcessId));
    }
}
