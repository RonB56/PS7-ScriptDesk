using System.Collections.Concurrent;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class ExeExportWorkflowTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7 Export Workflow {Guid.NewGuid():N}");

    public ExeExportWorkflowTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task SaveExecutableDialogCanceled_DoesNotStartExport()
    {
        var sourcePath = CreateSource("Canceled.ps1");
        var prompts = new ExportWorkflowPromptService();
        var exporter = new RecordingExeExportService();
        var viewModel = CreateViewModel(sourcePath, prompts, exporter);

        await viewModel.ExportSelectedTabAsExeAsync();

        Assert.Null(exporter.Request);
        Assert.Empty(exporter.ProgressUpdates);
    }

    [Fact]
    public async Task WizardCancel_DoesNotInvokeLegacyPromptOrExporter()
    {
        await AssertCanceledWizardStopsExportAsync("WizardCancel.ps1");
    }

    [Fact]
    public async Task WizardWindowClose_DoesNotInvokeLegacyPromptOrExporter()
    {
        await AssertCanceledWizardStopsExportAsync("WizardWindowClose.ps1");
    }

    [Fact]
    public async Task WizardEscape_DoesNotInvokeLegacyPromptOrExporter()
    {
        await AssertCanceledWizardStopsExportAsync("WizardEscape.ps1");
    }

    [Fact]
    public async Task WizardConfirmed_UsesWizardOutputWithoutLegacyPrompt()
    {
        var sourcePath = CreateSource("WizardConfirmed.ps1");
        var outputPath = Path.Combine(_testDirectory, "Wizard Output.exe");
        var prompts = new ExportWorkflowPromptService { ExecutablePath = Path.Combine(_testDirectory, "Legacy Output.exe") };
        var exporter = new RecordingExeExportService(request => SuccessfulResult(request.OutputExecutablePath));
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.PortableWindowsExe, "WizardConfirmed");
        configuration.OutputExecutablePath = outputPath;
        var wizard = new RecordingWizardService(configuration);
        var viewModel = CreateViewModel(sourcePath, prompts, exporter, wizard);

        await viewModel.ExportSelectedTabAsExeAsync();

        Assert.Equal(1, wizard.ShowCallCount);
        Assert.Equal(0, prompts.ShowSaveExecutableDialogCallCount);
        Assert.NotNull(exporter.Request);
        Assert.Equal(outputPath, exporter.Request!.OutputExecutablePath);
    }

    [Fact]
    public async Task WizardConfigurationWithoutOutput_DoesNotFallThroughToLegacyPrompt()
    {
        var sourcePath = CreateSource("WizardMissingOutput.ps1");
        var prompts = new ExportWorkflowPromptService { ExecutablePath = Path.Combine(_testDirectory, "Legacy Output.exe") };
        var exporter = new RecordingExeExportService();
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.PortableWindowsExe, "WizardMissingOutput");
        var wizard = new RecordingWizardService(configuration);
        var viewModel = CreateViewModel(sourcePath, prompts, exporter, wizard);

        await viewModel.ExportSelectedTabAsExeAsync();

        Assert.Equal(1, wizard.ShowCallCount);
        Assert.Equal(0, prompts.ShowSaveExecutableDialogCallCount);
        Assert.Null(exporter.Request);
        Assert.Empty(exporter.ProgressUpdates);
    }

    [Fact]
    public async Task DestinationConfirmed_InvokesExporterAndRetainsSpacedPaths()
    {
        var sourcePath = CreateSource("Source Script.ps1");
        var outputPath = Path.Combine(_testDirectory, "Output Folder", "My Export.exe");
        var prompts = new ExportWorkflowPromptService { ExecutablePath = outputPath };
        var exporter = new RecordingExeExportService(request => SuccessfulResult(request.OutputExecutablePath));
        var viewModel = CreateViewModel(sourcePath, prompts, exporter);

        await viewModel.ExportSelectedTabAsExeAsync();

        Assert.Equal(1, prompts.ShowSaveExecutableDialogCallCount);
        Assert.NotNull(exporter.Request);
        Assert.Equal(sourcePath, exporter.Request!.SourceScriptPath);
        Assert.Equal(outputPath, exporter.Request.OutputExecutablePath);
        Assert.Contains(" ", exporter.Request.SourceScriptPath);
        Assert.Contains(" ", exporter.Request.OutputExecutablePath);
    }

    [Fact]
    public async Task SuccessfulExport_ReportsCompletionAndOutputPath()
    {
        var sourcePath = CreateSource("Success.ps1");
        var outputPath = Path.Combine(_testDirectory, "Success.exe");
        var prompts = new ExportWorkflowPromptService { ExecutablePath = outputPath };
        var exporter = new RecordingExeExportService(request => SuccessfulResult(request.OutputExecutablePath));
        var viewModel = CreateViewModel(sourcePath, prompts, exporter);
        var updates = Subscribe(viewModel);

        await viewModel.ExportSelectedTabAsExeAsync();

        var completion = Assert.Single(updates, update => update.IsCompleted);
        Assert.True(completion.Succeeded);
        Assert.Equal(outputPath, completion.OutputExecutablePath);
        Assert.Equal("Complete", completion.Stage);
    }

    [Fact]
    public async Task MissingExportedExecutable_ReachesUiAsFailure()
    {
        var sourcePath = CreateSource("MissingOutput.ps1");
        var outputPath = Path.Combine(_testDirectory, "MissingOutput.exe");
        var prompts = new ExportWorkflowPromptService { ExecutablePath = outputPath };
        var exporter = new RecordingExeExportService(request => new ExeExportResult(
            succeeded: false,
            outputExecutablePath: request.OutputExecutablePath,
            summaryMessage: "The export did not create the expected executable file.",
            detailedLog: "The packaging process reported success, but the expected file was absent."));
        var viewModel = CreateViewModel(sourcePath, prompts, exporter);
        var updates = Subscribe(viewModel);

        await viewModel.ExportSelectedTabAsExeAsync();

        var completion = Assert.Single(updates, update => update.IsCompleted);
        Assert.False(completion.Succeeded);
        Assert.Contains("did not create", completion.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(outputPath, completion.OutputExecutablePath);
    }

    [Fact]
    public async Task ExporterException_ReachesUiAsControlledFailure()
    {
        var sourcePath = CreateSource("Exception.ps1");
        var outputPath = Path.Combine(_testDirectory, "Exception.exe");
        var prompts = new ExportWorkflowPromptService { ExecutablePath = outputPath };
        var exporter = new RecordingExeExportService(_ => throw new IOException("Injected packaging failure."));
        var viewModel = CreateViewModel(sourcePath, prompts, exporter);
        var updates = Subscribe(viewModel);

        await viewModel.ExportSelectedTabAsExeAsync();

        var completion = Assert.Single(updates, update => update.IsCompleted);
        Assert.False(completion.Succeeded);
        Assert.Equal("Failed", completion.Stage);
        Assert.Contains("failed unexpectedly", completion.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Injected packaging failure", completion.DetailedLog, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private MainWindowViewModel CreateViewModel(
        string sourcePath,
        ExportWorkflowPromptService prompts,
        RecordingExeExportService exporter,
        IExeExportWizardService? wizard = null)
    {
        var runtime = new PowerShellRuntimeInfo(
            "PowerShell 7 test",
            "Core",
            "7.0.0",
            new Version(7, 0),
            "x64",
            Environment.ProcessPath!,
            "test",
            isPowerShell7OrLater: true,
            isWindowsPowerShell: false,
            isPreferred: true);
        var viewModel = new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            prompts,
            new FakeLiveConsoleService(),
            exporter,
            startupRuntimeInfo: runtime,
            exeExportWizardService: wizard);

        Assert.True(viewModel.TryOpenFileFromPath(sourcePath, out var failureReason), failureReason);
        return viewModel;
    }

    private async Task AssertCanceledWizardStopsExportAsync(string sourceFileName)
    {
        var sourcePath = CreateSource(sourceFileName);
        var prompts = new ExportWorkflowPromptService { ExecutablePath = Path.Combine(_testDirectory, "Legacy Output.exe") };
        var exporter = new RecordingExeExportService();
        var wizard = new RecordingWizardService(null);
        var viewModel = CreateViewModel(sourcePath, prompts, exporter, wizard);

        await viewModel.ExportSelectedTabAsExeAsync();

        Assert.Equal(1, wizard.ShowCallCount);
        Assert.Equal(0, prompts.ShowSaveExecutableDialogCallCount);
        Assert.Null(exporter.Request);
        Assert.Empty(exporter.ProgressUpdates);
    }

    private string CreateSource(string fileName)
    {
        var sourcePath = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(sourcePath, "Write-Output 'export workflow test'");
        return sourcePath;
    }

    private static List<ExeExportProgressUpdate> Subscribe(MainWindowViewModel viewModel)
    {
        var updates = new List<ExeExportProgressUpdate>();
        viewModel.ExeExportProgressChanged += (_, update) => updates.Add(update);
        return updates;
    }

    private static ExeExportResult SuccessfulResult(string outputPath) => new(
        succeeded: true,
        outputExecutablePath: outputPath,
        summaryMessage: "Export as EXE completed successfully.",
        detailedLog: "The generated executable was verified.");

    private sealed class ExportWorkflowPromptService : IUserPromptService
    {
        public string? ExecutablePath { get; init; }
        public int ShowSaveExecutableDialogCallCount { get; private set; }
        public UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName) => UnsavedChangesDecision.Cancel;
        public ExternalFileConflictDecision ShowExternalFileConflictPrompt(string filePath, string conflictReason) => ExternalFileConflictDecision.Cancel;
        public DocumentRecoveryAction ShowDocumentRecoveryPrompt(DocumentRecoveryCandidate recoveryCandidate) => DocumentRecoveryAction.KeepForLater;
        public string? ShowSaveFileDialog(string suggestedFileName) => null;
        public string? ShowSaveExecutableDialog(string suggestedFileName)
        {
            ShowSaveExecutableDialogCallCount++;
            return ExecutablePath;
        }
        public string? ShowOpenFolderDialog() => null;
        public string? ShowOpenPowerShellExecutableDialog() => null;
        public void ShowWarningMessage(string title, string message) { }
    }

    private sealed class RecordingWizardService : IExeExportWizardService
    {
        private readonly ExeExportConfiguration? _result;

        public RecordingWizardService(ExeExportConfiguration? result) => _result = result;

        public int ShowCallCount { get; private set; }

        public ExeExportConfiguration? ShowWizard(ExeExportWizardRequest request)
        {
            ShowCallCount++;
            return _result;
        }
    }

    private sealed class RecordingExeExportService : IExeExportService
    {
        private readonly Func<ExeExportRequest, ExeExportResult> _resultFactory;

        public RecordingExeExportService(Func<ExeExportRequest, ExeExportResult>? resultFactory = null)
        {
            _resultFactory = resultFactory ?? (request => SuccessfulResult(request.OutputExecutablePath));
        }

        public ExeExportRequest? Request { get; private set; }
        public ConcurrentQueue<ExeExportProgressUpdate> ProgressUpdates { get; } = new();

        public Task<ExeExportResult> ExportScriptAsExeAsync(
            ExeExportRequest request,
            CancellationToken cancellationToken = default,
            IProgress<ExeExportProgressUpdate>? progress = null)
        {
            Request = request;
            var update = new ExeExportProgressUpdate("CompilingPackage", "Compiling and packaging the executable.", isIndeterminate: true);
            ProgressUpdates.Enqueue(update);
            progress?.Report(update);
            return Task.FromResult(_resultFactory(request));
        }
    }
}
