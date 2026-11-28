using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class RunFileSnapshotBehaviorTests
{
    [Fact]
    public async Task UnsavedSnapshotUsesPrivateTempRegardlessOfConsoleWorkingDirectoryAndPreservesUtf8Text()
    {
        var requestId = Guid.NewGuid();
        var systemDirectory = OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/";
        var expectedDirectory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution");

        var selectedDirectory = PsesOwnedInteractiveSessionService.GetRunFileSnapshotDirectory(null, systemDirectory);
        var afterWorkingDirectoryChange = PsesOwnedInteractiveSessionService.GetRunFileSnapshotDirectory(null, Path.GetTempPath());
        Assert.Equal(expectedDirectory, selectedDirectory);
        Assert.Equal(expectedDirectory, afterWorkingDirectoryChange);

        var text = "# π\r\nWrite-Output 'snapshot'\n";
        var path = await PsesOwnedInteractiveSessionService.CreateRunFileSnapshotAsync(
            selectedDirectory, applicationOwnedDirectory: true, requestId, text);
        try
        {
            Assert.Equal(Path.Combine(expectedDirectory, $".ps7sd-editor-{requestId:N}.ps1"), path);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Equal(text, Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MatchingSavedFileUsesOriginalAndDirtyFileStagesBesideIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-RunFileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var savedPath = Path.Combine(directory, "saved.ps1");
        const string savedText = "Write-Output 'saved'";
        await File.WriteAllTextAsync(savedPath, savedText, new UTF8Encoding(false));
        try
        {
            var matchingPath = PsesOwnedInteractiveSessionService.TryOpenMatchingSavedScript(savedPath, savedText, out var readLock);
            using (readLock)
                Assert.Equal(Path.GetFullPath(savedPath), matchingPath);

            var adjacentDirectory = PsesOwnedInteractiveSessionService.GetRunFileSnapshotDirectory(savedPath, Environment.SystemDirectory);
            Assert.Equal(directory, adjacentDirectory);
            var snapshot = await PsesOwnedInteractiveSessionService.CreateRunFileSnapshotAsync(
                adjacentDirectory, applicationOwnedDirectory: false, Guid.NewGuid(), "Write-Output 'dirty'");
            try
            {
                Assert.Equal(directory, Path.GetDirectoryName(snapshot));
            }
            finally
            {
                File.Delete(snapshot);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAdjacentDirectoryFailsWithoutCreatingItOrFallingBackToTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-RunFileTests", Guid.NewGuid().ToString("N"));
        var missingDirectory = Path.Combine(root, "missing");
        var savedPath = Path.Combine(missingDirectory, "dirty.ps1");

        var requestId = Guid.NewGuid();
        Assert.Throws<DirectoryNotFoundException>(() =>
            PsesOwnedInteractiveSessionService.GetRunFileSnapshotDirectory(savedPath, Environment.SystemDirectory));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            PsesOwnedInteractiveSessionService.CreateRunFileSnapshotAsync(
                missingDirectory, applicationOwnedDirectory: false, requestId, "Write-Output 'dirty'"));
        Assert.False(Directory.Exists(missingDirectory));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution", $".ps7sd-editor-{requestId:N}.ps1")));
    }

    [Fact(Timeout = 120_000)]
    public async Task BridgeStagingRejectionPreservesSessionAndSuccessfulUnsavedRunCleansSnapshot()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "PSES");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("Run File bridge lifecycle verification requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "03C4-A-RunFile", true, false, true, true);
        var workingDirectory = OperatingSystem.IsWindows() ? Environment.SystemDirectory : Path.GetTempPath();
        using var service = new PsesOwnedInteractiveSessionService(
            editorBridgeFeatureGate: new PsesEditorBridgeFeatureGate(isEnabled: true));
        var output = new ConcurrentQueue<string>();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution");
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), workingDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            var missingSavedPath = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-RunFileTests", Guid.NewGuid().ToString("N"), "dirty.ps1");
            var rejected = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "dirty saved document", "Write-Output 'must not run'", executeInCurrentScope: false,
                _ => { }, mode: EditorExecutionMode.ScriptCall, savedScriptPath: missingSavedPath)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(EditorExecutionStatus.Rejected, rejected.Status);
            Assert.False(rejected.CompletionObserved);
            Assert.False(rejected.RecoveryRequired);
            Assert.True(service.IsSessionRunning);
            Assert.True(service.IsInputWritable);
            Assert.False(service.IsCommandInProgress);
            Assert.DoesNotContain("must not run", string.Concat(output), StringComparison.Ordinal);

            var requestId = Guid.NewGuid();
            var completed = await service.ExecuteStructuredScriptAsync(requestId, service.SessionGeneration,
                "unsaved document", "$pwdBefore = (Get-Location).Path; Write-Output ('RUNFILE_PATHS|' + $pwdBefore + '|' + $PSScriptRoot)",
                executeInCurrentScope: false, record => output.Enqueue(record.Text),
                mode: EditorExecutionMode.ScriptCall)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(EditorExecutionStatus.Completed, completed.Status);
            Assert.True(completed.CompletionObserved);
            var observed = string.Concat(output);
            Assert.Contains("RUNFILE_PATHS|" + Path.GetFullPath(workingDirectory), observed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(tempDirectory), observed, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(tempDirectory, $".ps7sd-editor-{requestId:N}.ps1")));
            Assert.True(service.IsSessionRunning);
            Assert.True(service.IsInputWritable);

            var selection = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "selection", "Write-Output 'selection-still-works'", executeInCurrentScope: true,
                record => output.Enqueue(record.Text), mode: EditorExecutionMode.RunSelection)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(EditorExecutionStatus.Completed, selection.Status);
            Assert.Contains("selection-still-works", string.Concat(output), StringComparison.Ordinal);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
        }
    }
}
