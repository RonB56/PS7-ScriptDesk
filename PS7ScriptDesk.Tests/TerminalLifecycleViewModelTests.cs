using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalLifecycleViewModelTests
{
    [Fact]
    public async Task StopCommand_UsesInterruptRecoveryAndReturnsUiToIdle()
    {
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = true,
            InterruptResult = new LiveConsoleInterruptResult(
                interruptAttempted: true,
                completedGracefully: true,
                escalationRequired: false,
                processTerminationSucceeded: false,
                sessionRestarted: false,
                ownedProcessId: 42,
                gracefulTimeout: TimeSpan.FromSeconds(2))
        };
        var viewModel = await CreateViewModelAsync(console);

        Assert.True(viewModel.StopCommand.CanExecute(null));
        viewModel.StopCommand.Execute(null);

        await console.InterruptObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.StatusText == "Interrupt completed");

        Assert.Equal(["interrupt"], console.Operations);
        Assert.False(viewModel.IsExecutionRunning);
    }

    [Fact]
    public async Task RestartCommand_StopsActiveManagedCommandBeforeStartingReplacement()
    {
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = true
        };
        var runtime = CreateRuntime();
        var viewModel = await CreateViewModelAsync(console, runtime);

        Assert.True(viewModel.RestartConsoleCommand.CanExecute(null));
        viewModel.RestartConsoleCommand.Execute(null);

        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.StatusText.Contains("restarted", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["stop", "start"], console.Operations);
        Assert.Same(runtime, console.ActiveRuntime);
        Assert.True(console.IsSessionRunning);
    }

    [Fact]
    public async Task ResetRequest_DuringPendingInterrupt_IsRejectedWithoutStartingACompetingSession()
    {
        var interruptCompletion = new TaskCompletionSource<LiveConsoleInterruptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = true,
            InterruptCompletion = interruptCompletion
        };
        var runtime = CreateRuntime();
        var viewModel = await CreateViewModelAsync(console, runtime);

        viewModel.StopCommand.Execute(null);
        await console.InterruptObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StatusText.Contains("current Interrupt", StringComparison.Ordinal));

        Assert.Equal(["interrupt"], console.Operations);

        interruptCompletion.TrySetResult(new LiveConsoleInterruptResult(
            interruptAttempted: true,
            completedGracefully: true,
            escalationRequired: false,
            processTerminationSucceeded: false,
            sessionRestarted: false,
            ownedProcessId: 42,
            gracefulTimeout: TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => viewModel.StatusText == "Interrupt completed");

        Assert.True(viewModel.RestartConsoleCommand.CanExecute(null));
        viewModel.RestartConsoleCommand.Execute(null);
        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["interrupt", "stop", "start"], console.Operations);
    }

    [Fact]
    public async Task RestartCommand_DoesNotStartReplacementBeforeCleanTeardownBoundary()
    {
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = true,
            StopResult = false
        };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime());

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.StatusText == "ConPTY terminal restart failed");

        Assert.Equal(["stop"], console.Operations);
        Assert.True(console.IsSessionRunning);
        Assert.False(console.StartObserved.Task.IsCompleted);
    }

    [Fact]
    public async Task ResetWhileTerminalFocused_RestoresReplacementFocusAndLeavesInputAvailable()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime());
        var verifiedFocusCount = 0;
        viewModel.SetTerminalSessionControls(
            clearTerminal: () => { },
            isTerminalFocused: () => true,
            terminalFocusRestoreReadiness: () => new TerminalFocusRestoreReadiness(true, true, true, false),
            restoreTerminalFocus: (_, _) =>
            {
                verifiedFocusCount++;
                return Task.FromResult(new TerminalFocusRestoreResult(
                    WpfHostFocused: true,
                    WebViewFocused: true,
                    BrowserFocusCommandExecuted: true,
                    XtermInputActive: true,
                    ActiveElement: "TEXTAREA.xterm-helper-textarea",
                    FailureReason: null));
            });

        viewModel.PrepareTerminalFocusRestoreForReset();
        viewModel.RestartConsoleCommand.Execute(null);

        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => verifiedFocusCount == 1);
        await viewModel.WriteRawInputAsync("Get-Date");

        Assert.Equal(1, verifiedFocusCount);
        Assert.Equal(1, console.RawInputCount);
    }

    [Fact]
    public async Task FailedBrowserFocusVerification_RetriesOnlyWithinTheReplacementGeneration()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime());
        var attempts = 0;
        viewModel.SetTerminalSessionControls(
            clearTerminal: () => { },
            isTerminalFocused: () => true,
            terminalFocusRestoreReadiness: () => new TerminalFocusRestoreReadiness(true, true, true, false),
            restoreTerminalFocus: (_, _) =>
            {
                attempts++;
                return Task.FromResult(new TerminalFocusRestoreResult(
                    WpfHostFocused: true,
                    WebViewFocused: true,
                    BrowserFocusCommandExecuted: true,
                    XtermInputActive: attempts == 2,
                    ActiveElement: attempts == 2 ? "TEXTAREA.xterm-helper-textarea" : "BUTTON",
                    FailureReason: attempts == 2 ? null : "xterm-input-not-active"));
            });

        viewModel.PrepareTerminalFocusRestoreForReset();
        viewModel.RestartConsoleCommand.Execute(null);

        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => attempts == 2);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ResetWhileEditorFocused_DoesNotStealTerminalFocus()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime());
        var focusCount = 0;
        viewModel.SetTerminalSessionControls(
            clearTerminal: () => { },
            focusTerminal: () => focusCount++,
            isTerminalFocused: () => false,
            terminalFocusRestoreReadiness: () => new TerminalFocusRestoreReadiness(true, true, true, false));

        viewModel.RestartConsoleCommand.Execute(null);

        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.StatusText.Contains("restarted", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, focusCount);
    }

    [Fact]
    public async Task FocusMoveBeforeRendererReady_CancelsResetFocusRestoration()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime());
        var rendererReady = false;
        var focusCount = 0;
        viewModel.SetTerminalSessionControls(
            clearTerminal: () => { },
            focusTerminal: () => focusCount++,
            isTerminalFocused: () => true,
            terminalFocusRestoreReadiness: () => new TerminalFocusRestoreReadiness(rendererReady, true, true, false));

        viewModel.PrepareTerminalFocusRestoreForReset();
        viewModel.RestartConsoleCommand.Execute(null);
        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.NotifyTerminalFocusOwnershipChanged(terminalHasFocus: false);
        rendererReady = true;
        viewModel.NotifyTerminalRendererReady();

        Assert.Equal(0, focusCount);
    }

    [Fact]
    public async Task ApplicationShutdown_AwaitsTheOwnedTerminalTeardown()
    {
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            ShutdownCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = await CreateViewModelAsync(console);

        var shutdown = viewModel.ShutdownTerminalAsync();
        await WaitUntilAsync(() => console.Operations.Contains("shutdown", StringComparer.Ordinal));

        Assert.False(shutdown.IsCompleted);
        console.ShutdownCompletion.TrySetResult(true);

        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("shutdown", console.Operations.Last());
    }

    [Fact]
    public async Task SessionTermination_AutomaticallyRestartsSelectedRuntimeAndExplainsRecovery()
    {
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = true
        };
        var runtime = CreateRuntime();
        var viewModel = await CreateViewModelAsync(console, runtime);

        console.IsSessionRunning = false;
        console.IsCommandInProgress = false;
        console.RaiseSessionTerminated();

        await console.StartObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.StatusText.Contains("restarted", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["start"], console.Operations);
        Assert.False(viewModel.IsExecutionRunning);
        Assert.False(viewModel.StopCommand.CanExecute(null));
        Assert.Contains("terminal exited", viewModel.ApplicationActivityText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restarted", viewModel.ApplicationActivityText, StringComparison.OrdinalIgnoreCase);
        Assert.Same(runtime, console.ActiveRuntime);
        Assert.Contains("PowerShell 7.6.2", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("Running", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.False(viewModel.HasRunningRuntimeCompactText);
        Assert.True(console.IsSessionRunning);
    }

    [Fact]
    public async Task RepeatedSessionTermination_PausesAutomaticRestartUntilManualReset()
    {
        var console = new RecordingLiveConsoleService();
        var runtime = CreateRuntime();
        var viewModel = await CreateViewModelAsync(console, runtime);

        for (var i = 0; i < 5; i++)
        {
            console.IsSessionRunning = false;
            console.IsCommandInProgress = false;
            console.RaiseSessionTerminated();
            await Task.Delay(350);
        }

        await WaitUntilAsync(() => viewModel.StatusText.Contains("Automatic recovery paused", StringComparison.Ordinal));

        Assert.Equal(3, console.Operations.Count(operation => operation == "start"));
        Assert.Contains("repeatedly", viewModel.ApplicationActivityText, StringComparison.OrdinalIgnoreCase);

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => console.Operations.Count(operation => operation == "start") == 4);

        Assert.Contains("restarted", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<MainWindowViewModel> CreateViewModelAsync(
        RecordingLiveConsoleService console,
        PowerShellRuntimeInfo? runtime = null)
    {
        return Task.Run(() => new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            console,
            new FakeExeExportService(),
            startupRuntimeInfo: runtime));
    }

    private static PowerShellRuntimeInfo CreateRuntime()
    {
        return new PowerShellRuntimeInfo(
            "PowerShell 7.6.2 x64",
            "Core",
            "7.6.2",
            new Version(7, 6, 2),
            "x64",
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            "CharacterizationTest",
            isPowerShell7OrLater: true,
            isWindowsPowerShell: false,
            isPreferred: true,
            isValidated: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}

internal sealed class RecordingLiveConsoleService : ILiveConsoleService
{
    public bool IsSessionRunning { get; set; }
    public bool IsCommandInProgress { get; set; }
    public bool IsHostAttached => true;
    public PowerShellRuntimeInfo? ActiveRuntime { get; private set; }
    public string? CurrentWorkingDirectory => null;
    public List<string> Operations { get; } = new();
    public LiveConsoleInterruptResult InterruptResult { get; set; } = new(
        false,
        false,
        false,
        false,
        false,
        null,
        TimeSpan.FromSeconds(2));
    public TaskCompletionSource<bool> InterruptObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> StartObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool>? ShutdownCompletion { get; set; }
    public TaskCompletionSource<LiveConsoleInterruptResult>? InterruptCompletion { get; set; }
    public bool StopResult { get; set; } = true;
    public Exception? StartException { get; set; }

    public event Action? ScriptExecutionCompleted;
    public event Action? CommandExecutionCompleted;
    public event Action? SessionTerminated;
    public event Action<int>? TerminalSessionStarted;
    public event Action<int>? TerminalSessionStopping;
    public event Action<int, string>? PromptReadyObserved;
    public event Action<int, string>? RawOutputReceived;

    public void AttachHost(IntPtr hostHandle, int width, int height) { }
    public void ResizeHost(int width, int height) { }
    public void ResizeConsole(int cols, int rows) { }
    public void FocusConsole() { }
    public int RawInputCount { get; private set; }
    public Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
    {
        RawInputCount++;
        return Task.CompletedTask;
    }

    public Task StartSessionAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("start");
        if (StartException is not null)
        {
            throw StartException;
        }

        TerminalSessionStarted?.Invoke(1);
        ActiveRuntime = runtime;
        IsSessionRunning = true;
        StartObserved.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(
        string commandText,
        Action<ExecutionOutputRecord> onOutput,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<LiveConsoleCommandResult> ExecuteScriptAsync(
        string documentDisplayName,
        string scriptContent,
        Action<ExecutionOutputRecord> onOutput,
        bool executeInCurrentScope = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null)
    {
        Operations.Add("stop");
        if (!StopResult)
        {
            return Task.FromResult(false);
        }

        TerminalSessionStopping?.Invoke(1);
        IsCommandInProgress = false;
        IsSessionRunning = false;
        ActiveRuntime = null;
        return Task.FromResult(true);
    }

    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Operations.Add("shutdown");
        TerminalSessionStopping?.Invoke(1);
        IsCommandInProgress = false;
        IsSessionRunning = false;
        ActiveRuntime = null;
        return ShutdownCompletion?.Task ?? Task.FromResult(true);
    }

    public Task SendInterruptAsync() => Task.CompletedTask;

    public async Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(
        Action<ExecutionOutputRecord>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("interrupt");
        InterruptObserved.TrySetResult(true);
        var result = InterruptCompletion is null
            ? InterruptResult
            : await InterruptCompletion.Task.ConfigureAwait(false);
        IsCommandInProgress = false;
        return result;
    }

    public void Dispose() { }

    public void RaiseScriptCompleted() => ScriptExecutionCompleted?.Invoke();
    public void RaiseCommandCompleted() => CommandExecutionCompleted?.Invoke();
    public void RaiseSessionTerminated() => SessionTerminated?.Invoke();
    public void RaiseRawOutput(string text) => RawOutputReceived?.Invoke(1, text);

    public void RaisePromptReady(int generation, string path) => PromptReadyObserved?.Invoke(generation, path);
}
