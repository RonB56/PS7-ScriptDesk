using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class Bug004DapPromptRecoveryTests
{
    [Fact]
    public async Task DapStructuredExecution_ReturnsToInteractivePromptWithoutInput()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BUG-004 live regression requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0), "x64",
            runtimePath, "BUG004-R10", true, false, true, true);
        using var service = new PsesOwnedInteractiveSessionService();
        var visible = new ConcurrentQueue<string>();
        var promptEvents = 0;
        service.RawOutputReceived += (_, text) => visible.Enqueue(text);
        service.PromptReadyObserved += (_, _) => Interlocked.Increment(ref promptEvents);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            await WaitForAsync(() => Regex.IsMatch(StripAnsi(string.Concat(visible)), @"PS\s+[^\r\n>]*>"), TimeSpan.FromSeconds(15));
            var originalProcessId = service.ProcessId;
            var initialRunspace = $"__BUG004_R10_INITIAL__|{originalProcessId}|";
            await service.WriteRawInputAsync("Write-Output ('__BUG004_R10_INITIAL__|'+$PID+'|'+[runspace]::DefaultRunspace.InstanceId)\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => string.Concat(visible).Contains(initialRunspace, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            var initialIdentity = GetIdentity(string.Concat(visible), "__BUG004_R10_INITIAL__");
            var initialPromptEvents = promptEvents;
            var startOffset = string.Concat(visible).Length;
            var result = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "BUG004-R10-prompt", "Write-Output ('__BUG004_R10_RESULT__|'+$PID+'|'+[runspace]::DefaultRunspace.InstanceId)", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(EditorExecutionStatus.Completed, result.Status);
            await WaitForAsync(() => string.Concat(visible).Contains("__BUG004_R10_RESULT__", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            var suffix = string.Concat(visible)[startOffset..];

            Assert.True(promptEvents > initialPromptEvents, "DAP completion was observed, but no subsequent interactive prompt boundary arrived without input.");
            Assert.Matches(@"PS\s+[^\r\n>]*>\s*(?:\r?\n|$)", StripAnsi(suffix));
            Assert.DoesNotContain("__ps7sd_status_", suffix, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(initialIdentity, GetIdentity(suffix, "__BUG004_R10_RESULT__"));

            await service.WriteRawInputAsync("Write-Output '__BUG004_R10_INTERACTIVE__'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => string.Concat(visible).Contains("__BUG004_R10_INTERACTIVE__", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.Equal(originalProcessId, service.ProcessId);

            var beforeErrorPromptEvents = promptEvents;
            var errorStartOffset = string.Concat(visible).Length;
            var errorResult = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "BUG004-R10-error", "Write-Error '__BUG004_R10_ERROR__' -ErrorAction Continue", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(EditorExecutionStatus.Failed, errorResult.Status);
            await WaitForAsync(() => string.Concat(visible).Contains("__BUG004_R10_ERROR__", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            await WaitForAsync(() => promptEvents > beforeErrorPromptEvents, TimeSpan.FromSeconds(10));
            var errorSuffix = StripAnsi(string.Concat(visible)[errorStartOffset..]);
            Assert.Matches(@"PS\s+[^\r\n>]*>\s*(?:\r?\n|$)", errorSuffix);
            Assert.DoesNotContain("__ps7sd_status_", errorSuffix, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalProcessId, service.ProcessId);
            Assert.DoesNotContain("__ps7sd_status_", string.Concat(visible), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
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
        Assert.True(condition(), "Expected output or prompt boundary did not arrive.");
    }

    private static string StripAnsi(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        var escape = false;
        foreach (var character in value)
        {
            if (escape)
            {
                if (character is >= '@' and <= '~') escape = false;
                continue;
            }
            if (character == '\u001b') { escape = true; continue; }
            result.Append(character);
        }
        return result.ToString();
    }

    private static string GetIdentity(string output, string marker)
    {
        var line = StripAnsi(output).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Last(item => item.Contains(marker, StringComparison.Ordinal));
        var fields = line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].TrimStart('|').Split('|');
        Assert.Equal(2, fields.Length);
        return fields[1];
    }
}
