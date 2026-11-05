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
    public async Task RestartWaitsForRuntimeExecutionLeaseAndReplacesOnlyAfterRelease()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var current = await coordinator.AcquireAsync(runtime, _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        var restart = coordinator.RestartAsync(runtime, _ => { });
        await Task.Delay(50);
        Assert.False(restart.IsCompleted);
        Assert.Equal(0, terminal.ShutdownCount);

        await lease.DisposeAsync();
        var replacement = await restart.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(replacement.Generation > current.Generation);
        Assert.Equal(PowerShellRuntimeSessionState.Ready, replacement.State);
        Assert.Equal(2, terminal.StartCount);
        await lease.DisposeAsync();
        Assert.Equal(replacement.SessionId, coordinator.Snapshot.SessionId);
        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task ResetDuringLongRunningEditorExecutionKeepsGenerationCurrentUntilLeaseDrains()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var current = await coordinator.AcquireAsync(runtime, _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        var reset = coordinator.RestartAsync(runtime, _ => { });
        await WaitForLifecyclePhaseAsync(coordinator, PowerShellRuntimeLifecyclePhase.Draining);

        Assert.Equal(PowerShellRuntimeSessionState.Executing, coordinator.Snapshot.State);
        Assert.True(coordinator.IsCurrent(current.SessionId, current.Generation));
        Assert.Equal(0, terminal.ShutdownCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false));
        await coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "\u0003");

        await lease.DisposeAsync();
        var replacement = await reset.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(replacement.Generation > current.Generation);
        Assert.Equal(PowerShellRuntimeSessionState.Ready, replacement.State);
        Assert.Equal(PowerShellRuntimeLifecyclePhase.Stable, replacement.LifecyclePhase);
    }

    [Fact]
    public async Task ResetDuringReadHostWaitKeepsInputAdmissionUntilEditorLeaseCompletes()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        var reset = coordinator.RestartAsync(CreateRuntime(), _ => { });
        await WaitForLifecyclePhaseAsync(coordinator, PowerShellRuntimeLifecyclePhase.Draining);
        await coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "interactive-value\r");

        Assert.Equal(1, terminal.RawInputWriteCount);
        Assert.True(coordinator.IsCurrent(current.SessionId, current.Generation));
        await lease.DisposeAsync();
        await reset.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ResetWhileDebuggerPausedPreservesDebuggerOwnershipUntilDebuggerStops()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var debugger = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: true);

        var reset = coordinator.RestartAsync(CreateRuntime(), _ => { });
        await WaitForLifecyclePhaseAsync(coordinator, PowerShellRuntimeLifecyclePhase.Draining);
        Assert.Equal(PowerShellRuntimeSessionState.Debugging, coordinator.Snapshot.State);
        Assert.True(coordinator.IsCurrent(current.SessionId, current.Generation));
        Assert.Equal(0, terminal.ShutdownCount);

        await debugger.DisposeAsync(); // Represents DAP stop or completion releasing debugger ownership.
        var replacement = await reset.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PowerShellRuntimeSessionState.Ready, replacement.State);
        Assert.True(replacement.Generation > current.Generation);
    }

    [Fact]
    public async Task CancellingResetWhileWaitingRestoresHealthySessionAndInput()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);
        using var cancellation = new CancellationTokenSource();

        var reset = coordinator.RestartAsync(CreateRuntime(), _ => { }, cancellationToken: cancellation.Token);
        await WaitForLifecyclePhaseAsync(coordinator, PowerShellRuntimeLifecyclePhase.Draining);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(PowerShellRuntimeSessionState.Executing, coordinator.Snapshot.State);
        Assert.Equal(PowerShellRuntimeLifecyclePhase.Stable, coordinator.Snapshot.LifecyclePhase);
        Assert.True(coordinator.IsCurrent(current.SessionId, current.Generation));
        await coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "still-healthy");
        Assert.Equal(0, terminal.ShutdownCount);
        await lease.DisposeAsync();
        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task RepeatedResetRequestsSerializeAndEachPublishACompletedGeneration()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var initial = await coordinator.AcquireAsync(runtime, _ => { });

        var firstReset = coordinator.RestartAsync(runtime, _ => { });
        var secondReset = coordinator.RestartAsync(runtime, _ => { });
        var replacements = await Task.WhenAll(firstReset, secondReset).WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(3, terminal.StartCount);
        Assert.Equal(2, terminal.ShutdownCount);
        Assert.All(replacements, replacement => Assert.True(replacement.Generation > initial.Generation));
        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
        Assert.Equal(PowerShellRuntimeLifecyclePhase.Stable, coordinator.Snapshot.LifecyclePhase);
    }

    [Fact]
    public async Task RuntimeReacquisitionDuringExecutionDoesNotPublishReadyOverActiveLease()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var runtime = CreateRuntime();
        var current = await coordinator.AcquireAsync(runtime, _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        var reacquired = await coordinator.AcquireAsync(runtime, _ => { }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PowerShellRuntimeSessionState.Executing, reacquired.State);
        Assert.Equal(PowerShellRuntimeSessionState.Executing, coordinator.Snapshot.State);
        Assert.Equal(current.Generation, reacquired.Generation);
        await lease.DisposeAsync();
        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task NormalExecutionCanReturnToInteractiveConsoleInput()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);
        await lease.DisposeAsync();

        await coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "Get-Date\r");

        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
        Assert.Equal(1, terminal.RawInputWriteCount);
    }

    [Fact]
    public async Task CancelledLeaseAdmissionAndRepeatedReleaseDoNotLeakOrDoubleReleaseAdmission()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var owner = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: true);
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false, cancellation.Token));
        await owner.DisposeAsync();
        await owner.DisposeAsync();

        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
        var next = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);
        await next.DisposeAsync();
        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task DebuggerLeaseExcludesEditorLeaseAcrossPauseAndControlLifetime()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var debugger = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: true);

        Assert.Equal(PowerShellRuntimeSessionState.Debugging, coordinator.Snapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false));
        Assert.Equal(PowerShellRuntimeSessionState.Debugging, coordinator.Snapshot.State);

        await debugger.DisposeAsync();
        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
        var editor = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);
        await editor.DisposeAsync();
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

    [Fact]
    public async Task CachedReadySnapshotCannotAdmitInputWhenBackingTransportStopsAcceptingIt()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });

        terminal.InputWritableOverride = false;

        Assert.Equal(PowerShellRuntimeSessionState.Ready, coordinator.Snapshot.State);
        Assert.False(coordinator.IsCurrent(current.SessionId, current.Generation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "input"));
        Assert.Equal(0, terminal.RawInputWriteCount);
    }

    [Fact]
    public async Task UnannouncedProcessReplacementRejectsOldRuntimeIdentity()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });

        terminal.ReplaceProcessWithoutNotification(9876);

        Assert.False(coordinator.IsCurrent(current.SessionId, current.Generation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "input"));
        Assert.Equal(0, terminal.RawInputWriteCount);
    }

    [Fact]
    public async Task ProcessReplacementDuringWriteCannotBeReportedAsDeliveryToCurrentSession()
    {
        var terminal = new CoordinatorTerminalFake
        {
            WriteCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });

        var write = coordinator.WriteRawInputAsync(current.SessionId, current.Generation, "input");
        await terminal.WriteObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        terminal.ReplaceProcessWithoutNotification(9876);
        terminal.WriteCompletion.SetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => write);
        Assert.Equal(1, terminal.RawInputWriteCount);
    }

    [Fact]
    public async Task ExecutionLeaseDoesNotReturnToReadyAfterInputTransportFailure()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        terminal.InputWritableOverride = false;
        await lease.DisposeAsync();

        Assert.Equal(PowerShellRuntimeSessionState.Faulted, coordinator.Snapshot.State);
        Assert.False(coordinator.IsCurrent(current.SessionId, current.Generation));
    }

    [Fact]
    public async Task ExecutionLeaseDoesNotReturnReadyWhenTransportWorksButReadinessIsUnverified()
    {
        var terminal = new CoordinatorTerminalFake();
        await using var coordinator = new PowerShellRuntimeSessionCoordinator(terminal);
        var current = await coordinator.AcquireAsync(CreateRuntime(), _ => { });
        var lease = await coordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);

        Assert.True(terminal.IsInputWritable);
        lease.MarkReadinessUnverified("PSES foreground completion was unknown.");
        await lease.DisposeAsync();

        Assert.Equal(PowerShellRuntimeSessionState.Faulted, coordinator.Snapshot.State);
        Assert.Contains("unknown", coordinator.Snapshot.FaultMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(coordinator.IsCurrent(current.SessionId, current.Generation));
    }

    private static PowerShellRuntimeInfo CreateRuntime() => new(
        "Test PowerShell", "Core", "7.6.0", new Version(7, 6, 0), "x64",
        Environment.ProcessPath!, "test", true, false, true);

    private static async Task WaitForLifecyclePhaseAsync(
        PowerShellRuntimeSessionCoordinator coordinator,
        PowerShellRuntimeLifecyclePhase phase)
    {
        if (coordinator.Snapshot.LifecyclePhase == phase) return;
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(PowerShellRuntimeSessionSnapshot snapshot)
        {
            if (snapshot.LifecyclePhase == phase) observed.TrySetResult(true);
        }

        coordinator.StateChanged += Handler;
        try
        {
            if (coordinator.Snapshot.LifecyclePhase == phase) return;
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { coordinator.StateChanged -= Handler; }
    }

    private sealed class CoordinatorTerminalFake : ILiveConsoleService
    {
        public bool IsSessionRunning { get; private set; }
        public bool IsInputWritable => InputWritableOverride ?? IsSessionRunning;
        public bool? InputWritableOverride { get; set; }
        public bool IsCommandInProgress => false;
        public bool IsHostAttached => true;
        public PowerShellRuntimeInfo? ActiveRuntime { get; private set; }
        public int? ProcessId { get; private set; }
        public string? CurrentWorkingDirectory => null;
        public int StartCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int RawInputWriteCount { get; private set; }
        public TaskCompletionSource<bool>? WriteCompletion { get; set; }
        public TaskCompletionSource<bool> WriteObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailStart { get; init; }
        public bool HoldStartAfterEarlyNotification { get; init; }
        public TaskCompletionSource<bool> EarlyStartNotificationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _heldStartCompletion;
        public event Action? ScriptExecutionCompleted { add { } remove { } }
        public event Action? CommandExecutionCompleted { add { } remove { } }
        public event Action? SessionTerminated { add { } remove { } }
        public event Action<int>? TerminalSessionStarted;
        public event Action<int>? TerminalSessionStopping;
        public event Action<int, string>? PromptReadyObserved { add { } remove { } }
        public event Action<int, string>? RawOutputReceived { add { } remove { } }

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
        public void ReplaceProcessWithoutNotification(int processId) => ProcessId = processId;

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
        public async Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
        {
            RawInputWriteCount++;
            WriteObserved.TrySetResult(true);
            if (WriteCompletion is not null) await WriteCompletion.Task.WaitAsync(cancellationToken);
        }
        public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(string commandText, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LiveConsoleCommandResult> ExecuteScriptAsync(string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null) => Task.FromResult(true);
        public Task SendInterruptAsync() => Task.CompletedTask;
        public Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(Action<ExecutionOutputRecord>? onOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
