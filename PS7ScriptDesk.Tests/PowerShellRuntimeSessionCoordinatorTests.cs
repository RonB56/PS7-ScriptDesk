using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellRuntimeSessionCoordinatorTests
{
    [Fact]
    public async Task ConcurrentAcquisitionCreatesOneAuthoritativeRuntime()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            coordinator.AcquireAsync(runtime, _ => { })));

        Assert.Equal(1, terminal.StartCount);
        Assert.All(snapshots, snapshot => Assert.Equal(snapshots[0].SessionId, snapshot.SessionId));
        Assert.All(snapshots, snapshot => Assert.Equal(snapshots[0].Generation, snapshot.Generation));
        Assert.Equal(4321, coordinator.Snapshot.ProcessId);
    }

    [Fact]
    public async Task RestartChangesLogicalIdentityAndRejectsStaleReference()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var first = await coordinator.AcquireAsync(runtime, _ => { });
        var second = await coordinator.RestartAsync(runtime, _ => { });

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.True(second.Generation > first.Generation);
        Assert.False(coordinator.IsCurrent(first.SessionId, first.Generation));
        Assert.True(coordinator.IsCurrent(second.SessionId, second.Generation));
        Assert.Equal(2, terminal.StartCount);
    }

    [Fact]
    public async Task ShutdownIsIdempotentAndReleasesExecutionAdmission()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var current = await coordinator.AcquireAsync(runtime, _ => { });
        await using (await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false)) { }

        Assert.True(await coordinator.ShutdownAsync());
        Assert.True(await coordinator.ShutdownAsync());
        Assert.Equal(PowerShellRuntimeSessionState.Stopped, coordinator.Snapshot.State);
        Assert.False(coordinator.IsCurrent(current.SessionId, current.Generation));
        Assert.Equal(1, terminal.ShutdownCount);
    }

    [Fact]
    public async Task ShutdownWaitsForActiveExecutionLeaseBeforeDisposingTerminal()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var current = await coordinator.AcquireAsync(runtime, _ => { });
        await using var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        var shutdown = coordinator.ShutdownAsync();
        await Task.Delay(50);

        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, terminal.ShutdownCount);

        await lease.DisposeAsync();
        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, terminal.ShutdownCount);
    }

    [Fact]
    public async Task StartupFailurePublishesFaultedState()
    {
        var terminal = new CoordinatorTerminalFake { FailStart = true };
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcquireAsync(CreateRuntime(), _ => { }));

        Assert.Equal(PowerShellRuntimeSessionState.Faulted, coordinator.Snapshot.State);
        Assert.Contains("simulated", coordinator.Snapshot.FaultMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EarlyTerminalStartedNotificationDoesNotPublishReadyBeforeInputIsWritable()
    {
        var terminal = new CoordinatorTerminalFake { HoldStartAfterEarlyNotification = true };
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var states = new List<PowerShellRuntimeSessionState>();
        coordinator.StateChanged += snapshot => states.Add(snapshot.State);

        var acquire = coordinator.AcquireAsync(CreateRuntime(), _ => { });
        await terminal.EarlyStartNotificationObserved.Task;

        Assert.DoesNotContain(PowerShellRuntimeSessionState.Ready, states);
        Assert.Equal(PowerShellRuntimeSessionState.Starting, coordinator.Snapshot.State);

        terminal.CompleteHeldStart();
        var snapshot = await acquire;

        Assert.Equal(PowerShellRuntimeSessionState.Ready, snapshot.State);
        Assert.True(snapshot.InputWritable);
        Assert.Equal(4321, snapshot.ProcessId);
    }

    [Fact]
    public async Task StaleGenerationInputIsRejectedBeforeItReachesTheReplacementTerminal()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var first = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var second = await coordinator.RestartAsync(CreateRuntime(), _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WriteRawInputAsync(first.SessionId, first.Generation, "Get-Date"));

        Assert.Equal(0, terminal.RawInputWriteCount);
        await coordinator.WriteRawInputAsync(second.SessionId, second.Generation, "Get-Date");
        Assert.Equal(1, terminal.RawInputWriteCount);
    }

    private static PowerShellRuntimeInfo CreateRuntime() => new(
        "Test PowerShell", "Core", "7.6.0", new Version(7, 6, 0), "x64",
        Environment.ProcessPath!, "test", true, false, true);

    private sealed class CoordinatorTerminalFake : ILiveConsoleService
    {
        public bool IsSessionRunning { get; private set; }
        public bool IsInputWritable => IsSessionRunning;
        public bool IsCommandInProgress => false;
        public bool IsHostAttached => true;
        public PowerShellRuntimeInfo? ActiveRuntime { get; private set; }
        public int? ProcessId { get; private set; }
        public string? CurrentWorkingDirectory => null;
        public int StartCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int RawInputWriteCount { get; private set; }
        public bool FailStart { get; init; }
        public bool HoldStartAfterEarlyNotification { get; init; }
        public TaskCompletionSource<bool> EarlyStartNotificationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _heldStartCompletion;
        public event Action? ScriptExecutionCompleted;
        public event Action? CommandExecutionCompleted;
        public event Action? SessionTerminated;
        public event Action<int>? TerminalSessionStarted;
        public event Action<int>? TerminalSessionStopping;
        public event Action<int, string>? PromptReadyObserved;
        public event Action<int, string>? RawOutputReceived;

        public async Task StartSessionAsync(PowerShellRuntimeInfo runtime, Action<ExecutionOutputRecord> onOutput, string? startupWorkingDirectory = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailStart) throw new InvalidOperationException("simulated startup failure");
            StartCount++;
            if (HoldStartAfterEarlyNotification)
            {
                TerminalSessionStarted?.Invoke(StartCount);
                EarlyStartNotificationObserved.TrySetResult(true);
                _heldStartCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await _heldStartCompletion.Task.ConfigureAwait(false);
            }
            ActiveRuntime = runtime;
            ProcessId = 4321;
            IsSessionRunning = true;
            TerminalSessionStarted?.Invoke(StartCount);
        }

        public void CompleteHeldStart() => _heldStartCompletion?.TrySetResult(true);

        public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownCount++;
            TerminalSessionStopping?.Invoke(StartCount);
            IsSessionRunning = false;
            ActiveRuntime = null;
            ProcessId = null;
            return Task.FromResult(true);
        }

        public void AttachHost(IntPtr hostHandle, int width, int height) { }
        public void ResizeHost(int width, int height) { }
        public void ResizeConsole(int cols, int rows) { }
        public void FocusConsole() { }
        public Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
        {
            RawInputWriteCount++;
            return Task.CompletedTask;
        }
        public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(string commandText, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LiveConsoleCommandResult> ExecuteScriptAsync(string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null) => Task.FromResult(true);
        public Task SendInterruptAsync() => Task.CompletedTask;
        public Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(Action<ExecutionOutputRecord>? onOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
