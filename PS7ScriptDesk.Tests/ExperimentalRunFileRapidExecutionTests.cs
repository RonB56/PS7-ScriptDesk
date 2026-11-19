using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class ExperimentalRunFileRapidExecutionTests
{
    [Fact(Timeout = 120_000)]
    public async Task DirectRunFile_RapidRequestsWhileBusyAreRejectedWithoutQueueing()
    {
        await using var harness = await StartAsync("R3 admission");
        var firstOutput = new ConcurrentQueue<string>();
        harness.Service.RawOutputReceived += (_, text) => firstOutput.Enqueue(text);
        var first = harness.Service.ExecuteScriptAsync(
            "R3 active request", "Start-Sleep -Seconds 2; Write-Output 'R3_ACTIVE_COMPLETE'",
            _ => { });

        await WaitForAsync(() => harness.Service.IsCommandInProgress, TimeSpan.FromSeconds(5));
        var rejected = new List<Task>();
        for (var index = 0; index < 10; index++)
        {
            rejected.Add(Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.ExecuteScriptAsync(
                $"R3 rejected {index}", $"Write-Output 'R3_UNEXPECTED_{index}'", _ => { })));
        }

        await Task.WhenAll(rejected).WaitAsync(TimeSpan.FromSeconds(10));
        await first.WaitAsync(TimeSpan.FromSeconds(45));
        await WaitForAsync(() => !harness.Service.IsCommandInProgress, TimeSpan.FromSeconds(5));

        var output = string.Concat(firstOutput);
        Assert.Contains("R3_ACTIVE_COMPLETE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("R3_UNEXPECTED_", output, StringComparison.Ordinal);
        Assert.True(harness.Service.IsSessionRunning);
    }

    [Fact(Timeout = 120_000)]
    public async Task DirectRunFile_ThirtyImmediateSequentialRunsKeepOutputAndHideGeneratedEcho()
    {
        await using var harness = await StartAsync("R3 sequential");
        var output = new ConcurrentQueue<string>();
        harness.Service.RawOutputReceived += (_, text) => output.Enqueue(text);
        for (var index = 0; index < 30; index++)
        {
            var marker = $"R4_SEQUENTIAL_{index:D2}";
            await harness.Service.ExecuteScriptAsync(
                "R3 rapid sequential", $"Write-Output '{marker}'", _ => { })
                .WaitAsync(TimeSpan.FromSeconds(30));
        }

        var visible = string.Concat(output);
        for (var index = 0; index < 30; index++)
        {
            Assert.Equal(1, CountOccurrences(visible, $"R4_SEQUENTIAL_{index:D2}"));
        }
        Assert.DoesNotContain("__ps7sd_status_", visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PS7ScriptDesk-", visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("& {", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("try {", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(">> ", visible, StringComparison.Ordinal);
        Assert.True(harness.Service.IsSessionRunning);
    }

    private static async Task<Harness> StartAsync(string name)
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("Experimental Run File rapid execution coverage requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, name, true, false, true, true);
        var service = new PsesOwnedInteractiveSessionService(
            new ExperimentalRunFileBridgeFeatureGate(isEnabled: true));
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            return new Harness(service, previousRoot);
        }
        catch
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
            service.Dispose();
            throw;
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
        Assert.True(condition(), "The expected execution state was not reached before the deadline.");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private sealed class Harness(PsesOwnedInteractiveSessionService service, string? previousRoot) : IAsyncDisposable
    {
        public PsesOwnedInteractiveSessionService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                Service.Dispose();
                Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
            }
        }
    }
}
