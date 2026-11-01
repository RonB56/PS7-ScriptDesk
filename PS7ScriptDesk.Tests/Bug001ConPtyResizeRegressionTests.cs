using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class Bug001ConPtyResizeRegressionTests
{
    [Fact(Timeout = 180_000)]
    public async Task RepeatedEditorExecution_DoesNotBlockSubsequentDebuggerSession()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BUG-001 live regression requires PowerShell 7 and PSES.");

        var priorPsesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "BUG001-R9", true, false, true, true);
        var debugScript = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-R9-Debug-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(debugScript,
            "Write-Output 'R9_DEBUG_BEFORE'\nWrite-Output 'R9_DEBUG_BREAK'\nWrite-Output 'R9_DEBUG_DONE'\n");
        var chunks = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService();
        service.RawOutputReceived += (_, text) => chunks.Enqueue(text);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            var processId = service.ProcessId;
            var generation = service.SessionGeneration;

            await service.ExecuteScriptAsync("R9 selection", "$global:Bug001R9State = 41", _ => { },
                executeInCurrentScope: true).WaitAsync(TimeSpan.FromSeconds(30));
            await service.ExecuteScriptAsync("R9 file", "Write-Output ('R9_STATE|' + $global:Bug001R9State)", _ => { },
                executeInCurrentScope: false).WaitAsync(TimeSpan.FromSeconds(30));
            await WaitForAsync(() => string.Concat(chunks).Contains("R9_STATE|41\r\n", StringComparison.Ordinal));
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(generation, service.SessionGeneration);
            Assert.DoesNotContain("__ps7sd_status_", string.Concat(chunks), StringComparison.OrdinalIgnoreCase);

            using var debugger = new PsesDapDebugSession(service);
            await debugger.StartAsync(runtime, debugScript, [new DebugBreakpointInfo(debugScript, 2)]);
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused, 30);
            await debugger.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Stopped, 30);
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(generation, service.SessionGeneration);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", priorPsesRoot);
            try { File.Delete(debugScript); } catch { }
        }
    }

    [Fact]
    public async Task StructuredExecution_HistoryRecallDoesNotRevealInternalWrapper()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BUG-001 live regression requires PowerShell 7 and PSES.");

        var priorPsesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "BUG001-R9", true, false, true, true);
        var chunks = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService();
        service.RawOutputReceived += (_, text) => chunks.Enqueue(text);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            await WaitForAsync(() => string.Concat(chunks).Contains("PS ", StringComparison.Ordinal));
            var result = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "BUG001-R9-history", "Write-Output '__BUG001_R9_HISTORY__'", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(EditorExecutionStatus.Completed, result.Status);
            await WaitForAsync(() => string.Concat(chunks).Contains("__BUG001_R9_HISTORY__\r\n", StringComparison.Ordinal));
            var beforeRecall = string.Concat(chunks);
            await service.WriteRawInputAsync("\u001b[A").WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            var recalled = string.Concat(chunks)[beforeRecall.Length..];
            Assert.DoesNotContain("__ps7sd_status_", recalled, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PS7ScriptDesk-", recalled, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", priorPsesRoot);
        }
    }

    [Fact]
    public async Task StructuredExecution_ResizeDoesNotRevealInternalWrapper()
    {
        const string resultMarker = "__BUG001_R8_RESULT__";
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BUG-001 live regression requires PowerShell 7 and PSES.");

        var priorPsesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "BUG001-R8", true, false, true, true);
        var chunks = new ConcurrentQueue<string>();
        using var service = new PsesOwnedInteractiveSessionService();
        service.RawOutputReceived += (_, text) => chunks.Enqueue(text);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            await WaitForAsync(() => string.Concat(chunks).Contains("PS ", StringComparison.Ordinal));

            var result = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "BUG001-R8-resize", $"Write-Output '{resultMarker}'", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(EditorExecutionStatus.Completed, result.Status);
            await WaitForAsync(() => string.Concat(chunks).Contains(resultMarker + "\r\n", StringComparison.Ordinal));
            var beforeResize = string.Concat(chunks);
            Assert.DoesNotContain("__ps7sd_status_", beforeResize, StringComparison.OrdinalIgnoreCase);

            service.ResizeConsole(60, 24);
            service.ResizeConsole(110, 24);
            await Task.Delay(600); // Allow the native ConPTY/PSReadLine redraw to arrive.
            var afterResize = string.Concat(chunks);
            Assert.Contains(resultMarker + "\r\n", afterResize, StringComparison.Ordinal);
            Assert.DoesNotContain("__ps7sd_status_", afterResize[beforeResize.Length..], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", priorPsesRoot);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 15)
    {
        var timeout = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < timeout)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), $"Expected PSES state did not arrive within {seconds} seconds.");
    }
}
