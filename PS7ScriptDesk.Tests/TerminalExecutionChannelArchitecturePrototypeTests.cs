using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace PS7ScriptDesk.Tests;

/// <summary>
/// Isolated evidence for the execution-channel decision. This file deliberately
/// does not connect either prototype to the production terminal or the WPF UI.
/// </summary>
public sealed class TerminalExecutionChannelArchitecturePrototypeTests
{
    [Fact]
    public void CurrentInBandRun_WritesEditorControlThroughInteractiveInput()
    {
        var trace = InBandTerminalTrace.Create();

        trace.WriteInteractiveInput(". 'dispatch.ps1'\r");

        Assert.Equal(Encoding.UTF8.GetByteCount(". 'dispatch.ps1'\r"), trace.InteractiveInputBytes);
        Assert.Equal(1, trace.CarriageReturnsWritten);
        Assert.True(trace.InteractiveInputContainsEditorControl);
    }

    [Fact]
    public void CurrentInBandRun_ReturnedFramesTravelThroughTerminalOutput()
    {
        var trace = InBandTerminalTrace.Create();
        trace.WriteInteractiveInput(". 'dispatch.ps1'\r");
        trace.ReceiveTerminalOutput(
            "PS C:\\> . 'dispatch.ps1'\r\n" +
            "##PSSTUDIO_EXEC_START_abc\r\n" +
            "Get-Date\r\n" +
            "##PSSTUDIO_LOCATION_abc_QzpcV29yaw==\r\n" +
            "##PSSTUDIO_EXEC_DONE_abc\r\n" +
            "PS C:\\Work> ");

        Assert.True(trace.ProtocolFramesReceived >= 3);
        Assert.True(trace.LineFeedsReceived >= 5);
        Assert.True(trace.TerminalOutputBytes > 0);
    }

