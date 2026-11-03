using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class R20LongRunningExecutionTests
{
    [Fact(Timeout = 120_000)]
    public async Task FortySecondForegroundRunRetainsOwnershipAndSharedSession()
    {
        await using var fixture = await LiveFixture.CreateAsync();
        var pid = fixture.Service.ProcessId;
        var promptBefore = fixture.PromptCount;
        var execution = fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
            "R20 40-second run", "Write-Host 'R20_START'; Start-Sleep -Seconds 40; Write-Host 'R20_END'; $global:R20Value = 42", false, _ => { });

        await WaitForAsync(() => fixture.Output.Contains("R20_START", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(31));
        Assert.False(execution.IsCompleted);
        Assert.Equal(pid, fixture.Service.ProcessId);
        Assert.True(fixture.Service.IsSessionRunning);

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.Contains("R20_END", fixture.Output, StringComparison.Ordinal);
        Assert.True(fixture.PromptCount > promptBefore);
        Assert.Equal(pid, fixture.Service.ProcessId);

        var followUp = await fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
            "R20 follow-up", "if ($global:R20Value -ne 42) { throw 'Shared state lost' }; Write-Host 'R20_FOLLOWUP'", false, _ => { })
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(EditorExecutionStatus.Completed, followUp.Status);
        Assert.Contains("R20_FOLLOWUP", fixture.Output, StringComparison.Ordinal);
        Assert.Equal(pid, fixture.Service.ProcessId);
    }

    [Fact(Timeout = 90_000)]
    public async Task CancelledForegroundRunRecoversPromptBeforeReleasingOwnership()
    {
        await using var fixture = await LiveFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var pid = fixture.Service.ProcessId;
        var promptBefore = fixture.PromptCount;
        var execution = fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
            "R20 cancellable run", "Write-Host 'R20_CANCEL_START'; Start-Sleep -Seconds 60; Write-Host 'R20_CANCEL_BAD_END'", false, _ => { }, cancellation.Token);

        await WaitForAsync(() => fixture.Output.Contains("R20_CANCEL_START", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(35));
        Assert.False(execution.IsCompleted);
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(25));

        Assert.Equal(EditorExecutionStatus.Cancelled, result.Status);
        Assert.DoesNotContain("R20_CANCEL_BAD_END", fixture.Output, StringComparison.Ordinal);
        Assert.True(fixture.PromptCount > promptBefore);
        Assert.Equal(pid, fixture.Service.ProcessId);
        var followUp = await fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
            "R20 post-cancel", "Write-Host 'R20_POST_CANCEL'", false, _ => { })
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(EditorExecutionStatus.Completed, followUp.Status);
        Assert.Contains("R20_POST_CANCEL", fixture.Output, StringComparison.Ordinal);
    }

    [Fact(Timeout = 150_000)]
    public async Task ProgressRunBeyondOneMinuteRetainsForegroundOwnership()
    {
        RequireExtendedRuntimeValidation();
        await using var fixture = await LiveFixture.CreateAsync();
        var pid = fixture.Service.ProcessId;
        var execution = fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
            "R20 long progress", "Write-Host 'R20_PROGRESS_START'; for ($i = 1; $i -le 65; $i++) { Write-Progress -Activity 'R20 Progress' -PercentComplete $i; Start-Sleep -Seconds 1 }; Write-Progress -Activity 'R20 Progress' -Completed; Write-Host 'R20_PROGRESS_END'", false, _ => { });

        await WaitForAsync(() => fixture.Output.Contains("R20_PROGRESS_START", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(61));
        Assert.False(execution.IsCompleted);
        Assert.Equal(pid, fixture.Service.ProcessId);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.Contains("R20_PROGRESS_END", fixture.Output, StringComparison.Ordinal);
        Assert.Equal(pid, fixture.Service.ProcessId);
    }

    [Fact(Timeout = 900_000)]
    public async Task OriginalSystemDiagnosticsProducesReportWithoutPrematureCompletion()
    {
        RequireExtendedRuntimeValidation();
        const string scriptPath = @"C:\Powershell Scripts\SystemDiagnostics.ps1";
        if (!File.Exists(scriptPath))
            throw Xunit.Sdk.SkipException.ForSkip("The original SystemDiagnostics.ps1 was not available.");

        DeveloperDiagnostics.Enable(reason: "R20 original-script runtime validation");
        try
        {
            await using var fixture = await LiveFixture.CreateAsync();
            var pid = fixture.Service.ProcessId;
            var startedAt = DateTime.UtcNow;
            var script = await File.ReadAllTextAsync(scriptPath);
            var execution = fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
                scriptPath, script, false, _ => { });

            await Task.Delay(TimeSpan.FromSeconds(35));
            Assert.False(execution.IsCompleted);
            Assert.Equal(pid, fixture.Service.ProcessId);
            var result = await execution.WaitAsync(TimeSpan.FromMinutes(12));

            Assert.True(result.Status == EditorExecutionStatus.Completed,
                $"Expected Completed; status={result.Status}; error={result.ErrorMessage ?? "<none>"}; processId={fixture.Service.ProcessId?.ToString() ?? "<none>"}; outputTail={fixture.Output[^Math.Min(fixture.Output.Length, 500)..]}");
            Assert.Contains("PC Health Report written to:", fixture.Output, StringComparison.Ordinal);
            var reportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PCHealthReport");
            Assert.Contains(Directory.GetFiles(reportDirectory, "PC-Health-Report_*.html"),
                path => File.GetLastWriteTimeUtc(path) >= startedAt && new FileInfo(path).Length > 0);
            Assert.Equal(pid, fixture.Service.ProcessId);
            var followUp = await fixture.Service.ExecuteStructuredScriptAsync(Guid.NewGuid(), fixture.Service.SessionGeneration,
                "R20 post-diagnostics", "Write-Host 'R20_DIAGNOSTICS_FOLLOWUP'", false, _ => { })
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(EditorExecutionStatus.Completed, followUp.Status);
            Assert.Contains("R20_DIAGNOSTICS_FOLLOWUP", fixture.Output, StringComparison.Ordinal);
        }
        finally { DeveloperDiagnostics.Disable("R20 original-script runtime validation ended"); }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Expected live terminal output did not arrive.");
    }

    private static void RequireExtendedRuntimeValidation()
    {
        if (Environment.GetEnvironmentVariable("PS7SCRIPTDESK_RUN_R20_EXTENDED") != "1")
            throw Xunit.Sdk.SkipException.ForSkip("Set PS7SCRIPTDESK_RUN_R20_EXTENDED=1 to run the >60-second and original-script UAT cases.");
    }

    private sealed class LiveFixture : IAsyncDisposable
    {
        private readonly string? _previousPsesRoot;
        private readonly ConcurrentQueue<string> _output = new();
        private int _promptCount;

        private LiveFixture(PsesOwnedInteractiveSessionService service, string? previousPsesRoot)
        {
            Service = service;
            _previousPsesRoot = previousPsesRoot;
            service.RawOutputReceived += (_, text) => _output.Enqueue(text);
            service.PromptReadyObserved += (_, _) => Interlocked.Increment(ref _promptCount);
        }

        public PsesOwnedInteractiveSessionService Service { get; }
        public string Output => string.Concat(_output);
        public int PromptCount => Volatile.Read(ref _promptCount);

        public static async Task<LiveFixture> CreateAsync()
        {
            var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
            var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
            if (!File.Exists(runtimePath) || !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
                throw Xunit.Sdk.SkipException.ForSkip("R20 live regression requires PowerShell 7 and PSES.");

            var previousPsesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
            var service = new PsesOwnedInteractiveSessionService();
            var fixture = new LiveFixture(service, previousPsesRoot);
            try
            {
                var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
                    "x64", runtimePath, "R20", true, false, true, true);
                await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                    .WaitAsync(TimeSpan.FromSeconds(45));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                Service.Dispose();
                Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", _previousPsesRoot);
            }
        }
    }
}
