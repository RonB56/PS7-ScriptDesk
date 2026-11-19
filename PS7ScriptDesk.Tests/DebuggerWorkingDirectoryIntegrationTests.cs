using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

// These tests use the production PSES host and its ConPTY console, not a simulated DAP server.
[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class DebuggerWorkingDirectoryIntegrationTests
{
    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DebugLaunchPreservesInteractiveDirectoryAndRelativePaths(bool useSnapshotPath)
    {
        var runtime = RequirePses();
        var root = Path.Combine(Path.GetTempPath(), $"PS7 ScriptDesk CWD {Guid.NewGuid():N}");
        var consoleDirectory = Path.Combine(root, "console directory");
        var scriptDirectory = Path.Combine(root, useSnapshotPath ? "DebugSnapshots" : "saved scripts");
        Directory.CreateDirectory(consoleDirectory);
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, "debug script.ps1");
        await File.WriteAllTextAsync(Path.Combine(consoleDirectory, "relative file.txt"), "relative-ok");
        await File.WriteAllTextAsync(scriptPath,
            "Write-Output ('SCRIPT_LOCATION|' + (Get-Location).Path)\n" +
            "$value = (Get-Content -LiteralPath '.\\relative file.txt' -Raw).Trim()\n" +
            "Write-Output ('RELATIVE|' + $value)\n" +
            "Write-Output ('SCRIPT_ROOT|' + $PSScriptRoot)\n");

        var output = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService();
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), root).WaitAsync(TimeSpan.FromSeconds(45));
            var processId = service.ProcessId;
            var generation = service.SessionGeneration;
            await SendAndObserveAsync(service, output,
                $"Set-Location -LiteralPath '{Quote(consoleDirectory)}'; Write-Output ('BEFORE|' + (Get-Location).Path)",
                "BEFORE|" + consoleDirectory);

            // No breakpoint: the absolute program path must not select its parent as cwd.
            using (var normal = new PsesDapDebugSession(service))
            {
                await normal.StartAsync(runtime, scriptPath, []);
                await WaitForAsync(() => normal.CurrentState == DebugSessionState.Stopped, TimeSpan.FromSeconds(30));
            }
            await SendAndObserveAsync(service, output,
                "Write-Output ('AFTER_NORMAL|' + (Get-Location).Path)",
                "AFTER_NORMAL|" + consoleDirectory);

            // A breakpoint and step exercise the same PSES host after the first launch.
            using (var paused = new PsesDapDebugSession(service))
            {
                await paused.StartAsync(runtime, scriptPath, [new DebugBreakpointInfo(scriptPath, 2)]);
                await WaitForAsync(() => paused.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
                await paused.StepOverAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await WaitForAsync(() => paused.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(15));
                await paused.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await WaitForAsync(() => paused.CurrentState == DebugSessionState.Stopped, TimeSpan.FromSeconds(30));
            }
            await SendAndObserveAsync(service, output,
                "Write-Output ('AFTER_STEP|' + (Get-Location).Path)",
                "AFTER_STEP|" + consoleDirectory);

            Assert.Contains(output, item => item.Contains("SCRIPT_LOCATION|" + consoleDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(output, item => item.Contains("RELATIVE|relative-ok", StringComparison.Ordinal));
            Assert.Contains(output, item => item.Contains("SCRIPT_ROOT|" + scriptDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.True(service.IsSessionRunning);
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(generation, service.SessionGeneration);
        }
        finally
        {
            try { await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task CancellationPreservesLocationButIntentionalScriptChangePersists()
    {
        var runtime = RequirePses();
        var root = Path.Combine(Path.GetTempPath(), $"PS7 ScriptDesk CWD {Guid.NewGuid():N}");
        var initial = Path.Combine(root, "initial location");
        var intentional = Path.Combine(root, "intentional location");
        var snapshots = Path.Combine(root, "DebugSnapshots");
        Directory.CreateDirectory(initial);
        Directory.CreateDirectory(intentional);
        Directory.CreateDirectory(snapshots);
        var cancellablePath = Path.Combine(snapshots, "cancel script.ps1");
        var intentionalPath = Path.Combine(snapshots, "change location.ps1");
        await File.WriteAllTextAsync(cancellablePath, "$value = 1\nStart-Sleep -Seconds 30\n");
        await File.WriteAllTextAsync(intentionalPath, $"Set-Location -LiteralPath '{Quote(intentional)}'\nWrite-Output 'INTENTIONAL_DONE'\n");

        var output = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService();
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), root).WaitAsync(TimeSpan.FromSeconds(45));
            var processId = service.ProcessId;
            var generation = service.SessionGeneration;
            await SendAndObserveAsync(service, output,
                $"Set-Location -LiteralPath '{Quote(initial)}'; Write-Output ('BEFORE_CANCEL|' + (Get-Location).Path)",
                "BEFORE_CANCEL|" + initial);

            using (var cancelled = new PsesDapDebugSession(service))
            {
                await cancelled.StartAsync(runtime, cancellablePath, [new DebugBreakpointInfo(cancellablePath, 2)]);
                await WaitForAsync(() => cancelled.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
                Assert.True(await cancelled.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            }
            await SendAndObserveAsync(service, output,
                "Write-Output ('AFTER_CANCEL|' + (Get-Location).Path)",
                "AFTER_CANCEL|" + initial);

            using (var changed = new PsesDapDebugSession(service))
            {
                await changed.StartAsync(runtime, intentionalPath, []);
                await WaitForAsync(() => changed.CurrentState == DebugSessionState.Stopped, TimeSpan.FromSeconds(30));
            }
            await SendAndObserveAsync(service, output,
                "Write-Output ('AFTER_INTENTIONAL|' + (Get-Location).Path)",
                "AFTER_INTENTIONAL|" + intentional);
            Assert.True(service.IsSessionRunning);
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(generation, service.SessionGeneration);
        }
        finally
        {
            try { await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static PowerShellRuntimeInfo RequirePses()
    {
        var path = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var root = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(root) ||
            !File.Exists(Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("Real PSES integration requires PowerShell 7 and PS7SCRIPTDESK_PSES_ROOT.");
        return new PowerShellRuntimeInfo("CWD integration", "Core", "7.x", new Version(7, 0), "x64", path, "CON03-R1", true, false, true, true);
    }

    private static async Task SendAndObserveAsync(PsesOwnedInteractiveSessionService service, ConcurrentQueue<string> output, string command, string expected)
    {
        await service.WriteRawInputAsync(command + "\r").WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => output.Any(item => item.Contains(expected, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(15));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Expected output or debugger state was not observed before the timeout.");
    }

    private static string Quote(string path) => path.Replace("'", "''", StringComparison.Ordinal);
}
