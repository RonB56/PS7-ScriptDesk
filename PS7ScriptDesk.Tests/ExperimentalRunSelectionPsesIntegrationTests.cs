using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class ExperimentalRunSelectionPsesIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public async Task DirectRunSelection_DotSourcesIntoSharedPsesSessionAndPreservesRequestIdentity()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("Experimental Run Selection integration coverage requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "A02-P2B-3B-R1", true, false, true, true);
        var output = new ConcurrentQueue<string>();
        var documentId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        using var service = new PsesOwnedInteractiveSessionService(
            new ExperimentalRunFileBridgeFeatureGate(false),
            new ExperimentalRunSelectionBridgeFeatureGate(true));
        service.RawOutputReceived += (_, text) => output.Enqueue(text);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            var processId = service.ProcessId;
            var generation = service.SessionGeneration;
            var sourceInfo = new EditorExecutionSourceInfo(documentId, 17, 42, 73);
            var selectedSource = "$global:A02R1SelectionValue = 'selection-shared'; function global:A02R1SelectionFunction { 'function-shared' }; Write-Output ('SELECTION|' + $global:A02R1SelectionValue + '|' + (A02R1SelectionFunction))";
            var selected = await service.ExecuteStructuredScriptAsync(
                requestId,
                generation,
                "A02 Run Selection Integration",
                selectedSource,
                executeInCurrentScope: true,
                _ => { },
                CancellationToken.None,
                sourceInfo,
                EditorExecutionMode.RunSelection,
                directRunSelectionApproved: true).WaitAsync(TimeSpan.FromSeconds(45));

            Assert.Equal(EditorExecutionStatus.Completed, selected.Status);
            Assert.True(selected.CompletionObserved);
            Assert.Equal(processId, service.ProcessId);
            Assert.Equal(generation, service.SessionGeneration);

            var verification = await service.ExecuteStructuredScriptAsync(
                Guid.NewGuid(),
                generation,
                "A02 Run Selection Shared State Verification",
                "Write-Output ('SHARED|' + $global:A02R1SelectionValue + '|' + (A02R1SelectionFunction))",
                executeInCurrentScope: false,
                _ => { },
                CancellationToken.None,
                mode: EditorExecutionMode.ScriptCall).WaitAsync(TimeSpan.FromSeconds(45));

            Assert.Equal(EditorExecutionStatus.Completed, verification.Status);
            Assert.True(service.IsSessionRunning);
            Assert.Contains("SELECTION|selection-shared|function-shared", string.Concat(output), StringComparison.Ordinal);
            Assert.Contains("SHARED|selection-shared|function-shared", string.Concat(output), StringComparison.Ordinal);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
        }
    }
}
