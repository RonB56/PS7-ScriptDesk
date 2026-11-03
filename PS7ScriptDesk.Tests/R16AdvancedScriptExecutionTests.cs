using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class R16AdvancedScriptExecutionTests
{
    [Fact(Timeout = 120_000)]
    public async Task LoadedAdvancedScript_RunsRepeatedlyAndReturnsToSharedInteractivePrompt()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("R16 live regression requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        var path = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-R16-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path,
            "#requires -Version 7.0\n[CmdletBinding()]\nparam()\n" +
            "Write-Output 'R16_START'\n" +
            "$global:R16_LoadRunValue = [guid]::NewGuid().ToString('N')\n" +
            "Write-Output ('R16_PID=' + $PID)\n" +
            "Write-Output ('R16_VALUE=' + $global:R16_LoadRunValue)\n" +
            "Write-Output 'R16_END'\n");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "R16", true, false, true, true);
        using var service = new PsesOwnedInteractiveSessionService();
        var output = new ConcurrentQueue<string>();
        var promptCount = 0;
        service.RawOutputReceived += (_, text) => output.Enqueue(text);
        service.PromptReadyObserved += (_, _) => Interlocked.Increment(ref promptCount);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            var pid = service.ProcessId;
            var content = await File.ReadAllTextAsync(path);
            var lastValue = string.Empty;
            for (var run = 0; run < 2; run++)
            {
                var startLength = string.Concat(output).Length;
                var promptsBefore = promptCount;
                await service.ExecuteScriptAsync(path, content, _ => { })
                    .WaitAsync(TimeSpan.FromSeconds(25));
                var runOutput = string.Concat(output)[startLength..];
                Assert.Contains("R16_START", runOutput, StringComparison.Ordinal);
                Assert.Contains($"R16_PID={pid}", runOutput, StringComparison.Ordinal);
                var valueMatch = Regex.Match(runOutput, @"R16_VALUE=([0-9a-f]{32})");
                Assert.True(valueMatch.Success, "The advanced script did not emit its assigned session value.");
                lastValue = valueMatch.Groups[1].Value;
                Assert.Contains("R16_END", runOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("Unexpected attribute 'CmdletBinding'", runOutput, StringComparison.Ordinal);
                Assert.True(promptCount > promptsBefore, "The foreground prompt did not recover after Run.");
                Assert.Equal(pid, service.ProcessId);
            }

            var error = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "R16 error", "throw 'R16_EXPECTED_ERROR'", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(EditorExecutionStatus.Failed, error.Status);
            Assert.True(error.CompletionObserved);
            Assert.True(service.IsInputWritable);
            var invalidStartLength = string.Concat(output).Length;
            var invalid = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "R16 invalid syntax", "[CmdletBinding()] param(", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(EditorExecutionStatus.Failed, invalid.Status);
            Assert.False(invalid.CompletionObserved);
            Assert.DoesNotContain("expression after '&'", string.Concat(output)[invalidStartLength..], StringComparison.OrdinalIgnoreCase);
            Assert.True(service.IsInputWritable);
            await service.WriteRawInputAsync("Write-Output ('R16_SHARED=' + $global:R16_LoadRunValue)\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => string.Concat(output).Contains("R16_SHARED=" + lastValue, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            Assert.Equal(pid, service.ProcessId);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
            File.Delete(path);
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
        Assert.True(condition(), "Expected terminal output did not arrive.");
    }
}
