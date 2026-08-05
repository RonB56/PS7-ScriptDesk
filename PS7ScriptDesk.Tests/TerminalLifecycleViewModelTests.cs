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
    public async Task RestartCommand_StopsOwnedSessionBeforeStartingReplacement()
    {
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = false
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

    public event Action? ScriptExecutionCompleted;
    public event Action? CommandExecutionCompleted;
    public event Action? SessionTerminated;
    public event Action<string>? RawOutputReceived;

    public void AttachHost(IntPtr hostHandle, int width, int height) { }
    public void ResizeHost(int width, int height) { }
    public void ResizeConsole(int cols, int rows) { }
    public void FocusConsole() { }
    public Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StartSessionAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("start");
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
        IsCommandInProgress = false;
        IsSessionRunning = false;
        return Task.FromResult(true);
    }

    public Task SendInterruptAsync() => Task.CompletedTask;

    public Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(
        Action<ExecutionOutputRecord>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("interrupt");
        IsCommandInProgress = false;
        InterruptObserved.TrySetResult(true);
        return Task.FromResult(InterruptResult);
    }

    public void Dispose() { }

    public void RaiseScriptCompleted() => ScriptExecutionCompleted?.Invoke();
    public void RaiseCommandCompleted() => CommandExecutionCompleted?.Invoke();
    public void RaiseSessionTerminated() => SessionTerminated?.Invoke();
    public void RaiseRawOutput(string text) => RawOutputReceived?.Invoke(text);
}
