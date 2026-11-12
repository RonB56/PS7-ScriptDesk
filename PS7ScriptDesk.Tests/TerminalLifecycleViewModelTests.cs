using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.UI.ViewModels;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalLifecycleViewModelTests
{
    [Fact]
    public async Task CoordinatorOwnershipKeepsRunDisabledAndInterruptAvailableAfterTerminalCompletionEvent()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var terminal = new InteractiveTerminalCoordinator();
        terminal.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "ready");
        var execution = new RecordingEditorExecutionCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), terminal, execution);
        Assert.True(viewModel.IsRunAvailable);

        execution.SetActive(new ActiveEditorExecutionSnapshot(
            Guid.NewGuid(), 1, EditorExecutionCoordinator.LegacyBackendId,
            new ExecutionBackendCapabilities(true, true, true, false, true, false, true, true, true, true),
            DateTimeOffset.UtcNow));
        console.RaiseCommandCompleted();

        Assert.False(viewModel.IsRunAvailable);
        Assert.True(viewModel.StopCommand.CanExecute(null));

        execution.SetActive(null);
        Assert.True(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task OldGenerationCommandCompletionCannotIdleTheReplacementTerminal()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), terminal);
        console.RaiseSessionStarted(2);
        Assert.Equal(InteractiveTerminalState.Starting, terminal.State);

        console.RaiseCommandCompleted(1);

        Assert.Equal(InteractiveTerminalState.Starting, terminal.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RejectedTerminalInputShowsARecoverableStatusInsteadOfSilentlyFailing()
    {
        var interactive = new InteractiveTerminalCoordinator();
        interactive.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            RawInputException = new InvalidOperationException("stale terminal")
        };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), interactive);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.WriteRawInputAsync("Get-Date\r"));

        Assert.Contains("delivery could not be verified", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, interactive.State);
        Assert.True(viewModel.IsRunAvailable);
        console.RawInputException = null;
        await viewModel.WriteRawInputAsync("Get-Date\r");
        Assert.Contains("Get-Date\r", console.RawInputs);
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, interactive.State);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ProductionViewModelDispatchHoldsRuntimeLeaseUntilBackendCompletesAndRestartWaits()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        var initial = await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var backend = new BlockingExecutionBackend();
        var executionCoordinator = new EditorExecutionCoordinator(
            [backend],
            new EditorExecutionFeatureGate(false),
            shadowMode: false,
            ExecutionBackendAvailability.Unavailable,
            new PowerShellExecutionRequestClassifier());
        var viewModel = await CreateViewModelAsync(console, runtime, executionCoordinator: executionCoordinator, runtimeSessionCoordinator: runtimeCoordinator);
        console.RaiseSessionStarted(console.CurrentSessionGeneration);
        var dispatch = typeof(MainWindowViewModel).GetMethod("ExecuteEditorThroughCoordinatorAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(dispatch);

        var executeTask = (Task)dispatch!.Invoke(viewModel, ["lease-test.ps1", "Write-Output 'ok'", EditorExecutionMode.ScriptCall, false, null])!;
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PowerShellRuntimeSessionState.Executing, runtimeCoordinator.Snapshot.State);
        console.RaisePromptReady(console.CurrentSessionGeneration, "C:\\");
        Assert.Equal(InteractiveTerminalState.Starting, ((IInteractiveTerminalCoordinator?)typeof(MainWindowViewModel)
            .GetField("_interactiveTerminalCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel))!.State);

        var restart = runtimeCoordinator.RestartAsync(runtime, _ => { });
        await Task.Delay(50);
        Assert.False(restart.IsCompleted);

        backend.Complete();
        await executeTask.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = await restart.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(replacement.Generation > initial.Generation);
        Assert.Equal(PowerShellRuntimeSessionState.Ready, replacement.State);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ProductionViewModelDispatchReleasesRuntimeLeaseWhenBackendThrows()
    {
        var console = new RecordingLiveConsoleService();
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        var current = await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var backend = new BlockingExecutionBackend { Failure = new InvalidOperationException("simulated backend fault") };
        var executionCoordinator = new EditorExecutionCoordinator(
            [backend], new EditorExecutionFeatureGate(false), shadowMode: false,
            ExecutionBackendAvailability.Unavailable, new PowerShellExecutionRequestClassifier());
        var viewModel = await CreateViewModelAsync(console, runtime, executionCoordinator: executionCoordinator, runtimeSessionCoordinator: runtimeCoordinator);
        console.RaiseSessionStarted(console.CurrentSessionGeneration);
        var dispatch = typeof(MainWindowViewModel).GetMethod("ExecuteEditorThroughCoordinatorAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var executeTask = (Task)dispatch!.Invoke(viewModel, ["fault-test.ps1", "throw 'fault'", EditorExecutionMode.ScriptCall, false, null])!;
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Complete();
        await executeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PowerShellRuntimeSessionState.Ready, runtimeCoordinator.Snapshot.State);
        var next = await runtimeCoordinator.AcquireExecutionAsync(current.SessionId, current.Generation, debugger: false);
        await next.DisposeAsync();
        viewModel.Dispose();
    }

    [Fact]
    public async Task ProductionViewModelDispatchKeepsRuntimeFaultedWhenBackendReadinessIsUnknown()
    {
        var console = new RecordingLiveConsoleService();
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        var current = await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var backend = new BlockingExecutionBackend { ResultStatus = EditorExecutionStatus.Unknown };
        var executionCoordinator = new EditorExecutionCoordinator(
            [backend], new EditorExecutionFeatureGate(false), shadowMode: false,
            ExecutionBackendAvailability.Unavailable, new PowerShellExecutionRequestClassifier());
        var viewModel = await CreateViewModelAsync(console, runtime, executionCoordinator: executionCoordinator, runtimeSessionCoordinator: runtimeCoordinator);
        var dispatch = typeof(MainWindowViewModel).GetMethod("ExecuteEditorThroughCoordinatorAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var executeTask = (Task)dispatch!.Invoke(viewModel, ["unknown-test.ps1", "Write-Output 'uncertain'", EditorExecutionMode.ScriptCall, false, null])!;
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Complete();
        await executeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(console.IsInputWritable);
        Assert.Equal(PowerShellRuntimeSessionState.Faulted, runtimeCoordinator.Snapshot.State);
        Assert.False(runtimeCoordinator.IsCurrent(current.SessionId, current.Generation));
        console.RaisePromptReady(console.CurrentSessionGeneration, "C:\\");
        Assert.Equal(PowerShellRuntimeSessionState.Faulted, runtimeCoordinator.Snapshot.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Theory]
    [InlineData("\u001b[I")]
    [InlineData("\u001b[O")]
    public async Task FocusProtocolInput_DoesNotLeaveIdleStateOrDisableRun(string input)
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        await viewModel.WriteRawInputAsync(input);

        Assert.Equal([input], console.RawInputs);
        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(coordinator.CanStartEditorExecution);
        Assert.True(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Theory]
    [InlineData("\u001b[M!!")]
    [InlineData("\u001b[<0;10;4M")]
    [InlineData("\u001b[<0;10;4m")]
    public async Task MouseProtocolInput_IsForwardedWithoutDisablingRun(string input)
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        await viewModel.WriteRawInputAsync(input);

        Assert.Equal([input], console.RawInputs);
        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.Null(viewModel.RunDisabledReason);
        viewModel.Dispose();
    }

    [Fact]
    public async Task EscapeDuringInteractiveCommand_IsForwardedAndWaitsForVerifiedPrompt()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveCommandRunning, "interactive application owns terminal");
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        await viewModel.WriteRawInputAsync("\u001b");

        Assert.Equal(["\u001b"], console.RawInputs);
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);

        console.RaisePromptReady(console.CurrentSessionGeneration, "C:\\");

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task EscapeWithDeferredOwnedPrompt_ReconcilesAfterExecutionOwnershipRelease()
    {
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var coordinator = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.RaiseSessionStarted(1);
        coordinator.SetState(InteractiveTerminalState.InteractiveCommandRunning, "structured editor request owns terminal");

        var requestId = Guid.NewGuid();
        var requestField = typeof(MainWindowViewModel).GetField("_activeStructuredExecutionRequestId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(requestField);
        requestField!.SetValue(viewModel, requestId);

        await viewModel.WriteRawInputAsync("\u001b");
        var promptObservation = new StructuredPromptReadyObservation(requestId, 1, console.ProcessId!.Value, "C:\\");
        var observe = typeof(MainWindowViewModel).GetMethod("OnStructuredPromptReadyObserved", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(observe);
        observe!.Invoke(viewModel, [promptObservation]);

        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);

        requestField.SetValue(viewModel, null);
        var reconcile = typeof(MainWindowViewModel).GetMethod("ReconcileDeferredStructuredPromptObservation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(reconcile);
        reconcile!.Invoke(viewModel, [requestId, true]);

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task EscapeWhileEditingDoesNotPrematurelyAdmitEditorExecution()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveInputEditing, "unfinished user input");
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        await viewModel.WriteRawInputAsync("\u001b");

        Assert.Equal(["\u001b"], console.RawInputs);
        Assert.Equal(InteractiveTerminalState.InteractiveInputEditing, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Theory]
    [InlineData("\u001b[A")]
    [InlineData("\u001bOA")]
    [InlineData("\u001b[B")]
    [InlineData("\u001bOB")]
    public async Task HistoryNavigationInput_DisablesRunAndShowsConsoleEditingCue(string input)
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var viewModel = await CreateViewModelAsync(new RecordingLiveConsoleService(), CreateRuntime(), coordinator);

        await viewModel.WriteRawInputAsync(input);

        Assert.Equal(InteractiveTerminalState.InteractiveInputEditing, coordinator.State);
        Assert.False(coordinator.CanStartEditorExecution);
        Assert.False(viewModel.IsRunAvailable);
        Assert.Contains("command is being edited", viewModel.RunDisabledReason, StringComparison.Ordinal);
        Assert.Contains("Console input active", viewModel.ConsoleInputStatusText, StringComparison.Ordinal);
        viewModel.Dispose();
    }

    [Fact]
    public async Task CtrlCAfterHistoryNavigationWaitsForPromptBeforeReenablingRun()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        await viewModel.WriteRawInputAsync("\u001b[A");
        await viewModel.WriteRawInputAsync("\u0003");

        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);
        Assert.Equal(string.Empty, viewModel.ConsoleInputStatusText);

        console.RaisePromptReady(0, "C:\\");

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.Null(viewModel.RunDisabledReason);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RendererReadyBeforePromptReadiness_PreservesStartingWaitState()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.RaiseSessionStarted(7);

        viewModel.NotifyTerminalRendererReady();

        Assert.Equal(InteractiveTerminalState.Starting, coordinator.State);
        viewModel.Dispose();
    }

    [Fact]
    public async Task LateRendererReady_DoesNotRegressPromptReadyState()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.RaiseSessionStarted(8);
        console.RaisePromptReady(8, "C:\\");

        viewModel.NotifyTerminalRendererReady();

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(coordinator.CanStartEditorExecution);
        viewModel.Dispose();
    }

    [Fact]
    public async Task LateSessionStarted_DoesNotRegressPromptReadyStateOrDisableRun()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        console.SetCurrentGeneration(9);
        console.RaisePromptReady(9, "C:\\");
        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.RunCommand.CanExecute(null));

        // PSES output can deliver the first prompt before the session-start
        // lifecycle callback. The late callback must not regress this ready
        // generation back to Starting.
        console.RaiseSessionStarted(9);

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task RenderedDriveRootPrompt_ReenablesRunAfterInteractiveCommand()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.RaiseSessionStarted(1);
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "initial prompt");

        await viewModel.WriteRawInputAsync("2 + 2\r");
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);

        viewModel.PublishInteractiveTerminalOutput(1, "4\r\nPS C:\\>");

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        Assert.Null(viewModel.RunDisabledReason);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RenderedDriveRootPrompt_DoesNotAdmitBusyOrStaleSession()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var coordinator = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.RaiseSessionStarted(1);
        coordinator.SetState(InteractiveTerminalState.InteractiveCommandRunning, "command active");

        console.IsCommandInProgress = true;
        viewModel.PublishInteractiveTerminalOutput(1, "PS C:\\>");
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);

        console.IsCommandInProgress = false;
        console.RaiseSessionStarted(2);
        viewModel.PublishInteractiveTerminalOutput(1, "PS C:\\>");
        Assert.Equal(2, coordinator.Snapshot.Generation);
        Assert.Equal(InteractiveTerminalState.Starting, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);

        viewModel.PublishInteractiveTerminalOutput(2, "PS C:\\>");
        Assert.True(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RendererReady_ProjectsAlreadyRunningBackendIntoVisibleSessionStateWithoutRestart()
    {
        var console = new RecordingLiveConsoleService();
        var runtime = CreateRuntime();
        await console.StartSessionAsync(runtime, _ => { });
        var viewModel = await CreateViewModelAsync(console, runtime);

        Assert.Equal("ConPTY terminal: not started", viewModel.ConsoleSessionText);
        var startsBeforeRendererReady = console.Operations.Count(operation => operation == "start");

        viewModel.NotifyTerminalRendererReady();

        Assert.Contains("running", viewModel.ConsoleSessionText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not started", viewModel.ConsoleSessionText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(startsBeforeRendererReady, console.Operations.Count(operation => operation == "start"));
        viewModel.Dispose();
    }

    [Fact]
    public async Task CoordinatorStateChangesRefreshRunPropertyAndCommandAvailability()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.Starting, "test-starting");
        var viewModel = await CreateViewModelAsync(new RecordingLiveConsoleService(), CreateRuntime(), coordinator);
        var propertyChanges = new List<string?>();
        var commandChangeCount = 0;
        viewModel.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);
        viewModel.RunCommand.CanExecuteChanged += (_, _) => commandChangeCount++;

        Assert.False(viewModel.IsRunAvailable);
        Assert.False(viewModel.RunCommand.CanExecute(null));

        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");

        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        Assert.Contains(nameof(MainWindowViewModel.IsRunAvailable), propertyChanges);
        Assert.True(commandChangeCount > 0);
        viewModel.Dispose();
    }

    [Fact]
    public async Task DebuggerLeaseReleaseRefreshesCommandsWhenTerminalIsAlreadyIdle()
    {
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        var current = await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var startsBeforeDebug = console.Operations.Count(operation => operation == "start");
        var processIdBeforeDebug = console.ProcessId;
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(
            console,
            runtime,
            terminal,
            runtimeSessionCoordinator: runtimeCoordinator);
        console.RaiseSessionStarted(1);
        terminal.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "debugger-test-idle");
        var debugSessionId = Guid.NewGuid();
        var debugLease = await runtimeCoordinator.AcquireExecutionAsync(
            current.SessionId,
            current.Generation,
            debugger: true);
        viewModel.IsDebugSessionActive = true;
        viewModel.BeginDebuggerPromptReadinessReconciliation(debugSessionId);
        console.RaisePromptReady(1, "C:\\");
        viewModel.IsDebugSessionActive = false;

        Assert.Equal(PowerShellRuntimeSessionState.Debugging, runtimeCoordinator.Snapshot.State);
        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, terminal.State);
        Assert.False(viewModel.IsRunAvailable);
        var commandChangeCount = 0;
        var availabilityPropertyChangeCount = 0;
        viewModel.RunCommand.CanExecuteChanged += (_, _) => commandChangeCount++;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.IsRunAvailable) or nameof(MainWindowViewModel.RunSelectionDisabledReason))
                availabilityPropertyChangeCount++;
        };

        await debugLease.DisposeAsync();
        Assert.Equal(PowerShellRuntimeSessionState.Ready, runtimeCoordinator.Snapshot.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.Equal(0, commandChangeCount);

        viewModel.ReconcileDebuggerPromptReadiness(debugSessionId);

        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, terminal.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
        Assert.True(commandChangeCount > 0);
        Assert.True(availabilityPropertyChangeCount > 0);
        Assert.Equal(startsBeforeDebug, console.Operations.Count(operation => operation == "start"));

        var notificationCountAfterCompletion = commandChangeCount;
        viewModel.ReconcileDebuggerPromptReadiness(debugSessionId);
        Assert.Equal(notificationCountAfterCompletion, commandChangeCount);

        var secondDebugSessionId = Guid.NewGuid();
        var secondDebugLease = await runtimeCoordinator.AcquireExecutionAsync(
            current.SessionId,
            current.Generation,
            debugger: true);
        viewModel.IsDebugSessionActive = true;
        viewModel.BeginDebuggerPromptReadinessReconciliation(secondDebugSessionId);
        viewModel.ReconcileDebuggerPromptReadiness(debugSessionId);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.IsDebugSessionActive = false;
        await secondDebugLease.DisposeAsync();
        viewModel.ReconcileDebuggerPromptReadiness(secondDebugSessionId);

        Assert.Equal(PowerShellRuntimeSessionState.Ready, runtimeCoordinator.Snapshot.State);
        Assert.True(viewModel.IsRunAvailable);
        Assert.False(viewModel.StopCommand.CanExecute(null));
        Assert.Equal(startsBeforeDebug, console.Operations.Count(operation => operation == "start"));
        Assert.Equal(processIdBeforeDebug, console.ProcessId);
        viewModel.Dispose();
    }

    [Fact]
    public async Task DebuggerCommandRefreshKeepsRunDisabledWhileTerminalIsBusy()
    {
        var console = new RecordingLiveConsoleService { IsCommandInProgress = true };
        console.PrimeSession(1);
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        var current = await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(
            console,
            runtime,
            terminal,
            runtimeSessionCoordinator: runtimeCoordinator);
        console.RaiseSessionStarted(1);
        terminal.SetState(InteractiveTerminalState.InteractiveCommandRunning, "debugger-test-busy");
        var debugSessionId = Guid.NewGuid();
        var debugLease = await runtimeCoordinator.AcquireExecutionAsync(
            current.SessionId,
            current.Generation,
            debugger: true);
        viewModel.IsDebugSessionActive = true;
        viewModel.BeginDebuggerPromptReadinessReconciliation(debugSessionId);
        console.RaisePromptReady(1, "C:\\");
        viewModel.IsDebugSessionActive = false;
        await debugLease.DisposeAsync();

        viewModel.ReconcileDebuggerPromptReadiness(debugSessionId);

        Assert.Equal(PowerShellRuntimeSessionState.Ready, runtimeCoordinator.Snapshot.State);
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, terminal.State);
        Assert.False(viewModel.IsRunAvailable);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task StaleDebuggerPromptGenerationCannotRestoreRunAvailability()
    {
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        var current = await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(
            console,
            runtime,
            terminal,
            runtimeSessionCoordinator: runtimeCoordinator);
        console.RaiseSessionStarted(1);
        terminal.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "debugger-generation-one-idle");
        var debugSessionId = Guid.NewGuid();
        var debugLease = await runtimeCoordinator.AcquireExecutionAsync(
            current.SessionId,
            current.Generation,
            debugger: true);
        viewModel.IsDebugSessionActive = true;
        viewModel.BeginDebuggerPromptReadinessReconciliation(debugSessionId);
        console.RaisePromptReady(1, "C:\\");

        console.RaiseSessionStarted(2);
        viewModel.IsDebugSessionActive = false;
        await debugLease.DisposeAsync();
        viewModel.ReconcileDebuggerPromptReadiness(debugSessionId);

        Assert.Equal(2, console.CurrentSessionGeneration);
        Assert.Equal(InteractiveTerminalState.Starting, terminal.State);
        Assert.Equal(PowerShellRuntimeSessionState.Ready, runtimeCoordinator.Snapshot.State);
        Assert.False(viewModel.IsRunAvailable);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task CoordinatorEditingAndCommandRunningStatesDisableRunUntilPromptReturns()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var viewModel = await CreateViewModelAsync(new RecordingLiveConsoleService(), CreateRuntime(), coordinator);

        Assert.True(viewModel.IsRunAvailable);

        coordinator.SetState(InteractiveTerminalState.InteractiveInputEditing, "test-editing");
        Assert.False(viewModel.IsRunAvailable);
        Assert.False(viewModel.RunCommand.CanExecute(null));

        coordinator.SetState(InteractiveTerminalState.InteractiveCommandRunning, "test-running");
        Assert.False(viewModel.IsRunAvailable);

        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle-again");
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task ConsoleEditingExposesReasonAwareTooltipAndStatusCue()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var viewModel = await CreateViewModelAsync(new RecordingLiveConsoleService(), CreateRuntime(), coordinator);

        Assert.True(viewModel.IsRunAvailable);
        Assert.Null(viewModel.RunDisabledReason);
        Assert.Equal(string.Empty, viewModel.ConsoleInputStatusText);

        coordinator.SetState(InteractiveTerminalState.InteractiveInputEditing, "test-editing");

        Assert.False(viewModel.IsRunAvailable);
        Assert.Contains("command is being edited", viewModel.RunDisabledReason, StringComparison.Ordinal);
        Assert.Contains("Press Ctrl+C", viewModel.RunDisabledReason, StringComparison.Ordinal);
        Assert.Contains("Console input active", viewModel.ConsoleInputStatusText, StringComparison.Ordinal);

        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle-again");

        Assert.True(viewModel.IsRunAvailable);
        Assert.Null(viewModel.RunDisabledReason);
        Assert.Equal(string.Empty, viewModel.ConsoleInputStatusText);
        viewModel.Dispose();
    }

    [Fact]
    public async Task OtherDisabledStateDoesNotSuggestCtrlC()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.Starting, "test-starting");
        var viewModel = await CreateViewModelAsync(new RecordingLiveConsoleService(), CreateRuntime(), coordinator);

        Assert.False(viewModel.IsRunAvailable);
        Assert.DoesNotContain("Press Ctrl+C", viewModel.RunDisabledReason, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.ConsoleInputStatusText);
        viewModel.Dispose();
    }

    [Fact]
    public async Task CoordinatorStoppingAndUnavailableStatesDisableSharedRunEnablement()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt, "test-idle");
        var viewModel = await CreateViewModelAsync(new RecordingLiveConsoleService(), CreateRuntime(), coordinator);

        Assert.True(viewModel.IsRunAvailable);
        coordinator.SetState(InteractiveTerminalState.Stopping, "test-stopping");
        Assert.False(viewModel.IsRunAvailable);
        coordinator.SetState(InteractiveTerminalState.Unavailable, "test-unavailable");
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

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
        await WaitUntilAsync(() => viewModel.StatusText == "Interrupt completed" && viewModel.RestartConsoleCommand.CanExecute(null));

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

        for (var i = 0; i < 3; i++)
        {
            console.IsSessionRunning = false;
            console.IsCommandInProgress = false;
            console.RaiseSessionTerminated();
            await WaitUntilAsync(() => console.Operations.Count(operation => operation == "start") == i + 1 &&
                console.IsSessionRunning &&
                viewModel.ApplicationActivityText.Split("PowerShell terminal restarted with", StringSplitOptions.None).Length >= i + 2);
        }

        console.IsSessionRunning = false;
        console.IsCommandInProgress = false;
        console.RaiseSessionTerminated();
        await WaitUntilAsync(() => viewModel.StatusText.Contains("Automatic recovery paused", StringComparison.Ordinal));

        Assert.Equal(3, console.Operations.Count(operation => operation == "start"));
        Assert.Contains("repeatedly", viewModel.ApplicationActivityText, StringComparison.OrdinalIgnoreCase);

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => console.Operations.Count(operation => operation == "start") == 4);

        Assert.Contains("restarted", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearConsoleWithoutSession_PreservesOutputAndExplainsResetRecovery()
    {
        var console = new RecordingLiveConsoleService();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime());
        var originalOutput = viewModel.TerminalDisplayText;
        var rendererClearCount = 0;
        viewModel.SetTerminalSessionControls(() => rendererClearCount++);

        viewModel.ClearConsoleCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.StatusText.Contains("Use Reset Console", StringComparison.Ordinal));
        Assert.Equal(originalOutput, viewModel.TerminalDisplayText);
        Assert.Equal(0, rendererClearCount);
        Assert.Empty(console.RawInputs);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ResetPromptObservedDuringReset_IsReconciledAfterReplacementStarts()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        var console = new RecordingLiveConsoleService();
        var runtime = CreateRuntime();
        await using var runtimeCoordinator = new PowerShellRuntimeSessionCoordinator(console);
        await runtimeCoordinator.AcquireAsync(runtime, _ => { });
        var viewModel = await CreateViewModelAsync(
            console,
            runtime,
            coordinator,
            runtimeSessionCoordinator: runtimeCoordinator);
        InteractiveTerminalState stateWhenPromptObserved = InteractiveTerminalState.Unavailable;
        PowerShellRuntimeLifecyclePhase? lifecycleWhenPromptObserved = null;
        console.SessionStartCallback = (session, generation) =>
        {
            if (generation != 2) return;
            lifecycleWhenPromptObserved = runtimeCoordinator.Snapshot.LifecyclePhase;
            session.RaisePromptReady(generation, "C:\\");
            stateWhenPromptObserved = coordinator.State;
        };

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => coordinator.State == InteractiveTerminalState.InteractiveIdleAtPrompt);

        Assert.Equal(InteractiveTerminalState.Starting, stateWhenPromptObserved);
        Assert.Equal(PowerShellRuntimeLifecyclePhase.Replacing, lifecycleWhenPromptObserved);
        Assert.Equal(2, console.CurrentSessionGeneration);
        Assert.True(viewModel.IsRunAvailable);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        viewModel.Dispose();
    }

    [Fact]
    public async Task ResetPromptFromOldGeneration_IsNotDeferredOrAccepted()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.SessionStartCallback = (session, generation) =>
        {
            if (generation == 2) session.RaisePromptReady(1, "C:\\");
        };

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => console.CurrentSessionGeneration == 2 && viewModel.RestartConsoleCommand.CanExecute(null));

        Assert.Equal(InteractiveTerminalState.Starting, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ResetWithoutPrompt_DoesNotManufactureReadiness()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => console.CurrentSessionGeneration == 2 && viewModel.RestartConsoleCommand.CanExecute(null));

        Assert.Equal(InteractiveTerminalState.Starting, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task TerminatedReplacement_DiscardsPromptDeferredDuringReset()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.SessionStartCallback = (session, generation) =>
        {
            if (generation != 2) return;
            session.RaisePromptReady(generation, "C:\\");
            session.IsSessionRunning = false;
            session.ClearProcessIdentity();
            session.RaiseSessionTerminated();
        };

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => console.CurrentSessionGeneration == 2 && viewModel.RestartConsoleCommand.CanExecute(null));

        Assert.Equal(InteractiveTerminalState.Unavailable, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RepeatedReset_DoesNotReusePriorGenerationPrompt()
    {
        var coordinator = new InteractiveTerminalCoordinator();
        var console = new RecordingLiveConsoleService();
        console.PrimeSession(1);
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), coordinator);
        console.SessionStartCallback = (session, generation) =>
        {
            if (generation == 2) session.RaisePromptReady(generation, "C:\\");
        };

        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => coordinator.State == InteractiveTerminalState.InteractiveIdleAtPrompt);
        await WaitUntilAsync(() => viewModel.RestartConsoleCommand.CanExecute(null));
        viewModel.RestartConsoleCommand.Execute(null);
        await WaitUntilAsync(() => console.CurrentSessionGeneration == 3 && viewModel.RestartConsoleCommand.CanExecute(null));

        Assert.Equal(InteractiveTerminalState.Starting, coordinator.State);
        Assert.False(viewModel.IsRunAvailable);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ClearConsoleLiveSession_SendsClsAndKeepsRendererResetUnused()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), terminal);
        console.PrimeSession(1);
        console.RaisePromptReady(1, "C:\\");
        await WaitUntilAsync(() => terminal.State == InteractiveTerminalState.InteractiveIdleAtPrompt);
        var rendererClearCount = 0;
        var reconcileCount = 0;
        viewModel.SetTerminalSessionControls(
            () => rendererClearCount++,
            reconcileAuthoritativeClear: () => reconcileCount++);

        viewModel.ClearConsoleCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.StatusText.Contains("Clear command submitted", StringComparison.Ordinal));
        Assert.Equal(new[] { "cls" }, console.CommandTexts);
        Assert.True(console.IsCommandInProgress);
        Assert.Equal(0, rendererClearCount);
        Assert.Equal(1, reconcileCount);
        viewModel.Dispose();
    }

    [Fact]
    public async Task DirectConsoleSubmissionKeepsAdmissionOwnedUntilMatchingPromptObservation()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), terminal);
        console.PrimeSession(1);
        console.RaisePromptReady(1, "C:\\");
        await WaitUntilAsync(() => terminal.State == InteractiveTerminalState.InteractiveIdleAtPrompt);
        viewModel.ConsoleCommandText = "Start-Sleep 20";

        await viewModel.SubmitConsoleCommandAsync();

        Assert.Single(console.CommandRequestIds);
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, terminal.State);
        Assert.True(viewModel.IsExecutionRunning);
        Assert.False(viewModel.IsRunAvailable);

        console.RaiseInteractiveCommandCompleted(Guid.NewGuid());
        Assert.Equal(InteractiveTerminalState.InteractiveCommandRunning, terminal.State);
        Assert.True(viewModel.IsExecutionRunning);

        console.RaiseInteractiveCommandCompleted(console.CommandRequestIds[0]);
        await WaitUntilAsync(() => terminal.State == InteractiveTerminalState.InteractiveIdleAtPrompt);
        Assert.False(viewModel.IsExecutionRunning);
        Assert.Contains("exit status is not independently available", viewModel.StatusText, StringComparison.Ordinal);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ClearConsoleWriteFailure_PreservesRendererAndReportsFailure()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true, RawInputException = new IOException("session closed") };
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), terminal);
        console.PrimeSession(1);
        console.RaisePromptReady(1, "C:\\");
        await WaitUntilAsync(() => terminal.State == InteractiveTerminalState.InteractiveIdleAtPrompt);
        var originalOutput = viewModel.TerminalDisplayText;
        var rendererClearCount = 0;
        viewModel.SetTerminalSessionControls(() => rendererClearCount++);

        viewModel.ClearConsoleCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.StatusText.Contains("Terminal clear failed", StringComparison.Ordinal));
        Assert.Equal(originalOutput, viewModel.TerminalDisplayText);
        Assert.Equal(0, rendererClearCount);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ClearConsoleSessionShutdownRace_PreservesOutputAndReportsFailure()
    {
        var console = new RecordingLiveConsoleService { IsSessionRunning = true };
        console.BeforeRawInputWrite = () =>
        {
            console.IsSessionRunning = false;
            throw new IOException("session stopped during clear admission");
        };
        var terminal = new InteractiveTerminalCoordinator();
        var viewModel = await CreateViewModelAsync(console, CreateRuntime(), terminal);
        console.PrimeSession(1);
        console.RaisePromptReady(1, "C:\\");
        await WaitUntilAsync(() => terminal.State == InteractiveTerminalState.InteractiveIdleAtPrompt);
        var originalOutput = viewModel.TerminalDisplayText;
        var rendererClearCount = 0;
        viewModel.SetTerminalSessionControls(() => rendererClearCount++);

        viewModel.ClearConsoleCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.StatusText.Contains("Terminal clear failed", StringComparison.Ordinal));
        Assert.Equal(originalOutput, viewModel.TerminalDisplayText);
        Assert.Equal(0, rendererClearCount);
        viewModel.Dispose();
    }

    private static Task<MainWindowViewModel> CreateViewModelAsync(
        RecordingLiveConsoleService console,
        PowerShellRuntimeInfo? runtime = null,
        IInteractiveTerminalCoordinator? coordinator = null,
        IEditorExecutionCoordinator? executionCoordinator = null,
        IPowerShellRuntimeSessionCoordinator? runtimeSessionCoordinator = null)
    {
        return Task.Run(() => new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            console,
            new FakeExeExportService(),
            startupRuntimeInfo: runtime,
            interactiveTerminalCoordinator: coordinator,
            executionCoordinator: executionCoordinator,
            runtimeSessionCoordinator: runtimeSessionCoordinator));
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

    private sealed class RecordingEditorExecutionCoordinator : IEditorExecutionCoordinator
    {
        public bool IsShadowMode => false;
        public bool IsRollbackEnabled => false;
        public ActiveEditorExecutionSnapshot? ActiveExecution { get; private set; }
        public event Action<EditorExecutionEvent>? EventPublished { add { } remove { } }
        public event Action? ActiveExecutionChanged;

        public void SetActive(ActiveEditorExecutionSnapshot? active)
        {
            ActiveExecution = active;
            ActiveExecutionChanged?.Invoke();
        }

        public ExecutionRoutingDecision Route(EditorExecutionRequest request) => throw new NotSupportedException();
        public Task<ExecutionRoutingDecision> ObserveAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class BlockingExecutionBackend : IEditorExecutionBackend
    {
        private static readonly ExecutionBackendCapabilities BackendCapabilities = new(true, true, true, false, true, false, true, true, true, true);
        public string BackendId => EditorExecutionCoordinator.LegacyBackendId;
        public ExecutionBackendCapabilities Capabilities => BackendCapabilities;
        public ExecutionBackendAvailability Availability => ExecutionBackendAvailability.Available;
        public string? AvailabilityReason => null;
        public event Action<EditorExecutionEvent>? EventPublished;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public EditorExecutionStatus ResultStatus { get; init; } = EditorExecutionStatus.Completed;

        public void Complete() => _completion.TrySetResult(true);
        public async Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Failure is not null) throw Failure;
            var now = DateTimeOffset.UtcNow;
            EventPublished?.Invoke(new EditorExecutionEvent(EditorExecutionEventKind.Completed, request.RequestId,
                request.SessionGeneration, 1, Timestamp: now, BackendId: BackendId));
            return new EditorExecutionResult(request.RequestId, request.SessionGeneration, ResultStatus,
                Array.Empty<EditorOutputRecord>(), request.WorkingDirectory, null,
                ResultStatus == EditorExecutionStatus.Unknown ? "simulated unknown readiness" : null, now, now, BackendId);
        }
    }
}