    [Fact]
    public void RendererFiltering_HidesTextButCannotUndoUpstreamLogicalRows()
    {
        var trace = InBandTerminalTrace.Create();
        trace.WriteInteractiveInput(". 'dispatch.ps1'\r");
        trace.ReceiveTerminalOutput(
            "PS C:\\> . 'dispatch.ps1'\r\n" +
            "##PSSTUDIO_EXEC_START_abc\r\n" +
            "Get-Date\r\n" +
            "##PSSTUDIO_EXEC_DONE_abc\r\n" +
            "PS C:\\> ");

        var visible = trace.FilterProtocolLines();

        Assert.DoesNotContain("EXEC_START", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("EXEC_DONE", visible, StringComparison.Ordinal);
        Assert.True(trace.UpstreamLogicalRows > trace.VisibleLogicalRows);
    }

    [Fact]
    public void CurrentProductionPath_StillCallsTerminalInputForEditorRun()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.PowerShell", "Services", "LiveConsoleService.cs"));

        Assert.Contains("WriteTerminalInputAsync(scriptCommand, sessionGeneration, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("BuildScriptDispatchCommand", source, StringComparison.Ordinal);
        Assert.Contains("##PSSTUDIO_EXEC_START_", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredControlChannel_SendsLifecycleEventsWithoutTerminalInput()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("Get-Date");

        Assert.Equal(0, host.InteractiveInputBytes);
        Assert.Equal(["ExecutionStarted", "ExecutionCompleted"], host.Events);
        Assert.NotEmpty(result.Output);
        Assert.Empty(result.ProtocolText);
    }

    [Fact]
    public async Task StructuredControlChannel_UsesCorrelationAndGenerationForCompletion()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("'structured-output'");

        Assert.NotEqual(Guid.Empty, result.CorrelationId);
        Assert.Equal(host.SessionGeneration, result.SessionGeneration);
        Assert.True(host.AcceptCompletion(result.CorrelationId, result.SessionGeneration));
        Assert.False(host.AcceptCompletion(result.CorrelationId, result.SessionGeneration - 1));
    }

    [Fact]
    public async Task SameSession_PersistsVariables()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        await host.ExecuteAsync("$prototypeValue = 123", executeInCurrentScope: true);

        var result = await host.ExecuteAsync("$prototypeValue");

        Assert.Contains("123", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_PersistsFunctions()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        await host.ExecuteAsync("function Get-PrototypeValue { 'function-persisted' }", executeInCurrentScope: true);

        var result = await host.ExecuteAsync("Get-PrototypeValue");

        Assert.Contains("function-persisted", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_PersistsImportedModules()
    {
        var moduleDirectory = CreateTempDirectory();
        try
        {
            var moduleName = "PrototypeModule" + Guid.NewGuid().ToString("N");
            var modulePath = Path.Combine(moduleDirectory, $"{moduleName}.psm1");
            File.WriteAllText(modulePath, "function Get-PrototypeModuleValue { 'module-persisted' }\nExport-ModuleMember -Function Get-PrototypeModuleValue");
            await using var host = await SameSessionStructuredHost.CreateAsync();
            await host.ExecuteAsync($"Import-Module {Quote(modulePath)}", executeInCurrentScope: true);

            var result = await host.ExecuteAsync("Get-PrototypeModuleValue");

            Assert.True(result.Output.Contains("module-persisted", StringComparison.Ordinal), result.AllText);
        }
        finally
        {
            TryDeleteDirectory(moduleDirectory);
        }
    }

    [Fact]
    public async Task SameSession_PersistsLocation()
    {
        var directory = CreateTempDirectory();
        try
        {
            await using var host = await SameSessionStructuredHost.CreateAsync();
            await host.ExecuteAsync($"Set-Location {Quote(directory)}", executeInCurrentScope: true);

            var result = await host.ExecuteAsync("(Get-Location).ProviderPath");

            Assert.True(result.Output.Contains(directory, StringComparison.OrdinalIgnoreCase), result.AllText);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SameSession_PersistsEnvironmentChanges()
    {
        var name = "PS7SD_PROTOTYPE_" + Guid.NewGuid().ToString("N");
        await using var host = await SameSessionStructuredHost.CreateAsync();
        await host.ExecuteAsync($"$env:{name} = 'persisted'");

        var result = await host.ExecuteAsync($"$env:{name}");

        Assert.Contains("persisted", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_CurrentScopeRun_PersistsAssignment()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        await host.ExecuteAsync("$currentScopeValue = 'current-scope'", executeInCurrentScope: true);

        var result = await host.ExecuteAsync("$currentScopeValue", executeInCurrentScope: true);

        Assert.Contains("current-scope", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_DotSourcePreservesState()
    {
        var directory = CreateTempDirectory();
        var scriptPath = Path.Combine(directory, "dot-source.ps1");
        try
        {
            File.WriteAllText(scriptPath, "$dotSourcedValue = 'dot-sourced'");
            await using var host = await SameSessionStructuredHost.CreateAsync();
            await host.ExecuteAsync($". {Quote(scriptPath)}", executeInCurrentScope: true);

            var result = await host.ExecuteAsync("$dotSourcedValue", executeInCurrentScope: true);

            Assert.True(result.Output.Contains("dot-sourced", StringComparison.Ordinal), result.AllText);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SameSession_CallOperatorKeepsScriptLocalScope()
    {
        var directory = CreateTempDirectory();
        var scriptPath = Path.Combine(directory, "call.ps1");
        try
        {
            File.WriteAllText(scriptPath, "$callOnlyValue = 'local'");
            await using var host = await SameSessionStructuredHost.CreateAsync();
            await host.ExecuteAsync($"& {Quote(scriptPath)}");

            var result = await host.ExecuteAsync("if ($null -eq $callOnlyValue) { 'isolated' } else { 'leaked' }");

            Assert.Contains("isolated", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ScriptFileExecution_SetsPSScriptRootAndPSCommandPath()
    {
        var directory = CreateTempDirectory();
        var scriptPath = Path.Combine(directory, "identity.ps1");
        try
        {
            File.WriteAllText(scriptPath, "\"$PSScriptRoot|$PSCommandPath\"");
            await using var host = await SameSessionStructuredHost.CreateAsync();

            var result = await host.ExecutePathAsync(scriptPath);

            Assert.Contains(directory, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(scriptPath, result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ScriptFileExecution_ResolvesRelativePathsFromWorkingDirectory()
    {
        var directory = CreateTempDirectory();
        var childPath = Path.Combine(directory, "child.txt");
        var scriptPath = Path.Combine(directory, "relative.ps1");
        try
        {
            File.WriteAllText(childPath, "relative-content");
            File.WriteAllText(scriptPath, "Get-Content ./child.txt");
            await using var host = await SameSessionStructuredHost.CreateAsync();
            await host.ExecuteAsync($"Set-Location {Quote(directory)}", executeInCurrentScope: true);

            var result = await host.ExecutePathAsync(scriptPath);

            Assert.Contains("relative-content", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SameSession_PreservesPipelineOutputOrdering()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("'one'; 'two'; 'three'");

        Assert.Equal(["one", "two", "three"], result.OutputLines);
    }

    [Fact]
    public async Task SameSession_CapturesPowerShellStreamsSeparately()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("Write-Warning warning; Write-Verbose verbose -Verbose; Write-Information information -InformationAction Continue; Write-Debug debug -Debug");

        Assert.Contains("warning", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verbose", result.Verbose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("information", result.Information, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("debug", result.Debug, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameSession_PreservesAnsiAndUnicodePayloads()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("Write-Output ([string][char]27 + '[31mred' + [char]27 + '[0m 日本語')");

        Assert.Contains("\x1b[31mred\x1b[0m 日本語", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_HandlesCarriageReturnProgressPayload()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("Write-Output ('10%' + [char]13 + '20%' + [char]13 + '100%')");

        Assert.Contains("10%\r20%\r100%", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_HandlesLargeOutput()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("'x' * 10000");

        Assert.Equal(10000, result.OutputLines.Single().Length);
    }

    [Fact]
    public async Task SameSession_ReportsTerminatingErrorsWithoutProtocolText()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteAsync("throw 'terminating-prototype-error'");

        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain("EXEC_START", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("EXEC_DONE", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSession_InterruptionStopsLongRunningInvocation()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        var result = await host.ExecuteInterruptibleAsync("while ($true) { Start-Sleep -Milliseconds 25 }");

        Assert.True(result.WasStopped);
        Assert.True(result.InvocationState is PSInvocationState.Stopped or PSInvocationState.Failed);
    }

    [Fact]
    public async Task StructuredControlChannel_CancellationStopsQueuedRequest()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ExecuteAsync("'cancelled'", cancellationToken: cancellation.Token));
        Assert.Empty(host.Events);
        Assert.Equal(0, host.InteractiveInputBytes);
    }

    [Fact]
    public async Task StructuredControlChannel_RejectsStaleCompletion()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        var result = await host.ExecuteAsync("'stale-check'");

        Assert.False(host.AcceptCompletion(result.CorrelationId, result.SessionGeneration - 1));
        Assert.False(host.AcceptCompletion(Guid.NewGuid(), result.SessionGeneration));
    }

    [Fact]
    public async Task StructuredControlChannel_HostShutdownRejectsNewWork()
    {
        var host = await SameSessionStructuredHost.CreateAsync();
        await host.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ExecuteAsync("'after-shutdown'"));
    }

    [Fact]
    public async Task StructuredControlChannel_BackendExitCompletesAsFailure()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        host.SimulateBackendExit();

        var result = await host.ExecuteAsync("'backend-exit'");

        Assert.True(result.Failed);
        Assert.Contains("backend", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StructuredControlChannel_DisconnectDoesNotWriteTerminalInput()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();
        host.DisconnectControlChannel();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync("'disconnected'"));
        Assert.Equal(0, host.InteractiveInputBytes);
    }

    [Fact]
    public async Task SameSession_SupportsMultipleSequentialExecutions()
    {
        await using var host = await SameSessionStructuredHost.CreateAsync();

        for (var index = 0; index < 5; index++)
        {
            var result = await host.ExecuteAsync($"'run-{index}'");
            Assert.Contains($"run-{index}", result.Output, StringComparison.Ordinal);
        }

        Assert.Equal(10, host.Events.Count);
        Assert.Equal(0, host.InteractiveInputBytes);
    }

    [Fact]
    public async Task ExecutionTargetProbe_DirtyEditorTextUsesSnapshot()
    {
        var target = ExecutionTargetProbe.Select("C:\\work\\saved.ps1", "new", "old");

        Assert.True(target.IsSnapshot);
        Assert.NotEqual("C:\\work\\saved.ps1", target.Path);
    }

    [Fact]
    public void ExecutionTargetProbe_SavedEditorTextUsesRealPath()
    {
        var target = ExecutionTargetProbe.Select("C:\\work\\saved.ps1", "same", "same");

        Assert.False(target.IsSnapshot);
        Assert.Equal("C:\\work\\saved.ps1", target.Path);
    }

    [Fact]
    public async Task DedicatedHost_DoesNotSharePersistentVariables()
    {
        await using var host = await DedicatedExecutionHost.CreateAsync();
        await host.ExecuteAsync("$dedicatedValue = 'private'");

        var result = await host.ExecuteAsync("if ($null -eq $dedicatedValue) { 'isolated' } else { 'leaked' }");

        Assert.Contains("isolated", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DedicatedHost_DoesNotShareFunctionsOrLocation()
    {
        await using var host = await DedicatedExecutionHost.CreateAsync();
        var privateDirectory = CreateTempDirectory();
        try
        {
            await host.ExecuteAsync($"function Get-DedicatedValue {{ 'private' }}; Set-Location {Quote(privateDirectory)}");

            var result = await host.ExecuteAsync("@((Get-Command Get-DedicatedValue -ErrorAction SilentlyContinue), (Get-Location).Path) | ForEach-Object { $_.ToString() }");

            Assert.DoesNotContain("Get-DedicatedValue", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(privateDirectory, result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(privateDirectory);
        }
    }

    [Fact]
    public async Task DedicatedHost_StillProvidesScriptFileIdentity()
    {
        var directory = CreateTempDirectory();
        var scriptPath = Path.Combine(directory, "dedicated.ps1");
        try
        {
            File.WriteAllText(scriptPath, "\"$PSScriptRoot|$PSCommandPath\"");
            await using var host = await DedicatedExecutionHost.CreateAsync();

            var result = await host.ExecuteAsync(scriptPath, isPath: true);

            Assert.True(result.Output.Contains(directory, StringComparison.OrdinalIgnoreCase), result.AllText);
            Assert.Contains(scriptPath, result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DedicatedHost_UsesSeparateLifecycleAndNoTerminalProtocol()
    {
        await using var host = await DedicatedExecutionHost.CreateAsync();

        var result = await host.ExecuteAsync("'dedicated-output'");

        Assert.Equal(0, host.InteractiveInputBytes);
        Assert.Contains("dedicated-output", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("PSSTUDIO", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PS7ScriptDesk.ExecutionPrototype." + Guid.NewGuid().ToString("N"))).FullName;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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

internal sealed class SameSessionStructuredHost : IAsyncDisposable
{
    private readonly Runspace _runspace;
    private readonly Channel<ExecutionControlRequest> _requests = Channel.CreateUnbounded<ExecutionControlRequest>();
    private readonly ConcurrentDictionary<Guid, int> _activeRequests = new();
    private readonly ConcurrentDictionary<Guid, int> _completedRequests = new();
    private bool _disposed;
    private bool _backendExited;
    private bool _controlDisconnected;

    private SameSessionStructuredHost(Runspace runspace)
    {
        _runspace = runspace;
        SessionGeneration = 1;
    }

    public List<string> Events { get; } = [];
    public int InteractiveInputBytes { get; private set; }
    public int SessionGeneration { get; }

    public static Task<SameSessionStructuredHost> CreateAsync()
    {
        var initialSessionState = InitialSessionState.CreateDefault2();
        initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(initialSessionState);
        runspace.Open();
        return Task.FromResult(new SameSessionStructuredHost(runspace));
    }

    public async Task<PrototypeResult> ExecuteAsync(string script, bool executeInCurrentScope = false, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        if (_controlDisconnected)
        {
            throw new InvalidOperationException("The structured execution channel is disconnected.");
        }

        var request = new ExecutionControlRequest(Guid.NewGuid(), SessionGeneration, script, executeInCurrentScope);
        await _requests.Writer.WriteAsync(request, cancellationToken);
        var accepted = await _requests.Reader.ReadAsync(cancellationToken);
        _activeRequests[accepted.CorrelationId] = accepted.SessionGeneration;
        Events.Add("ExecutionStarted");
        try
        {
            if (_backendExited)
            {
                return PrototypeResult.Failure(accepted.CorrelationId, accepted.SessionGeneration, "backend process exited before execution");
            }

            var result = PowerShellRunspaceProbe.Invoke(_runspace, accepted.Script, accepted.ExecuteInCurrentScope);
            Events.Add("ExecutionCompleted");
            _completedRequests[accepted.CorrelationId] = accepted.SessionGeneration;
            return result with { CorrelationId = accepted.CorrelationId, SessionGeneration = accepted.SessionGeneration };
        }
        finally
        {
            _activeRequests.TryRemove(accepted.CorrelationId, out _);
        }
    }

    public Task<PrototypeResult> ExecutePathAsync(string path, CancellationToken cancellationToken = default) => ExecuteAsync("& " + Quote(path), cancellationToken: cancellationToken);

    public async Task<PrototypeResult> ExecuteInterruptibleAsync(string script)
    {
        ThrowIfUnavailable();
        var correlationId = Guid.NewGuid();
        var powerShell = System.Management.Automation.PowerShell.Create();
        powerShell.Runspace = _runspace;
        powerShell.AddScript(script, useLocalScope: false);
        var invocation = powerShell.BeginInvoke();
        await Task.Delay(100);
        powerShell.Stop();
        try
        {
            powerShell.EndInvoke(invocation);
        }
        catch (PipelineStoppedException)
        {
        }
        finally
        {
            powerShell.Dispose();
        }

        return PrototypeResult.Stopped(correlationId, SessionGeneration);
    }

    public bool AcceptCompletion(Guid correlationId, int sessionGeneration) =>
        sessionGeneration == SessionGeneration &&
        (_activeRequests.ContainsKey(correlationId) || _completedRequests.TryGetValue(correlationId, out var completedGeneration) && completedGeneration == sessionGeneration);

    public void SimulateBackendExit() => _backendExited = true;

    public void DisconnectControlChannel() => _controlDisconnected = true;

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _requests.Writer.TryComplete();
            _runspace.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SameSessionStructuredHost));
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed class DedicatedExecutionHost : IAsyncDisposable
{
    private bool _disposed;

    public List<string> Events { get; } = [];
    public int InteractiveInputBytes { get; private set; }

    public static Task<DedicatedExecutionHost> CreateAsync() => Task.FromResult(new DedicatedExecutionHost());

    public async Task<PrototypeResult> ExecuteAsync(string scriptOrPath, bool isPath = false, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DedicatedExecutionHost));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Events.Add("ExecutionStarted");
        var initialSessionState = InitialSessionState.CreateDefault2();
        initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        using var runspace = RunspaceFactory.CreateRunspace(initialSessionState);
        runspace.Open();
        var result = PowerShellRunspaceProbe.Invoke(runspace, isPath ? "& " + Quote(scriptOrPath) : scriptOrPath, executeInCurrentScope: false);
        Events.Add("ExecutionCompleted");
        await Task.CompletedTask;
        return result;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed record ExecutionControlRequest(Guid CorrelationId, int SessionGeneration, string Script, bool ExecuteInCurrentScope);

internal sealed record PrototypeResult(
    Guid CorrelationId,
    int SessionGeneration,
    IReadOnlyList<string> OutputLines,
    string Warning,
    string Verbose,
    string Information,
    string Debug,
    string Errors,
    bool WasStopped,
    bool Failed,
    string FailureMessage,
    PSInvocationState InvocationState)
{
    public string Output => string.Join(Environment.NewLine, OutputLines);
    public string AllText => string.Join("\n", Output, Warning, Verbose, Information, Debug, Errors, FailureMessage);
    public IReadOnlyList<string> ProtocolText { get; } = [];

    public static PrototypeResult Failure(Guid correlationId, int sessionGeneration, string message) =>
        new(correlationId, sessionGeneration, [], string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, true, message, PSInvocationState.Failed);

    public static PrototypeResult Stopped(Guid correlationId, int sessionGeneration) =>
        new(correlationId, sessionGeneration, [], string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, true, false, string.Empty, PSInvocationState.Stopped);
}

internal static class PowerShellRunspaceProbe
{
    public static PrototypeResult Invoke(Runspace runspace, string script, bool executeInCurrentScope)
    {
        using var powerShell = System.Management.Automation.PowerShell.Create();
        powerShell.Runspace = runspace;
        powerShell.AddScript(script, useLocalScope: !executeInCurrentScope);
        Collection<PSObject> output;
        string invocationError = string.Empty;
        try
        {
            output = powerShell.Invoke();
        }
        catch (RuntimeException ex)
        {
            output = [];
            invocationError = ex.Message;
        }

        var outputLines = output.Select(item => item?.BaseObject?.ToString() ?? string.Empty).ToArray();
        return new PrototypeResult(
            Guid.Empty,
            0,
            outputLines,
            string.Join(Environment.NewLine, powerShell.Streams.Warning.Select(item => item.Message)),
            string.Join(Environment.NewLine, powerShell.Streams.Verbose.Select(item => item.Message)),
            string.Join(Environment.NewLine, powerShell.Streams.Information.Select(item => item.MessageData?.ToString() ?? string.Empty)),
            string.Join(Environment.NewLine, powerShell.Streams.Debug.Select(item => item.Message)),
            string.Join(Environment.NewLine, powerShell.Streams.Error.Select(item => item.ToString()).Append(invocationError).Where(message => !string.IsNullOrWhiteSpace(message))),
            false,
            powerShell.HadErrors,
            powerShell.HadErrors ? "PowerShell invocation reported errors" : string.Empty,
            powerShell.InvocationStateInfo.State);
    }
}

internal sealed class InBandTerminalTrace
{
    private readonly StringBuilder _terminalOutput = new();
    private readonly StringBuilder _interactiveInput = new();

    public int InteractiveInputBytes { get; private set; }
    public int CarriageReturnsWritten { get; private set; }
    public int LineFeedsReceived { get; private set; }
    public int ProtocolFramesReceived { get; private set; }
    public int TerminalOutputBytes { get; private set; }
    public int UpstreamLogicalRows { get; private set; }
    public int VisibleLogicalRows { get; private set; }
    public bool InteractiveInputContainsEditorControl => _interactiveInput.Length > 0;

    public static InBandTerminalTrace Create() => new();

    public void WriteInteractiveInput(string data)
    {
        _interactiveInput.Append(data);
        InteractiveInputBytes += Encoding.UTF8.GetByteCount(data);
        CarriageReturnsWritten += data.Count(character => character == '\r');
    }

    public void ReceiveTerminalOutput(string data)
    {
        _terminalOutput.Append(data);
        TerminalOutputBytes += Encoding.UTF8.GetByteCount(data);
        LineFeedsReceived += data.Count(character => character == '\n');
        UpstreamLogicalRows += data.Count(character => character == '\n');
        ProtocolFramesReceived += data.Split('\n').Count(line => line.Contains("##PSSTUDIO_", StringComparison.Ordinal));
    }

    public string FilterProtocolLines()
    {
        var visible = string.Join(
            "\n",
            _terminalOutput
                .ToString()
                .Split('\n')
                .Where(line => !line.Contains("##PSSTUDIO_", StringComparison.Ordinal)));
        VisibleLogicalRows = visible.Count(character => character == '\n');
        return visible;
    }
}

internal readonly record struct ExecutionTargetProbeResult(string Path, bool IsSnapshot);

internal static class ExecutionTargetProbe
{
    public static ExecutionTargetProbeResult Select(string displayName, string editorText, string savedText)
    {
        if (Path.GetExtension(displayName).Equals(".ps1", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(editorText, savedText, StringComparison.Ordinal))
        {
            return new(displayName, IsSnapshot: false);
        }

        return new(Path.Combine(Path.GetTempPath(), "PS7ScriptDesk.Prototype." + Guid.NewGuid().ToString("N") + ".ps1"), IsSnapshot: true);
    }
}
