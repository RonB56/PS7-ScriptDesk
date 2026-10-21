using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class AzPsesDapProductionIntegrationTests
{
    [Fact(Timeout = 180_000)]
    public async Task AuthoritativePsesSession_ConnectsDapHitsBreakpointInspectsAndReturnsToHost()
    {
        var runtime = FindRuntime();
        var root = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (runtime is null || string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("AZ production PSES integration requires PS7SCRIPTDESK_PSES_ROOT and PowerShell 7.");

        var script = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-AZ-{Guid.NewGuid():N}.ps1");
        var token = $"AZ_TOKEN_{Guid.NewGuid():N}";
        var evidencePath = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_AZ_EVIDENCE_PATH");
        var sessionPid = 0;
        DateTimeOffset? sessionStart = null;
        var sessionGeneration = 0;
        var debugServicePipePublished = false;
        var debuggerSessionId = Guid.Empty;
        var paused = false;
        var frameCount = 0;
        var variableCount = 0;
        var stepOverObserved = false;
        var completed = false;
        await File.WriteAllTextAsync(script, "$global:AZState = '" + token + "'\nWrite-Output ('AZ_BREAK|' + $global:AZState)\nWrite-Output ('AZ_AFTER|' + $global:AZState)\n");

        using var service = new PsesOwnedInteractiveSessionService();
        var output = new ConcurrentQueue<string>();
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), Path.GetDirectoryName(script)).WaitAsync(TimeSpan.FromSeconds(45));
            Assert.True(service.IsSessionRunning);
            Assert.NotNull(service.ProcessId);
            Assert.NotNull(service.ProcessStartTimeUtc);
            sessionPid = service.ProcessId!.Value;
            sessionStart = service.ProcessStartTimeUtc;
            sessionGeneration = service.SessionGeneration;
            debugServicePipePublished = !string.IsNullOrWhiteSpace(service.DebugServicePipeName);

            using var debugger = new PsesDapDebugSession(service);
            debuggerSessionId = debugger.SessionId;
            await debugger.StartAsync(runtime, script, new[] { new DebugBreakpointInfo(script, 2) });
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
            paused = true;

            var frames = await debugger.GetCallStackAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEmpty(frames);
            frameCount = frames.Count;
            var variables = await debugger.GetVariablesAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEmpty(variables);
            variableCount = variables.Count;

            await debugger.StepOverAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(15));
            stepOverObserved = true;
            await debugger.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Stopped, TimeSpan.FromSeconds(30));
            await service.WriteRawInputAsync("Write-Output ('AZ_POST_DEBUG|' + $global:AZState)\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => output.Any(item => item.Contains("AZ_POST_DEBUG|", StringComparison.Ordinal)), TimeSpan.FromSeconds(15));
            completed = true;
        }
        finally
        {
            try { await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            try { File.Delete(script); } catch { }
            if (!string.IsNullOrWhiteSpace(evidencePath))
            {
                File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
                {
                    test = nameof(AuthoritativePsesSession_ConnectsDapHitsBreakpointInspectsAndReturnsToHost),
                    passed = completed,
                    psesRoot = root,
                    powershellVersion = runtime.VersionText,
                    processId = sessionPid,
                    processStartTimeUtc = sessionStart,
                    sessionGeneration,
                    debugServicePipePublished,
                    debuggerSessionId,
                    paused,
                    frameCount,
                    variableCount,
                    stepOverObserved,
                    postDebugStateObserved = output.Any(item => item.Contains("AZ_POST_DEBUG|", StringComparison.Ordinal)),
                    processRunningAfterShutdown = service.IsSessionRunning
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.True(condition(), "The expected production PSES/DAP state was not observed before the timeout.");
    }

    private static PowerShellRuntimeInfo? FindRuntime()
    {
        var path = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!File.Exists(path)) return null;
        return new PowerShellRuntimeInfo("AZ PowerShell", "Core", "7.x", new Version(7, 0), "x64", path, "Phase2C-AZ", true, false, true, true);
    }
}
