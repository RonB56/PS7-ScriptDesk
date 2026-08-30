using System.Text.Json;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class DocumentRecoveryWorkflowTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk.RecoveryTests-{Guid.NewGuid():N}");
    private readonly string _recoveryDirectory;

    public DocumentRecoveryWorkflowTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _recoveryDirectory = Path.Combine(_testDirectory, "CrashRecovery");
    }

    [Fact]
    public void DirtyNamedDocument_CreatesRecoverableStateWithoutTouchingOriginal()
    {
        var path = CreateFile("named.ps1", "saved");
        var service = CreateRecoveryService();
        var (viewModel, _) = CreateViewModel(service);
        Assert.True(viewModel.TryOpenFileFromPath(path, out _));

        viewModel.SelectedTab!.Content = "dirty";
        viewModel.FlushPendingRecoveryWrites();

        var candidate = Assert.Single(service.GetRecoverableDocuments());
        Assert.Equal(path, candidate.OriginalFilePath);
        Assert.Equal("dirty", candidate.Content);
        Assert.Equal("saved", File.ReadAllText(path));
        Assert.Equal(DocumentRecoveryFileStatus.OriginalUnchanged, candidate.OriginalFileStatus);
    }

    [Fact]
    public void DirtyUntitledDocument_CreatesRecoverableStateWithoutAFilePath()
    {
        var service = CreateRecoveryService();
        var (viewModel, _) = CreateViewModel(service);

        viewModel.SelectedTab!.Content = "untitled dirty";
        viewModel.FlushPendingRecoveryWrites();

        var candidate = Assert.Single(service.GetRecoverableDocuments());
        Assert.Null(candidate.OriginalFilePath);
        Assert.True(candidate.IsUntitled);
        Assert.Equal("untitled dirty", candidate.Content);
    }

    [Fact]
    public void MultipleDirtyDocuments_CreateIndependentRecoveryEntries()
    {
        var firstPath = CreateFile("same.ps1", "first saved");
        var secondFolder = Path.Combine(_testDirectory, "nested");
        Directory.CreateDirectory(secondFolder);
        var secondPath = Path.Combine(secondFolder, "same.ps1");
        File.WriteAllText(secondPath, "second saved");
        var service = CreateRecoveryService();
        var (viewModel, _) = CreateViewModel(service);

        Assert.True(viewModel.TryOpenFileFromPath(firstPath, out _));
        viewModel.SelectedTab!.Content = "first dirty";
        Assert.True(viewModel.TryOpenFileFromPath(secondPath, out _));
        viewModel.SelectedTab!.Content = "second dirty";
        viewModel.NewScriptCommand.Execute(null);
        viewModel.SelectedTab!.Content = "untitled one";
        viewModel.NewScriptCommand.Execute(null);
        viewModel.SelectedTab!.Content = "untitled two";
        viewModel.FlushPendingRecoveryWrites();

        var candidates = service.GetRecoverableDocuments();
        Assert.Equal(4, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.OriginalFilePath == firstPath && candidate.Content == "first dirty");
        Assert.Contains(candidates, candidate => candidate.OriginalFilePath == secondPath && candidate.Content == "second dirty");
        Assert.Equal(2, candidates.Count(static candidate => candidate.IsUntitled));
    }

    [Fact]
    public void UpdatingDirtyDocument_UpdatesRecoverySnapshot()
    {
        var service = CreateRecoveryService();
        var (viewModel, _) = CreateViewModel(service);

        viewModel.SelectedTab!.Content = "first";
        viewModel.FlushPendingRecoveryWrites();
        viewModel.SelectedTab.Content = "second";
        viewModel.FlushPendingRecoveryWrites();

        var candidate = Assert.Single(service.GetRecoverableDocuments());
        Assert.Equal("second", candidate.Content);
    }

    [Fact]
    public void SuccessfulSaveAndSaveAs_RemoveObsoleteRecoveryState()
    {
        var path = CreateFile("save.ps1", "saved");
        var service = CreateRecoveryService();
        var (viewModel, _) = CreateViewModel(service);
        Assert.True(viewModel.TryOpenFileFromPath(path, out _));
        viewModel.SelectedTab!.Content = "dirty named";
        viewModel.FlushPendingRecoveryWrites();

        Assert.True(viewModel.SaveSelectedTab());
        Assert.Empty(service.GetRecoverableDocuments());

        viewModel.SelectedTab!.Content = "dirty again";
        viewModel.FlushPendingRecoveryWrites();
        var saveAsPath = Path.Combine(_testDirectory, "save-as.ps1");
        Assert.True(viewModel.SaveSelectedTabAs(saveAsPath));
        Assert.Empty(service.GetRecoverableDocuments());
        Assert.Equal("dirty again", File.ReadAllText(saveAsPath));
    }

    [Fact]
    public void CleanDiscardRemovesRecoveryButCanceledClosePreservesIt()
    {
        var service = CreateRecoveryService();
        var (viewModel, prompts) = CreateViewModel(service);
        viewModel.SelectedTab!.Content = "keep me";
        viewModel.FlushPendingRecoveryWrites();
        prompts.UnsavedDecisions.Enqueue(UnsavedChangesDecision.Cancel);

        Assert.False(viewModel.TryPrepareForApplicationClose());
        Assert.Single(service.GetRecoverableDocuments());

        prompts.UnsavedDecisions.Enqueue(UnsavedChangesDecision.Discard);
        Assert.True(viewModel.TryPrepareForApplicationClose());
        Assert.Empty(service.GetRecoverableDocuments());
    }

    [Fact]
    public void SimulatedUncleanShutdown_LeavesValidRecoveryForNextStartupRestore()
    {
        var service = CreateRecoveryService();
        var (firstViewModel, _) = CreateViewModel(service);
        firstViewModel.SelectedTab!.Content = "survived";
        firstViewModel.FlushPendingRecoveryWrites();

        var prompts = new FakeUserPromptService();
        prompts.RecoveryDecisions.Enqueue(DocumentRecoveryAction.Restore);
        var restoredViewModel = CreateViewModel(service, prompts).ViewModel;

        Assert.True(restoredViewModel.ProcessStartupDocumentRecovery());
        Assert.Equal("survived", restoredViewModel.SelectedTab!.Content);
        Assert.True(restoredViewModel.SelectedTab.IsDirty);
        Assert.True(restoredViewModel.SelectedTab.IsRecoveredContent);
    }

    [Fact]
    public void StartupDiscard_RemovesOnlySelectedRecoveryAndLeavesOriginalUntouched()
    {
        var firstPath = CreateFile("first.ps1", "first");
        var secondPath = CreateFile("second.ps1", "second");
        var service = CreateRecoveryService();
        service.SaveSnapshot(CreateSnapshot("first-id", firstPath, "first.ps1", "first recovery"));
        service.SaveSnapshot(CreateSnapshot("second-id", secondPath, "second.ps1", "second recovery"));
        var prompts = new FakeUserPromptService();
        prompts.RecoveryDecisions.Enqueue(DocumentRecoveryAction.Discard);
        prompts.RecoveryDecisions.Enqueue(DocumentRecoveryAction.KeepForLater);
        var viewModel = CreateViewModel(service, prompts).ViewModel;

        viewModel.ProcessStartupDocumentRecovery();

        var remaining = Assert.Single(service.GetRecoverableDocuments());
        Assert.Equal("second-id", remaining.RecoveryId);
        Assert.Equal("first", File.ReadAllText(firstPath));
        Assert.Equal("second", File.ReadAllText(secondPath));
    }

    [Fact]
    public void StartupSaveAs_PreservesRecoveredContentToNewFileAndDoesNotOverwriteOriginal()
    {
        var originalPath = CreateFile("original.ps1", "disk");
        var saveAsPath = Path.Combine(_testDirectory, "recovered-copy.ps1");
        var service = CreateRecoveryService();
        service.SaveSnapshot(CreateSnapshot("recover-save-as", originalPath, "original.ps1", "recovered"));
        var prompts = new FakeUserPromptService();
        prompts.RecoveryDecisions.Enqueue(DocumentRecoveryAction.SaveAs);
        prompts.SaveFilePaths.Enqueue(saveAsPath);
        var viewModel = CreateViewModel(service, prompts).ViewModel;

        viewModel.ProcessStartupDocumentRecovery();

        Assert.Equal("disk", File.ReadAllText(originalPath));
        Assert.Equal("recovered", File.ReadAllText(saveAsPath));
        Assert.Empty(service.GetRecoverableDocuments());
        Assert.False(viewModel.SelectedTab!.IsDirty);
    }

    [Fact]
    public void ExternalModificationAfterSnapshot_IsDetectedAfterRestoreAndDoesNotOverwrite()
    {
        var originalPath = CreateFile("conflict.ps1", "baseline");
        var service = CreateRecoveryService();
        service.SaveSnapshot(CreateSnapshot("conflict-id", originalPath, "conflict.ps1", "recovered"));
        File.WriteAllText(originalPath, "external");
        File.SetLastWriteTimeUtc(originalPath, DateTime.UtcNow.AddSeconds(5));
        var prompts = new FakeUserPromptService();
        prompts.RecoveryDecisions.Enqueue(DocumentRecoveryAction.Restore);
        prompts.ExternalDecisions.Enqueue(ExternalFileConflictDecision.Cancel);
        var viewModel = CreateViewModel(service, prompts).ViewModel;

        viewModel.ProcessStartupDocumentRecovery();
        Assert.False(viewModel.SaveSelectedTab());

        Assert.Equal("external", File.ReadAllText(originalPath));
        Assert.Equal(1, prompts.ExternalConflictPromptCount);
        Assert.True(viewModel.SelectedTab!.IsDirty);
    }

    [Fact]
    public void DeletedOriginalFileAfterSnapshot_IsSurfacedAndCanBeRestoredUntitledDirty()
    {
        var originalPath = CreateFile("deleted.ps1", "baseline");
        var service = CreateRecoveryService();
        service.SaveSnapshot(CreateSnapshot("deleted-id", originalPath, "deleted.ps1", "recovered"));
        File.Delete(originalPath);
        var candidate = Assert.Single(service.GetRecoverableDocuments());
        Assert.Equal(DocumentRecoveryFileStatus.OriginalMissing, candidate.OriginalFileStatus);
        var prompts = new FakeUserPromptService();
        prompts.RecoveryDecisions.Enqueue(DocumentRecoveryAction.Restore);
        var viewModel = CreateViewModel(service, prompts).ViewModel;

        viewModel.ProcessStartupDocumentRecovery();

        Assert.Equal("recovered", viewModel.SelectedTab!.Content);
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.False(File.Exists(originalPath));
    }

    [Fact]
    public void CorruptAndUnsupportedRecoveryArtifacts_DoNotBlockValidRecovery()
    {
        Directory.CreateDirectory(_recoveryDirectory);
        File.WriteAllText(Path.Combine(_recoveryDirectory, "bad.ps7recovery.json"), "{not-json");
        File.WriteAllText(Path.Combine(_recoveryDirectory, "unsupported.ps7recovery.json"), JsonSerializer.Serialize(new { SchemaVersion = 99, RecoveryId = "old" }));
        var service = CreateRecoveryService();
        service.SaveSnapshot(new DocumentRecoverySnapshot(
            "valid-id",
            null,
            "Untitled.ps1",
            "valid",
            DateTime.UtcNow,
            null,
            null,
            null,
            true));

        var candidates = service.GetRecoverableDocuments();

        var candidate = Assert.Single(candidates);
        Assert.Equal("valid-id", candidate.RecoveryId);
        Assert.True(Directory.EnumerateFiles(_recoveryDirectory, "*.corrupt").Any());
    }

    [Fact]
    public void RecoveryWriteFailure_DoesNotCrashOrBlockEditing()
    {
        var service = new FailingRecoveryService(_recoveryDirectory);
        var (viewModel, _) = CreateViewModel(service);

        viewModel.SelectedTab!.Content = "dirty";
        var exception = Record.Exception(() => viewModel.FlushPendingRecoveryWrites());

        Assert.Null(exception);
        Assert.True(viewModel.SelectedTab.IsDirty);
        Assert.Equal(1, service.SaveAttempts);
    }

    [Fact]
    public void RecoveryStorageContainsOnlyRecoveryModelAndDocumentText()
    {
        var service = CreateRecoveryService();
        var (viewModel, _) = CreateViewModel(service);
        viewModel.SelectedTab!.Content = "$apiKey = 'document text only'";
        viewModel.FlushPendingRecoveryWrites();

        var recoveryFile = Assert.Single(Directory.EnumerateFiles(_recoveryDirectory, "*.ps7recovery.json"));
        var json = File.ReadAllText(recoveryFile);

        Assert.Contains("document text only", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalDisplayText", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DebuggerOutputText", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentFilePaths", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedSessionRestore_ContinuesToOpenDiskFileWhenNoRecoveryExists()
    {
        var path = CreateFile("session.ps1", "disk session");
        var service = CreateRecoveryService();
        var settings = new ApplicationSettings
        {
            ReopenDocuments =
            [
                new OpenDocumentState
                {
                    FilePath = path,
                    LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path),
                    LastKnownLength = new FileInfo(path).Length
                }
            ]
        };

        var viewModel = CreateViewModel(service, new FakeUserPromptService(), settings).ViewModel;

        Assert.Equal(path, viewModel.SelectedTab!.FilePath);
        Assert.Equal("disk session", viewModel.SelectedTab.Content);
        Assert.False(viewModel.SelectedTab.IsDirty);
        Assert.False(viewModel.ProcessStartupDocumentRecovery());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private DocumentRecoveryService CreateRecoveryService()
        => new(_recoveryDirectory, TimeSpan.FromMilliseconds(250));

    private (MainWindowViewModel ViewModel, FakeUserPromptService Prompts) CreateViewModel(
        IDocumentRecoveryService recoveryService,
        FakeUserPromptService? prompts = null,
        ApplicationSettings? settings = null)
    {
        prompts ??= new FakeUserPromptService();
        var viewModel = new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            prompts,
            new FakeLiveConsoleService(),
            new FakeExeExportService(),
            settings,
            documentRecoveryService: recoveryService);
        return (viewModel, prompts);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static DocumentRecoverySnapshot CreateSnapshot(string recoveryId, string? originalPath, string displayName, string content)
    {
        DateTime? lastWriteTimeUtc = null;
        long? length = null;
        string? hash = null;

        if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
        {
            var fileInfo = new FileInfo(originalPath);
            lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            length = fileInfo.Length;
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(originalPath))));
        }

        return new DocumentRecoverySnapshot(
            recoveryId,
            originalPath,
            displayName,
            content,
            DateTime.UtcNow,
            lastWriteTimeUtc,
            length,
            hash,
            string.IsNullOrWhiteSpace(originalPath));
    }

    private sealed class FailingRecoveryService : IDocumentRecoveryService
    {
        public FailingRecoveryService(string storageDirectory)
        {
            RecoveryStorageDirectory = storageDirectory;
        }

        public string RecoveryStorageDirectory { get; }

        public TimeSpan RecoveryWriteDelay => TimeSpan.FromMilliseconds(250);

        public int SaveAttempts { get; private set; }

        public IReadOnlyList<DocumentRecoveryCandidate> GetRecoverableDocuments()
            => Array.Empty<DocumentRecoveryCandidate>();

        public bool SaveSnapshot(DocumentRecoverySnapshot snapshot)
        {
            SaveAttempts++;
            return false;
        }

        public bool DiscardRecovery(string recoveryId) => false;
    }
}
