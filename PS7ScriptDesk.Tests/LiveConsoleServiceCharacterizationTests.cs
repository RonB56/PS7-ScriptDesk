using System.Reflection;
using System.Runtime.ExceptionServices;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class LiveConsoleServiceCharacterizationTests
{
    [Fact]
    public void RawOutputRoute_PreservesAnsiAndChunkOrder_WhileRemovingNullBytes()
    {
        using var service = new LiveConsoleService();
        var rawOutput = new List<string>();
        var fallbackOutput = new List<ExecutionOutputRecord>();
        service.RawOutputReceived += rawOutput.Add;

        Publish(service, "first\0", fallbackOutput.Add);
        Publish(service, "\x1b[31msecond\x1b[0m", fallbackOutput.Add);
        Publish(service, "\r\nthird", fallbackOutput.Add);

        Assert.Equal("first\x1b[31msecond\x1b[0m\r\nthird", string.Concat(rawOutput));
        Assert.Empty(fallbackOutput);
    }

    [Fact]
    public void FragmentedDispatchTokens_AreHidden_WhileScriptOutputRemainsOrderedAndVisible()
    {
        using var service = new LiveConsoleService();
        const string startToken = "##PSSTUDIO_EXEC_START_characterization";
        const string completionToken = "##PSSTUDIO_EXEC_DONE_characterization";
        var rawOutput = new List<string>();
        var commandCompletions = 0;
        var scriptCompletions = 0;

        ConfigureScriptDispatch(service, startToken, completionToken);
        service.RawOutputReceived += rawOutput.Add;
        service.CommandExecutionCompleted += () => commandCompletions++;
        service.ScriptExecutionCompleted += () => scriptCompletions++;

        Publish(service, "PS C:\\> hidden dispatch echo\r\n##PSSTUDIO_EXEC_STA", _ => { });
        Assert.Empty(rawOutput);

        Publish(service, "RT_characterization\r\nscript line one", _ => { });
        Publish(service, "\r\nscript line two\r\n##PSSTUDIO_EXEC_DONE_char", _ => { });
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
    public void PromptRecognition_UsesLastPrompt_AndCompletesManualInteractiveTracking()
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

        Assert.Equal(@"D:\Last", service.CurrentWorkingDirectory);
        Assert.Equal(1, commandCompletions);
        Assert.False(GetField<bool>(service, "_isCommandInProgress"));
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

        Assert.Equal(@"C:\Work", service.CurrentWorkingDirectory);
        Assert.Equal(0, commandCompletions);
        Assert.True(GetField<bool>(service, "_isCommandInProgress"));
    }

    private static void ConfigureScriptDispatch(
        LiveConsoleService service,
        string startToken,
        string completionToken)
    {
        SetField(service, "_isCommandInProgress", true);
        SetField(service, "_currentCommandIsScript", true);
        SetField(service, "_commandDispatchGeneration", 7);
        SetField(service, "_pendingStartToken", startToken);
        SetField(service, "_pendingCompletionToken", completionToken);
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
            BindingFlags.Instance | BindingFlags.NonPublic);
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
