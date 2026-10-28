using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class PsesLiveCompletionProductionIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task ConsoleStateIsVisibleToAuthoritativeLiveCompletionAndPersistsAcrossExecution()
    {
        var runtime = FindRuntime();
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (runtime is null || string.IsNullOrWhiteSpace(psesRoot) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Live completion integration requires PowerShell 7 and the recovered PSES root.");
        }

        using var service = new PsesOwnedInteractiveSessionService();
        var output = new ConcurrentQueue<string>();
        await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), Environment.CurrentDirectory)
            .WaitAsync(TimeSpan.FromSeconds(45));
        try
        {
            Assert.True(service.IsLiveCompletionAvailable);

            var variableToken = $"ISE_LIVE_{Guid.NewGuid():N}";
            await service.WriteRawInputAsync($"${variableToken} = 'SharedSessionVerified'; Write-Output 'LIVE_VARIABLE_READY'\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "LIVE_VARIABLE_READY", TimeSpan.FromSeconds(15));

            var variableResult = await service.GetLiveCompletionsAsync(
                "untitled:ps7scriptdesk/live-probe.ps1",
                "$" + variableToken[..12],
                variableToken[..12].Length + 1)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(variableResult);
            Assert.Contains(variableResult!.Items, item => item.CompletionText.Contains(variableToken, StringComparison.OrdinalIgnoreCase));

            await service.WriteRawInputAsync("$Customer = [PSCustomObject]@{ Name = 'Ron'; AccountNumber = 123; Active = $true }; Write-Output 'LIVE_OBJECT_READY'\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "LIVE_OBJECT_READY", TimeSpan.FromSeconds(15));

            var objectResult = await service.GetLiveCompletionsAsync(
                "untitled:ps7scriptdesk/live-probe.ps1",
                "$Customer.",
                "$Customer.".Length)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(objectResult);
            Assert.Contains(objectResult!.Items, item => string.Equals(item.CompletionText, "Name", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(objectResult.Items, item => string.Equals(item.CompletionText, "AccountNumber", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(objectResult.Items, item => string.Equals(item.CompletionText, "Active", StringComparison.OrdinalIgnoreCase));

            await service.WriteRawInputAsync("$Customer | Add-Member -NotePropertyName Region -NotePropertyValue 'North' -Force; Write-Output 'LIVE_OBJECT_MUTATED'\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "LIVE_OBJECT_MUTATED", TimeSpan.FromSeconds(15));
            var mutatedObjectResult = await service.GetLiveCompletionsAsync(
                "untitled:ps7scriptdesk/live-probe.ps1",
                "$Customer.",
                "$Customer.".Length)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(mutatedObjectResult);
            Assert.Contains(mutatedObjectResult!.Items, item => string.Equals(item.CompletionText, "Region", StringComparison.OrdinalIgnoreCase));

            var editorVariable = $"$Orders = [PSCustomObject]@{{ Count = 42; Status = 'Ready' }}";
            await service.ExecuteScriptAsync("live completion probe", editorVariable, record => output.Enqueue(record.Text), executeInCurrentScope: true)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await service.WriteRawInputAsync("$Orders.Count; Write-Output 'LIVE_EDITOR_STATE_READY'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "42", TimeSpan.FromSeconds(15));

            var functionName = "Get-SessionProbe";
            await service.WriteRawInputAsync("function Get-SessionProbe { 'SessionVerified' }; Write-Output 'LIVE_FUNCTION_READY'\r")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(output, "LIVE_FUNCTION_READY", TimeSpan.FromSeconds(15));
            var functionResult = await service.GetLiveCompletionsAsync(
                "untitled:ps7scriptdesk/live-probe.ps1",
                "Get-Sess",
                "Get-Sess".Length)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(functionResult);
            Assert.Contains(functionResult!.Items, item => string.Equals(item.CompletionText, functionName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task WaitForAsync(ConcurrentQueue<string> output, string expected, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (output.Any(item => item.Contains(expected, StringComparison.OrdinalIgnoreCase))) return;
            await Task.Delay(50);
        }

        Assert.Contains(output, item => item.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static PowerShellRuntimeInfo? FindRuntime()
    {
        var configured = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH");
        var candidates = new[] { configured, Environment.GetEnvironmentVariable("PS7SCRIPTDESK_POWERSHELL"), @"C:\Program Files\PowerShell\7\pwsh.exe", "pwsh" }
            .Where(item => !string.IsNullOrWhiteSpace(item));
        foreach (var candidate in candidates)
        {
            var path = candidate!;
            if (!Path.IsPathRooted(path))
            {
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo("where.exe", path) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                    path = probe?.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? path;
                    probe?.WaitForExit(3000);
                }
                catch { }
            }

            if (!File.Exists(path)) continue;
            return new PowerShellRuntimeInfo("Live PowerShell", "Core", "7.x", new Version(7, 0), "x64", path, "LiveCompletion", true, false, true, true);
        }

        return null;
    }
}
