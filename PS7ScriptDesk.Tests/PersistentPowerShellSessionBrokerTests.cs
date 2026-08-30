using System.Management.Automation;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PersistentPowerShellSessionBrokerTests
{
    [Fact]
    public async Task Startup_ProducesReadySnapshotWithGeneration()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync("prototype-runtime");

        Assert.Equal(1, broker.Snapshot.SessionGeneration);
        Assert.Equal(PersistentSessionLifecycle.Ready, broker.Snapshot.Lifecycle);
        Assert.Equal("prototype-runtime", broker.Snapshot.RuntimeIdentity);
        Assert.False(broker.Snapshot.IsExecutionRunning);
    }

    [Fact]
    public async Task StructuredRun_GetDateHasNoInteractiveTerminalDependency()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var events = new List<EditorExecutionEvent>();
        broker.EventPublished += events.Add;

        var result = await broker.ExecuteAsync(Request("Get-Date", EditorExecutionMode.ScriptCall));

        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.Outputs);
        Assert.Contains(events, item => item.Kind == EditorExecutionEventKind.Started);
        Assert.Contains(events, item => item.Kind == EditorExecutionEventKind.Completed);
        Assert.DoesNotContain(events, item => item.Output?.Payload.Contains("PSSTUDIO", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task CurrentScope_VariablePersistsAcrossRuns()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        await broker.ExecuteAsync(Request("$wave1Value = 123", EditorExecutionMode.CurrentScope));

        var result = await broker.ExecuteAsync(Request("$wave1Value", EditorExecutionMode.CurrentScope));

        Assert.Contains("123", Output(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentScope_FunctionPersistsAcrossRuns()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        await broker.ExecuteAsync(Request("function Get-Wave1Value { 'persisted-function' }", EditorExecutionMode.CurrentScope));

        var result = await broker.ExecuteAsync(Request("Get-Wave1Value", EditorExecutionMode.CurrentScope));

        Assert.Contains("persisted-function", Output(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentScope_ImportedModulePersistsAcrossRuns()
    {
        var directory = CreateTempDirectory();
        try
        {
            var modulePath = Path.Combine(directory, "Wave1Module.psm1");
            File.WriteAllText(modulePath, "function Get-Wave1ModuleValue { 'persisted-module' }\nExport-ModuleMember -Function Get-Wave1ModuleValue");
            await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
            await broker.ExecuteAsync(Request($"Import-Module {Quote(modulePath)}", EditorExecutionMode.CurrentScope));

            var result = await broker.ExecuteAsync(Request("Get-Wave1ModuleValue", EditorExecutionMode.CurrentScope));

            Assert.Contains("persisted-module", Output(result), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CurrentScope_SetLocationProducesTypedWorkingDirectoryEvent()
    {
        var directory = CreateTempDirectory();
        try
        {
            await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
            var events = new List<EditorExecutionEvent>();
            broker.EventPublished += events.Add;

            var result = await broker.ExecuteAsync(Request($"Set-Location {Quote(directory)}", EditorExecutionMode.CurrentScope));

            Assert.Equal(directory, result.CurrentWorkingDirectory, ignoreCase: true);
            Assert.Contains(events, item => item.Kind == EditorExecutionEventKind.WorkingDirectoryChanged && string.Equals(item.WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(events, item => item.Output?.Payload.Contains("LOCATION_", StringComparison.Ordinal) == true);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CurrentScope_EnvironmentChangePersistsAcrossRuns()
    {
        var variable = "PS7SD_WAVE1_" + Guid.NewGuid().ToString("N");
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        await broker.ExecuteAsync(Request($"$env:{variable} = 'persisted-environment'", EditorExecutionMode.CurrentScope));

        var result = await broker.ExecuteAsync(Request($"$env:{variable}", EditorExecutionMode.CurrentScope));

        Assert.Contains("persisted-environment", Output(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptCall_DoesNotLeakScriptLocalVariables()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        await broker.ExecuteAsync(Request("$scriptLocalValue = 'private'", EditorExecutionMode.ScriptCall));

        var result = await broker.ExecuteAsync(Request("if ($null -eq $scriptLocalValue) { 'isolated' } else { 'leaked' }", EditorExecutionMode.ScriptCall));

        Assert.Contains("isolated", Output(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DotSourceAndCallOperatorRemainDistinct()
    {
        var directory = CreateTempDirectory();
        try
        {
            var scriptPath = Path.Combine(directory, "scope.ps1");
            File.WriteAllText(scriptPath, "$scopeValue = 'dot-sourced'");
            await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
            await broker.ExecuteAsync(Request(string.Empty, EditorExecutionMode.CurrentScope, scriptPath, isSavedClean: true));
            var dotSourced = await broker.ExecuteAsync(Request("$scopeValue", EditorExecutionMode.CurrentScope));

            Assert.Contains("dot-sourced", Output(dotSourced), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SavedCleanArtifactUsesRealPathAndScriptIdentity()
    {
        var directory = CreateTempDirectory();
        var scriptPath = Path.Combine(directory, "saved.ps1");
        try
        {
            File.WriteAllText(scriptPath, "\"$PSScriptRoot|$PSCommandPath\"");
            await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
            var result = await broker.ExecuteAsync(Request(File.ReadAllText(scriptPath), EditorExecutionMode.ScriptCall, scriptPath, isSavedClean: true));

            Assert.False(result.Artifact!.IsSnapshot);
            Assert.Equal(scriptPath, result.Artifact.ExecutionPath);
            Assert.Contains(directory, Output(result), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(scriptPath, Output(result), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DirtyExecutionUsesOwnedSnapshotAndCleansItAfterCompletion()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();

        var result = await broker.ExecuteAsync(Request("'dirty-snapshot'", EditorExecutionMode.RunSelection));

        Assert.True(result.Artifact!.IsSnapshot);
        Assert.True(result.Artifact.DeleteAfterRun);
        Assert.False(File.Exists(result.Artifact.ExecutionPath));
    }

    [Fact]
    public async Task OutputStreamsAreTypedAndSequenced()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var result = await broker.ExecuteAsync(Request("Write-Output output; Write-Warning warning; Write-Verbose verbose -Verbose; Write-Information information -InformationAction Continue; Write-Debug debug -Debug; Write-Error error"));

        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Success && item.Payload == "output");
        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Warning);
        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Verbose);
        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Information);
        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Debug);
        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Error);
        Assert.Equal(result.Outputs.Select(item => item.Sequence).OrderBy(item => item), result.Outputs.Select(item => item.Sequence));
    }

    [Fact]
    public async Task OutputPreservesAnsiUnicodeCarriageReturnAndLargePayload()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var result = await broker.ExecuteAsync(Request("[char]27 + '[31mred' + [char]27 + '[0m 日本語'; '10%' + [char]13 + '20%'; 'x' * 10000"));
        var output = Output(result);

        Assert.Contains("\x1b[31mred\x1b[0m 日本語", output, StringComparison.Ordinal);
        Assert.Contains("10%\r20%", output, StringComparison.Ordinal);
        Assert.Contains(new string('x', 10000), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminatingErrorIsStructuredFailure()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();

        var result = await broker.ExecuteAsync(Request("throw 'wave1-terminating-error'"));

        Assert.Equal(EditorExecutionStatus.Failed, result.Status);
        Assert.Contains("wave1-terminating-error", string.Join("\n", result.Outputs.Select(item => item.Payload)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonTerminatingErrorRemainsTypedOutput()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();

        var result = await broker.ExecuteAsync(Request("Write-Error 'wave1-nonterminating-error'; 'after-error'"));

        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.Contains(result.Outputs, item => item.StreamKind == EditorOutputStreamKind.Error && item.Payload.Contains("wave1-nonterminating-error", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("after-error", Output(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionCompletesCancelledWithoutExecution()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await broker.ExecuteAsync(Request("$shouldNotRun = 1", EditorExecutionMode.CurrentScope), cancellation.Token);

        Assert.Equal(EditorExecutionStatus.Cancelled, result.Status);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task CancellationDuringExecutionStopsRunspaceInvocation()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var execution = broker.ExecuteAsync(Request("while ($true) { Start-Sleep -Milliseconds 25 }", EditorExecutionMode.CurrentScope), cancellation.Token);
        await WaitUntilAsync(() => broker.Snapshot.IsExecutionRunning);
        cancellation.Cancel();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(EditorExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task OnlyOneRunspaceExecutionIsAdmitted()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var first = broker.ExecuteAsync(Request("Start-Sleep -Milliseconds 100; 'first'", EditorExecutionMode.CurrentScope));
        await WaitUntilAsync(() => broker.Snapshot.IsExecutionRunning);
        var second = broker.ExecuteAsync(Request("'second'", EditorExecutionMode.CurrentScope));

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(EditorExecutionStatus.Completed, result.Status));
        Assert.Equal(2, results.Length);
    }

    [Fact]
    public async Task RestartRejectsStaleGenerationAndAcceptsNewGeneration()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var oldGeneration = broker.Snapshot.SessionGeneration;
        await broker.RestartAsync();

        var stale = await broker.ExecuteAsync(Request("'stale'", EditorExecutionMode.ScriptCall, sessionGeneration: oldGeneration));
        var current = await broker.ExecuteAsync(Request("'current'", sessionGeneration: broker.Snapshot.SessionGeneration));

        Assert.Equal(EditorExecutionStatus.Rejected, stale.Status);
        Assert.Equal(EditorExecutionStatus.Completed, current.Status);
        Assert.Equal(oldGeneration + 1, current.SessionGeneration);
    }

    [Fact]
    public async Task ShutdownWhileRunningStopsWorkAndRejectsLaterRequests()
    {
        var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var running = broker.ExecuteAsync(Request("while ($true) { Start-Sleep -Milliseconds 25 }", EditorExecutionMode.CurrentScope));
        await WaitUntilAsync(() => broker.Snapshot.IsExecutionRunning);
        await broker.ShutdownAsync();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        var later = await broker.ExecuteAsync(Request("'after-shutdown'"));

        Assert.Equal(EditorExecutionStatus.Cancelled, result.Status);
        Assert.Equal(EditorExecutionStatus.Rejected, later.Status);
        Assert.Equal(PersistentSessionLifecycle.Disposed, broker.Snapshot.Lifecycle);
    }

    [Fact]
    public async Task RepeatedShutdownAndDisposeAreIdempotent()
    {
        var broker = await PersistentPowerShellSessionBroker.CreateAsync();

        await broker.ShutdownAsync();
        await broker.ShutdownAsync();
        await broker.DisposeAsync();
        await broker.DisposeAsync();

        Assert.Equal(PersistentSessionLifecycle.Disposed, broker.Snapshot.Lifecycle);
    }

    [Fact]
    public async Task EventSubscriberFailureDoesNotBreakExecution()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        broker.EventPublished += _ => throw new InvalidOperationException("subscriber failure");

        var result = await broker.ExecuteAsync(Request("'still-runs'"));

        Assert.True(result.Succeeded);
        Assert.Contains("still-runs", Output(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeatureGateDisabledDoesNotFallbackToLegacyTerminalInjection()
    {
        await using var broker = await PersistentPowerShellSessionBroker.CreateAsync();
        var adapter = new StructuredEditorExecutionAdapter(broker, new EditorExecutionFeatureGate(structuredExecutionEnabled: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(Request("'disabled'")));

        Assert.Contains("No legacy terminal fallback", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveAdmissionPolicyDoesNotUseRendererCursorState()
    {
        var coordinator = new InteractiveTerminalCoordinator();

        coordinator.SetState(InteractiveTerminalState.InteractiveIdleAtPrompt);
        Assert.True(coordinator.CanStartEditorExecution);
        coordinator.SetState(InteractiveTerminalState.InteractiveInputEditing);
        Assert.False(coordinator.CanStartEditorExecution);
        coordinator.SetState(InteractiveTerminalState.InteractiveCommandRunning);
        Assert.False(coordinator.CanStartEditorExecution);
    }

    [Fact]
    public void OutputMultiplexerSerializesInteractiveAndEditorOutput()
    {
        var multiplexer = new TerminalOutputMultiplexer();
        var requestId = Guid.NewGuid();
        multiplexer.SetInteractiveState(InteractiveTerminalState.InteractiveIdleAtPrompt);
        Assert.True(multiplexer.TryBeginEditorExecution(requestId, out _));

        var interactive = multiplexer.PublishInteractive(EditorOutputStreamKind.VirtualTerminal, "prompt");
        var editor = multiplexer.PublishEditor(new EditorOutputRecord(requestId, 1, 1, EditorOutputStreamKind.Success, "output", DateTimeOffset.UtcNow));

        Assert.True(interactive.Sequence < editor.Sequence);
        Assert.Equal(2, multiplexer.Published.Count);
        multiplexer.EndEditorExecution(requestId);
    }

    [Fact]
    public void StructuredProductionSourcesContainNoLegacyTerminalControlSymbols()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.PowerShell", "Services", "PersistentPowerShellSessionBroker.cs"));

        Assert.DoesNotContain("WriteTerminalInputAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildScriptDispatchCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EXEC_START", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EXEC_DONE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPATCH_DIAG", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalProtocolOutputFilter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalEnterSequence", source, StringComparison.Ordinal);
    }

    private static EditorExecutionRequest Request(
        string script,
        EditorExecutionMode mode = EditorExecutionMode.ScriptCall,
        string? savedScriptPath = null,
        bool isSavedClean = false,
        int sessionGeneration = 1) => new(
            Guid.NewGuid(),
            sessionGeneration,
            mode,
            "Wave 1 test.ps1",
            script,
            savedScriptPath,
            isSavedClean,
            null,
            mode == EditorExecutionMode.CurrentScope);

    private static string Output(EditorExecutionResult result) => string.Join(Environment.NewLine, result.Outputs.Select(item => item.Payload));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The broker did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string CreateTempDirectory() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PS7ScriptDesk.Broker." + Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