internal sealed class RecordingLiveConsoleService : ILiveConsoleService
{
    public int? ProcessId { get; private set; }
    public int CurrentSessionGeneration { get; private set; }
    public bool IsSessionRunning { get; set; }
    public bool? InputWritableOverride { get; set; }
    public bool IsInputWritable => InputWritableOverride ?? IsSessionRunning;
    public bool IsCommandInProgress { get; set; }
    public bool IsHostAttached => true;
    public PowerShellRuntimeInfo? ActiveRuntime { get; private set; }
    public string? CurrentWorkingDirectory => null;
    public List<string> Operations { get; } = new();
    public List<string> RawInputs { get; } = new();
    public List<string> CommandTexts { get; } = new();
    public List<Guid> CommandRequestIds { get; } = new();
    public Exception? RawInputException { get; set; }
    public Action? BeforeRawInputWrite { get; set; }
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
    public Action<RecordingLiveConsoleService, int>? SessionStartCallback { get; set; }

    public event Action? ScriptExecutionCompleted;
    public event Action<int>? CommandExecutionCompleted;
    public event Action<LiveConsoleCommandCompletion>? InteractiveCommandCompleted;
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
        BeforeRawInputWrite?.Invoke();
        if (RawInputException is not null) throw RawInputException;
        RawInputs.Add(data);
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

