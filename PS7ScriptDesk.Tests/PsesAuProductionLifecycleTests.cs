using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class PsesAuProductionLifecycleTests
{
    [Fact]
    public async Task ProductionPsesSession_CompletesFiveInterruptAndRecoveryLifecycles()
    {
        var runtime = FindRuntime();
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (runtime is null || string.IsNullOrWhiteSpace(psesRoot) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
        {
            throw Xunit.Sdk.SkipException.ForSkip("AU production lifecycle requires PowerShell 7 and the recovered PSES root.");
        }

        for (var lifecycle = 1; lifecycle <= 5; lifecycle++)
        {
            using var service = new PsesOwnedInteractiveSessionService();
            var output = new ConcurrentQueue<string>();
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            try
            {
                Assert.True(service.IsSessionRunning);
                Assert.True(service.IsInputWritable);
                Assert.NotNull(service.ProcessId);
                Assert.True(service.SessionGeneration > 0);

                var ready = $"AU_READY_{lifecycle}_{Guid.NewGuid():N}";
                await service.WriteRawInputAsync($"$global:AU_TOKEN='{ready}'; Write-Output '{ready}'; Write-Output ('AU_ID|'+$PID+'|'+[runspace]::DefaultRunspace.InstanceId)\r")
                    .WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForAsync(output, ready, TimeSpan.FromSeconds(15));
                Assert.Contains(output, item => item.Contains("AU_ID|", StringComparison.Ordinal));

                var structured = await service.ExecuteStructuredScriptAsync(
                    Guid.NewGuid(), service.SessionGeneration, $"AU-{lifecycle}",
                    $"Write-Output 'AU_STRUCTURED_{lifecycle}'", false,
                    record => output.Enqueue(record.Text)).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(EditorExecutionStatus.Completed, structured.Status);
                Assert.True(structured.CompletionObserved);
                await WaitForAsync(output, $"AU_STRUCTURED_{lifecycle}", TimeSpan.FromSeconds(10));

                var running = $"AU_RUNNING_{lifecycle}_{Guid.NewGuid():N}";
                await service.WriteRawInputAsync($"Write-Output '{running}'; Start-Sleep -Seconds 30; Write-Output 'AU_SHOULD_NOT_APPEAR_{lifecycle}'\r")
                    .WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForAsync(output, running, TimeSpan.FromSeconds(10));
                Assert.True(await service.StopConsoleAsync().WaitAsync(TimeSpan.FromSeconds(10)));

                var recovered = $"AU_RECOVERED_{lifecycle}_{Guid.NewGuid():N}";
                await service.WriteRawInputAsync($"Write-Output '{recovered}'\r").WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForAsync(output, recovered, TimeSpan.FromSeconds(15));
            }
            finally
            {
                await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(service.IsSessionRunning);
                Assert.False(service.IsInputWritable);
            }
        }
    }

    private static async Task WaitForAsync(ConcurrentQueue<string> output, string expected, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (output.Any(item => item.Contains(expected, StringComparison.Ordinal))) return;
            await Task.Delay(50);
        }

        Assert.Contains(output, item => item.Contains(expected, StringComparison.Ordinal));
    }

    private static PowerShellRuntimeInfo? FindRuntime()
    {
        var configured = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH");
        var candidates = new[] { configured, @"C:\Program Files\PowerShell\7\pwsh.exe", "pwsh" }
            .Where(item => !string.IsNullOrWhiteSpace(item));
        foreach (var candidate in candidates)
        {
            var path = candidate!;
            if (!Path.IsPathRooted(path))
            {
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo("where.exe", path)
                    {
                        RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                    });
                    path = probe?.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? path;
                    probe?.WaitForExit(3000);
                }
                catch { }
            }

            if (!File.Exists(path)) continue;
            return new PowerShellRuntimeInfo("AU PowerShell", "Core", "7.x", new Version(7, 0),
                Environment.Is64BitOperatingSystem ? "x64" : "unknown", path, "Phase2C-AU",
                true, false, true, true);
        }

        return null;
    }
}
