using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class DocumentSaveWorkflowTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk.SaveTests-{Guid.NewGuid():N}");

    public DocumentSaveWorkflowTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void Save_UnchangedFile_SucceedsAndUpdatesKnownState()
    {
        var path = CreateFile("normal.ps1", "original");
        var (viewModel, _, liveConsole) = CreateViewModelWithConsole(path);
        liveConsole.IsSessionRunning = true;
        liveConsole.IsCommandInProgress = false;
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";

        var saved = viewModel.SaveSelectedTab();

        Assert.True(saved);
        Assert.Equal("editor", File.ReadAllText(path));
        Assert.False(viewModel.SelectedTab.IsDirty);
        Assert.NotNull(viewModel.SelectedTab.LastKnownFileWriteTimeUtc);
        Assert.Equal(new FileInfo(path).Length, viewModel.SelectedTab.LastKnownFileLength);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.SelectedTab.LastKnownFileContentSha256));
        Assert.Equal("normal.ps1 saved", viewModel.StatusText);
        AssertTerminalUntouched(terminal);
        Assert.True(liveConsole.IsSessionRunning);
        Assert.False(liveConsole.IsCommandInProgress);
        Assert.Equal(0, liveConsole.RawInputWriteCount);
    }

    [Fact]
    public void SaveAs_SucceedsWithoutPublishingToTerminal()
    {
        var originalPath = CreateFile("save-as-source.ps1", "baseline");
        var destinationPath = Path.Combine(_testDirectory, "save-as-destination.ps1");
        var (viewModel, _) = CreateViewModel(originalPath);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";

        var saved = viewModel.SaveSelectedTabAs(destinationPath);

        Assert.True(saved);
        Assert.Equal("editor", File.ReadAllText(destinationPath));
        Assert.Equal(destinationPath, viewModel.SelectedTab.FilePath);
        Assert.Equal("save-as-destination.ps1 saved", viewModel.StatusText);
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void Save_MetadataChangedButContentSame_DoesNotPrompt()
    {
        var path = CreateFile("touched.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        viewModel.SelectedTab!.Content = "editor";
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));

        var saved = viewModel.SaveSelectedTab();

        Assert.True(saved);
        Assert.Equal(0, prompts.ExternalConflictPromptCount);
        Assert.Equal("editor", File.ReadAllText(path));
    }

    [Fact]
    public void Save_ExternalConflictCancel_PreservesDiskEditorAndDirtyState()
    {
        var path = CreateFile("cancel.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(path, "external");
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.Cancel);

        var saved = viewModel.SaveSelectedTab();

        Assert.False(saved);
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Equal("editor", viewModel.SelectedTab.Content);
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.Equal(path, viewModel.SelectedTab.FilePath);
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void Save_ExternalConflictReload_LoadsDiskAndStopsOriginalSave()
    {
        var path = CreateFile("reload.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(path, "external");
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.ReloadFromDisk);

        var saved = viewModel.SaveSelectedTab();

        Assert.False(saved);
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Equal("external", viewModel.SelectedTab.Content);
        Assert.False(viewModel.SelectedTab.IsDirty);
        Assert.Equal(path, viewModel.SelectedTab.FilePath);
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void Save_ExternalConflictOverwrite_ReplacesDiskOnlyAfterDecision()
    {
        var path = CreateFile("overwrite.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(path, "external");
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.OverwriteDisk);

        var saved = viewModel.SaveSelectedTab();

        Assert.True(saved);
        Assert.Equal("editor", File.ReadAllText(path));
        Assert.False(viewModel.SelectedTab.IsDirty);
        Assert.Equal(1, prompts.ExternalConflictPromptCount);
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void Save_ExternalConflictSaveAs_PreservesOriginalAndMovesTabAfterSuccess()
    {
        var originalPath = CreateFile("original.ps1", "baseline");
        var saveAsPath = Path.Combine(_testDirectory, "copy.ps1");
        var (viewModel, prompts) = CreateViewModel(originalPath);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(originalPath, "external");
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.SaveAs);
        prompts.SaveFilePaths.Enqueue(saveAsPath);

        var saved = viewModel.SaveSelectedTab();

        Assert.True(saved);
        Assert.Equal("external", File.ReadAllText(originalPath));
        Assert.Equal("editor", File.ReadAllText(saveAsPath));
        Assert.Equal(saveAsPath, viewModel.SelectedTab.FilePath);
        Assert.False(viewModel.SelectedTab.IsDirty);
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void Save_ExternalConflictSaveAsCanceled_PreservesOriginalPathAndDirtyState()
    {
        var path = CreateFile("save-as-cancel.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(path, "external");
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.SaveAs);
        prompts.SaveFilePaths.Enqueue(null);

        var saved = viewModel.SaveSelectedTab();

        Assert.False(saved);
        Assert.Equal(path, viewModel.SelectedTab.FilePath);
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.Equal("external", File.ReadAllText(path));
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void ApplicationClose_ConflictCanceled_DoesNotContinue()
    {
        var path = CreateFile("close.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(path, "external");
        prompts.UnsavedDecisions.Enqueue(UnsavedChangesDecision.Save);
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.Cancel);

        var mayClose = viewModel.TryPrepareForApplicationClose();

        Assert.False(mayClose);
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.Equal("external", File.ReadAllText(path));
    }

    [Fact]
    public void ApplicationClose_ConflictReloaded_DoesNotContinueOriginalClose()
    {
        var path = CreateFile("close-reload.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        viewModel.SelectedTab!.Content = "editor";
        ChangeExternally(path, "external");
        prompts.UnsavedDecisions.Enqueue(UnsavedChangesDecision.Save);
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.ReloadFromDisk);

        var mayClose = viewModel.TryPrepareForApplicationClose();

        Assert.False(mayClose);
        Assert.False(viewModel.SelectedTab.IsDirty);
        Assert.Equal("external", viewModel.SelectedTab.Content);
    }

    [Fact]
    public void ApplicationClose_SaveFailure_DoesNotContinue()
    {
        var path = CreateFile("close-failure.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        viewModel.SelectedTab!.Content = "editor";
        prompts.UnsavedDecisions.Enqueue(UnsavedChangesDecision.Save);

        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var mayClose = viewModel.TryPrepareForApplicationClose();

        Assert.False(mayClose);
        Assert.True(viewModel.SelectedTab.IsDirty);
    }

    [Fact]
    public void Save_ExternallyDeletedFile_OverwriteDecisionRecreatesItSafely()
    {
        var path = CreateFile("deleted.ps1", "baseline");
        var (viewModel, prompts) = CreateViewModel(path);
        viewModel.SelectedTab!.Content = "editor";
        File.Delete(path);
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.OverwriteDisk);

        var saved = viewModel.SaveSelectedTab();

        Assert.True(saved);
        Assert.Equal("editor", File.ReadAllText(path));
        Assert.False(viewModel.SelectedTab.IsDirty);
    }

    [Fact]
    public void Save_ReplacementFailure_PreservesPriorKnownStateAndDirtyEditor()
    {
        var path = CreateFile("save-failure.ps1", "baseline");
        var (viewModel, _) = CreateViewModel(path);
        var knownWriteTime = viewModel.SelectedTab!.LastKnownFileWriteTimeUtc;
        var knownLength = viewModel.SelectedTab.LastKnownFileLength;
        var knownHash = viewModel.SelectedTab.LastKnownFileContentSha256;
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab.Content = "editor";

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(viewModel.SaveSelectedTab());
        }

        Assert.Equal("baseline", File.ReadAllText(path));
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.Equal(knownWriteTime, viewModel.SelectedTab.LastKnownFileWriteTimeUtc);
        Assert.Equal(knownLength, viewModel.SelectedTab.LastKnownFileLength);
        Assert.Equal(knownHash, viewModel.SelectedTab.LastKnownFileContentSha256);
        AssertTerminalUntouched(terminal);
    }

    [Fact]
    public void SaveAs_ReplacementFailure_PreservesOriginalTabIdentityAndDirtyState()
    {
        var originalPath = CreateFile("source.ps1", "baseline");
        var destinationPath = CreateFile("destination.ps1", "destination");
        var (viewModel, _) = CreateViewModel(originalPath);
        var terminal = AttachTerminalProbe(viewModel);
        viewModel.SelectedTab!.Content = "editor";

        using (var lockStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(viewModel.SaveSelectedTabAs(destinationPath));
        }

        Assert.Equal(originalPath, viewModel.SelectedTab.FilePath);
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.Equal("baseline", File.ReadAllText(originalPath));
        Assert.Equal("destination", File.ReadAllText(destinationPath));
        AssertTerminalUntouched(terminal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private (MainWindowViewModel ViewModel, FakeUserPromptService Prompts) CreateViewModel(string filePath)
    {
        var (viewModel, prompts, _) = CreateViewModelWithConsole(filePath);
        return (viewModel, prompts);
    }

    private (MainWindowViewModel ViewModel, FakeUserPromptService Prompts, FakeLiveConsoleService LiveConsole) CreateViewModelWithConsole(string filePath)
    {
        var prompts = new FakeUserPromptService();
        var liveConsole = new FakeLiveConsoleService();
        var viewModel = new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            prompts,
            liveConsole,
            new FakeExeExportService());

        Assert.True(viewModel.TryOpenFileFromPath(filePath, out var failureReason), failureReason);
        return (viewModel, prompts, liveConsole);
    }

    private static TerminalSinkProbe AttachTerminalProbe(MainWindowViewModel viewModel)
    {
        var probe = new TerminalSinkProbe();
        viewModel.SetTerminalSessionControls(probe.Clear, probe.Focus);
        return probe;
    }

    private static void AssertTerminalUntouched(TerminalSinkProbe probe)
    {
        Assert.Empty(probe.Writes);
        Assert.Equal(0, probe.ClearCount);
        Assert.Equal(0, probe.FocusCount);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void ChangeExternally(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
    }
}

internal sealed class TerminalSinkProbe
{
    public List<string> Writes { get; } = new();
    public int ClearCount { get; private set; }
    public int FocusCount { get; private set; }

    public void Write(string text) => Writes.Add(text);
    public void Clear() => ClearCount++;
    public void Focus() => FocusCount++;
}

internal sealed class FakeUserPromptService : IUserPromptService
{
    public Queue<UnsavedChangesDecision> UnsavedDecisions { get; } = new();
    public Queue<ExternalFileConflictDecision> ExternalDecisions { get; } = new();
    public Queue<string?> SaveFilePaths { get; } = new();
    public int ExternalConflictPromptCount { get; private set; }

    public UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName) =>
        UnsavedDecisions.Count > 0 ? UnsavedDecisions.Dequeue() : UnsavedChangesDecision.Cancel;

    public ExternalFileConflictDecision ShowExternalFileConflictPrompt(string filePath, string conflictReason)
    {
        ExternalConflictPromptCount++;
        return ExternalDecisions.Count > 0 ? ExternalDecisions.Dequeue() : ExternalFileConflictDecision.Cancel;
    }

    public string? ShowSaveFileDialog(string suggestedFileName) => SaveFilePaths.Count > 0 ? SaveFilePaths.Dequeue() : null;
    public string? ShowSaveExecutableDialog(string suggestedFileName) => null;
    public string? ShowOpenFolderDialog() => null;
    public string? ShowOpenPowerShellExecutableDialog() => null;
    public void ShowWarningMessage(string title, string message) { }
}

internal sealed class FakeWorkspaceService : IWorkspaceService
{
    public string GetWorkspaceDisplayText() => "Workspace";
}

internal sealed class FakeRuntimeService : IRuntimeService
{
    public RuntimeDiscoveryResult DiscoverRuntimes() => throw new NotSupportedException();
    public RuntimeDiscoveryResult DiscoverRuntimes(bool requireLaunchValidation) => throw new NotSupportedException();
    public PowerShellRuntimeInfo? TryResolveRuntimeIdentity(string executablePath) => null;
    public RuntimeValidationResult ValidateRuntimePath(string executablePath, string source) => throw new NotSupportedException();
}

internal sealed class FakeWorkspaceFolderService : IWorkspaceFolderService
{
    public WorkspaceFolderLoadResult GetWorkspaceItems(string folderPath, string? filterText = null, bool recursive = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public WorkspaceFolderLoadResult GetWorkspaceChildItems(string workspaceRootPath, string directoryPath, string? filterText = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeExeExportService : IExeExportService
{
    public Task<ExeExportResult> ExportScriptAsExeAsync(
        ExeExportRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ExeExportProgressUpdate>? progress = null) => throw new NotSupportedException();
}

internal sealed class FakeLiveConsoleService : ILiveConsoleService
{
    public bool IsSessionRunning { get; set; }
    public bool IsCommandInProgress { get; set; }
    public bool IsHostAttached => false;
    public PowerShellRuntimeInfo? ActiveRuntime => null;
    public string? CurrentWorkingDirectory => null;
    public int RawInputWriteCount { get; private set; }
    public event Action? ScriptExecutionCompleted { add { } remove { } }
    public event Action? CommandExecutionCompleted { add { } remove { } }
    public event Action? SessionTerminated { add { } remove { } }
    public event Action<int>? TerminalSessionStarted { add { } remove { } }
    public event Action<int>? TerminalSessionStopping { add { } remove { } }
    public event Action<int, string>? RawOutputReceived { add { } remove { } }
    public void AttachHost(IntPtr hostHandle, int width, int height) { }
    public void ResizeHost(int width, int height) { }
    public void ResizeConsole(int cols, int rows) { }
    public void FocusConsole() { }
    public Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
    {
        RawInputWriteCount++;
        return Task.CompletedTask;
    }
    public Task StartSessionAsync(PowerShellRuntimeInfo runtime, Action<ExecutionOutputRecord> onOutput, string? startupWorkingDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(string commandText, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveConsoleCommandResult> ExecuteScriptAsync(string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null) => Task.FromResult(true);
    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task SendInterruptAsync() => Task.CompletedTask;
    public Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(Action<ExecutionOutputRecord>? onOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { }
}
