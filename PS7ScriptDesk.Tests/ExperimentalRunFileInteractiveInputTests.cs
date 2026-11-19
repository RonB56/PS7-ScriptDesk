using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class ExperimentalRunFileInteractiveInputTests
{
    [Fact(Timeout = 120_000)]
    public async Task DirectRunFile_InteractiveAndSecureReadHost_UsesSharedConsoleWithoutShowingWrapper()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("Experimental Run File interaction coverage requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "A02-P2B-3A-R2", true, false, true, true);
        var chunks = new ConcurrentQueue<string>();
        var preFilterChunks = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService(
            new ExperimentalRunFileBridgeFeatureGate(isEnabled: true));
        service.RawOutputReceived += (_, text) => chunks.Enqueue(text);
        service.OutputObservedBeforeDisplayFiltering += (_, text) => preFilterChunks.Enqueue(text);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            var processId = service.ProcessId;
            var generation = service.SessionGeneration;

            await service.WriteRawInputAsync(
                "$global:A02BridgeShared = 'SHARED_SESSION_OK'; Write-Output 'R2_SHARED_READY'\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => string.Concat(chunks).Contains("R2_SHARED_READY", StringComparison.Ordinal));
            var visibleStart = string.Concat(chunks).Length;
            var preFilterStart = string.Concat(preFilterChunks).Length;

            var script = "$plain = Read-Host 'R2_PLAIN_INPUT'\n" +
                "if ($plain -ceq 'plain-ok') { Write-Output 'R2_PLAIN_OK' } else { Write-Output 'R2_PLAIN_BAD' }\n" +
                "$secure = Read-Host 'R2_SECURE_INPUT' -AsSecureString\n" +
                "$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)\n" +
                "try { $secureText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer); if ($secureText -ceq 'secure-ok') { Write-Output 'R2_SECURE_OK' } else { Write-Output 'R2_SECURE_BAD' } } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }\n" +
                "Write-Output ('R2_SHARED|' + $global:A02BridgeShared)\n";
            var run = service.ExecuteScriptAsync("A02 interactive Run File", script, _ => { });

            await WaitForAsync(() => string.Concat(chunks).Contains("R2_PLAIN_INPUT", StringComparison.Ordinal));
            await service.WriteRawInputAsync("plain-ok\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => string.Concat(chunks).Contains("R2_SECURE_INPUT", StringComparison.Ordinal));
            await service.WriteRawInputAsync("secure-ok\r").WaitAsync(TimeSpan.FromSeconds(10));
            await run.WaitAsync(TimeSpan.FromSeconds(45));

            var visibleRun = string.Concat(chunks)[visibleStart..];
            var rawRunOutput = string.Concat(preFilterChunks)[preFilterStart..];
            Assert.Contains("R2_PLAIN_OK", visibleRun, StringComparison.Ordinal);
            Assert.Contains("R2_SECURE_OK", visibleRun, StringComparison.Ordinal);
            Assert.Contains("R2_SHARED|SHARED_SESSION_OK", visibleRun, StringComparison.Ordinal);
            Assert.Contains("__ps7sd_status_", rawRunOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("& {", rawRunOutput, StringComparison.Ordinal);
            Assert.Contains("try {", rawRunOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("secure-ok", visibleRun, StringComparison.Ordinal);
            Assert.DoesNotContain("__ps7sd_status_", visibleRun, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PS7ScriptDesk-", visibleRun, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("& {", visibleRun, StringComparison.Ordinal);
            Assert.DoesNotContain("try {", visibleRun, StringComparison.Ordinal);
            Assert.DoesNotContain(">> ", visibleRun, StringComparison.Ordinal);
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(generation, service.SessionGeneration);
            Assert.True(service.IsSessionRunning);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "The expected interactive Run File prompt or output did not arrive.");
    }
}