        CurrentSessionGeneration++;
        ActiveRuntime = runtime;
        IsSessionRunning = true;
        ProcessId = 4321 + CurrentSessionGeneration;
        TerminalSessionStarted?.Invoke(CurrentSessionGeneration);
        SessionStartCallback?.Invoke(this, CurrentSessionGeneration);
        StartObserved.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(
        string commandText,
        Action<ExecutionOutputRecord> onOutput,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(
        string commandText,
        Action<ExecutionOutputRecord> onOutput,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        BeforeRawInputWrite?.Invoke();
        if (RawInputException is not null) throw RawInputException;
        CommandTexts.Add(commandText);
        CommandRequestIds.Add(requestId);
        IsCommandInProgress = true;
        var now = DateTime.UtcNow;
        return Task.FromResult(new LiveConsoleCommandResult(
            "test direct command", false, CurrentWorkingDirectory, now, now,
            requestId, CurrentSessionGeneration, ProcessId, LiveConsoleCommandOutcome.Submitted));
    }

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

        TerminalSessionStopping?.Invoke(CurrentSessionGeneration);
        IsCommandInProgress = false;
        IsSessionRunning = false;
        ActiveRuntime = null;
        ProcessId = null;
        return Task.FromResult(true);
    }

    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Operations.Add("shutdown");
        TerminalSessionStopping?.Invoke(CurrentSessionGeneration);
        IsCommandInProgress = false;
        IsSessionRunning = false;
        ActiveRuntime = null;
        ProcessId = null;
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
    public void RaiseCommandCompleted() => RaiseCommandCompleted(CurrentSessionGeneration);
    public void RaiseCommandCompleted(int generation) => CommandExecutionCompleted?.Invoke(generation);
    public void RaiseInteractiveCommandCompleted(Guid requestId, LiveConsoleCommandOutcome outcome = LiveConsoleCommandOutcome.PromptObserved)
    {
        IsCommandInProgress = false;
        if (ProcessId is { } processId)
            InteractiveCommandCompleted?.Invoke(new LiveConsoleCommandCompletion(requestId, CurrentSessionGeneration, processId, outcome, 1, DateTimeOffset.UtcNow));
    }
    public void RaiseSessionTerminated() => SessionTerminated?.Invoke();
    public void RaiseSessionStarted(int generation)
    {
        CurrentSessionGeneration = generation;
        TerminalSessionStarted?.Invoke(generation);
    }
    public void SetCurrentGeneration(int generation) => CurrentSessionGeneration = generation;
    public void PrimeSession(int generation)
    {
        CurrentSessionGeneration = generation;
        IsSessionRunning = true;
        ProcessId = 4321 + generation;
        TerminalSessionStarted?.Invoke(generation);
    }
    public void ClearProcessIdentity() => ProcessId = null;
    public void RaiseRawOutput(string text) => RawOutputReceived?.Invoke(1, text);

    public void RaisePromptReady(int generation, string path) => PromptReadyObserved?.Invoke(generation, path);
}
