using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class BaAuthoritativeReliabilityTests
{
    [Fact(Timeout = 180_000)]
    public async Task ProductionDapStopAndRestartPreservesTheAuthoritativeHost()
    {
        var runtime = FindRuntime();
        var root = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (runtime is null || string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BA production debugger recovery requires the recovered PSES root and PowerShell 7.");

        var script = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-BA-Debug-{Guid.NewGuid():N}.ps1");
        var output = new ConcurrentQueue<string>();
        await File.WriteAllTextAsync(script, "$global:BADebugState='break'\nWrite-Output ('BA_DEBUG_BREAK|' + $global:BADebugState)\n$global:BADebugState='middle'\n$global:BADebugState='after'\n");
        using var service = new PsesOwnedInteractiveSessionService();
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), Path.GetDirectoryName(script)).WaitAsync(TimeSpan.FromSeconds(45));
            var generation = service.SessionGeneration;
            using (var first = new PsesDapDebugSession(service))
            {
                await first.StartAsync(runtime, script, [new DebugBreakpointInfo(script, 2)]);
                await WaitForAsync(() => first.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
                Assert.True(await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            }
            Assert.True(service.IsSessionRunning);
            await service.WriteRawInputAsync("Write-Output 'BA_DEBUG_RECOVERED_1'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "BA_DEBUG_RECOVERED_1", TimeSpan.FromSeconds(15));

            using (var second = new PsesDapDebugSession(service))
            {
                await second.StartAsync(runtime, script, [new DebugBreakpointInfo(script, 2)]);
                await WaitForAsync(() => second.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
                await second.StepOverAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await WaitForAsync(() => second.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(15));
                await second.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await WaitForAsync(() => second.CurrentState == DebugSessionState.Stopped, TimeSpan.FromSeconds(30));
            }
            Assert.True(service.IsSessionRunning);
            Assert.Equal(generation, service.SessionGeneration);
            await service.WriteRawInputAsync("Write-Output 'BA_DEBUG_RECOVERED_2'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "BA_DEBUG_RECOVERED_2", TimeSpan.FromSeconds(15));
        }
        finally
        {
            try { await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            try { File.Delete(script); } catch { }
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task StructuredCancellationStopsObservableSideEffectsAndPreservesTheAuthoritativeSession()
    {
        var runtime = FindRuntime();
        var root = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (runtime is null || string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BA production cancellation proof requires the recovered PSES root and PowerShell 7.");

        var markerPath = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-BA-{Guid.NewGuid():N}.txt");
        var output = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService();
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), Path.GetDirectoryName(markerPath)).WaitAsync(TimeSpan.FromSeconds(45));
            var generation = service.SessionGeneration;
            var processId = service.ProcessId;
            var startTime = service.ProcessStartTimeUtc;
            Assert.True(service.IsSessionRunning);

            await service.WriteRawInputAsync("$global:BAState='terminal'; Write-Output ('BA_ID|'+$PID+'|'+[runspace]::DefaultRunspace.InstanceId)\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForIdentityAsync(output, "BA_ID|", TimeSpan.FromSeconds(15));
            var terminalIdentity = ExtractIdentity(output, "BA_ID|");

            var structured = await service.ExecuteStructuredScriptAsync(
                Guid.NewGuid(), generation, "BA identity", "$global:BAState='structured'; Write-Output ('BA_STRUCT_ID|'+$PID+'|'+[runspace]::DefaultRunspace.InstanceId)",
                executeInCurrentScope: true, record => output.Enqueue(record.Text)).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(EditorExecutionStatus.Completed, structured.Status);
            await service.WriteRawInputAsync("Write-Output ('BA_STRUCT_ID|' + $PID + '|' + [runspace]::DefaultRunspace.InstanceId)\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForIdentityAsync(output, "BA_STRUCT_ID|", TimeSpan.FromSeconds(15));
            var structuredIdentity = ExtractIdentity(output, "BA_STRUCT_ID|");
            Assert.Equal(terminalIdentity.ProcessId, structuredIdentity.ProcessId);
            Assert.Equal(terminalIdentity.RunspaceId, structuredIdentity.RunspaceId);

            await service.WriteRawInputAsync("Write-Output ('BA_STATE|' + $global:BAState)\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "BA_STATE|structured", TimeSpan.FromSeconds(15));

            await File.WriteAllTextAsync(markerPath, string.Empty);
            using var cancellation = new CancellationTokenSource();
            var request = service.ExecuteStructuredScriptAsync(
                Guid.NewGuid(), generation, "BA cancellation",
                $"1..100 | ForEach-Object {{ Add-Content -LiteralPath '{Quote(markerPath)}' -Value $_; Start-Sleep -Milliseconds 100 }}",
                executeInCurrentScope: true, record => output.Enqueue(record.Text), cancellation.Token);
            await WaitForAsync(() => CountLines(markerPath) >= 3, TimeSpan.FromSeconds(20));
            cancellation.Cancel();
            var canceled = await request.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(EditorExecutionStatus.Cancelled, canceled.Status);

            var countAtCancellation = CountLines(markerPath);
            await Task.Delay(1500);
            Assert.Equal(countAtCancellation, CountLines(markerPath));

            var recovery = $"BA_RECOVERED_{Guid.NewGuid():N}";
            await service.WriteRawInputAsync($"Write-Output '{recovery}'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, recovery, TimeSpan.FromSeconds(15));
            Assert.True(service.IsSessionRunning);
            Assert.Equal(generation, service.SessionGeneration);
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(startTime, service.ProcessStartTimeUtc);
        }
        finally
        {
            try { await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            try { File.Delete(markerPath); } catch { }
        }
    }

    private static async Task WaitForAsync(ConcurrentQueue<string> output, string expected, TimeSpan timeout)
        => await WaitForAsync(() => output.Any(item => item.Contains(expected, StringComparison.Ordinal)), timeout);

    private static async Task WaitForIdentityAsync(ConcurrentQueue<string> output, string marker, TimeSpan timeout)
    {
        var pattern = Regex.Escape(marker) + @"\d+\|[0-9a-fA-F-]{36}";
        await WaitForAsync(() => output.Any(item => Regex.IsMatch(item, pattern)), timeout);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "The expected authoritative-session observation was not reached before the timeout.");
    }

    private static int CountLines(string path)
    {
        try { return File.ReadLines(path).Count(); } catch (FileNotFoundException) { return 0; }
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static (int ProcessId, string RunspaceId) ExtractIdentity(ConcurrentQueue<string> output, string marker)
    {
        var pattern = Regex.Escape(marker) + @"(?<pid>\d+)\|(?<runspace>[0-9a-fA-F-]{36})";
        var match = output.SelectMany(item => Regex.Matches(item, pattern).Cast<Match>()).FirstOrDefault();
        Assert.True(match is not null, $"Identity marker {marker} was not observed in output: {string.Join("\\n", output.Take(12))}");
        Assert.True(int.TryParse(match!.Groups["pid"].Value, out var processId), $"Identity PID was malformed: {marker}");
        return (processId, match.Groups["runspace"].Value);
    }

    private static PowerShellRuntimeInfo? FindRuntime()
    {
        var path = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        return File.Exists(path)
            ? new PowerShellRuntimeInfo("BA PowerShell", "Core", "7.x", new Version(7, 0), "x64", path, "Phase2C-BA", true, false, true, true)
            : null;
    }
}
