using System.Text.Json;
using System.IO;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.ViewModels;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PSScriptAnalyzerPhase3Tests
{
    [Fact]
    public void EditorDocumentIdentityAdvancesOnlyWhenContentChanges()
    {
        var tab = new EditorTabViewModel("untitled.ps1", "Write-Output one");
        Assert.Equal(0, tab.DiagnosticDocument.Revision);
        tab.UpdateContentFromEditor("Write-Output one", 1);
        Assert.Equal(0, tab.DiagnosticDocument.Revision);
        tab.UpdateContentFromEditor("Write-Output two", 1);
        tab.UpdateContentFromEditor("Write-Output three", 1);
        Assert.Equal(2, tab.DiagnosticDocument.Revision);
        tab.SetFilePath("saved.ps1");
        Assert.Equal(2, tab.DiagnosticDocument.Revision);
    }

    [Fact]
    public void UnsavedDocumentsHaveDistinctStableIdentities()
    {
        var first = new EditorTabViewModel("Untitled 1", string.Empty);
        var second = new EditorTabViewModel("Untitled 2", string.Empty);
        Assert.NotEqual(first.DiagnosticDocument.DocumentId, second.DiagnosticDocument.DocumentId);
        Assert.Equal(first.DiagnosticDocument.DocumentId, first.DiagnosticDocument.Capture().DocumentId);
    }

    [Theory]
    [InlineData("abc", 1, 1, 0, 0)]
    [InlineData("abc", 1, 99, 3, 3)]
    [InlineData("one\ntwo", 2, 1, 4, 4)]
    [InlineData("one\r\ntwo", 1, 4, 3, 3)]
    public void DiagnosticRangesAreClampedToUtf16EditorOffsets(string text, int startLine, int startColumn, int expectedStart, int expectedEnd)
    {
        var range = ScriptDiagnosticRangeMapper.Map(text, startLine, startColumn, startLine, startColumn + 1);
        Assert.Equal(expectedStart, range.StartOffset);
        Assert.InRange(range.EndOffset, expectedEnd, text.Length);
    }

    [Fact]
    public void AnalyzerSettingsHaveConservativePersistedDefaults()
    {
        var settings = new ApplicationSettings();
        var roundTrip = JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(roundTrip.PSScriptAnalyzerEnabled);
        Assert.Equal("All", roundTrip.PSScriptAnalyzerSeverityFilter);
    }

    [Fact]
    public void DiagnosticProjectionPreservesStructuredSourceAndRule()
    {
        var span = new EditorDiagnosticSpanViewModel(2, 3, "Avoid Write-Host", 4, 14, "Warning", "PSScriptAnalyzer", "PSAvoidUsingWriteHost");
        var row = new SyntaxErrorViewModel(2, 3, span.Message, span.StartOffset, span.EndOffset, span.Severity, span.SourceId, span.RuleId);
        Assert.Equal("PSScriptAnalyzer", row.SourceId);
        Assert.Equal("PSAvoidUsingWriteHost", row.RuleId);
    }

    [Fact]
    public void Phase3AUsesVisibleAnalyzeCommandAndExistingProblemsSurface()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "MainWindow.xaml.cs"));
        Assert.Contains("Analyze Current Document", xaml);
        Assert.Contains("AnalyzeCurrentDocument_Click", xaml);
        Assert.Contains("diagnostics.analyzeCurrentDocument", code);
        Assert.Contains("Show _Problems", xaml);
        Assert.DoesNotContain("ScriptAnalyzerPane", xaml);
    }

    [Fact]
    public async Task LiveSchedulerCollapsesRapidRevisionsAndDispatchesOnlyLatest()
    {
        var requests = new List<PSScriptAnalyzerRequest>();
        using var scheduler = new PSScriptAnalyzerLiveAnalysisScheduler((request, _) =>
        {
            lock (requests) requests.Add(request);
            return Task.FromResult(true);
        }, TimeSpan.FromMilliseconds(40));
        var documentId = Guid.NewGuid();
        scheduler.Schedule(documentId, 1, null, "one", "All");
        scheduler.Schedule(documentId, 2, null, "two", "All");
        scheduler.Schedule(documentId, 3, null, "three", "All");
        await Task.Delay(140);
        Assert.Single(requests);
        Assert.Equal(3, requests[0].Revision);
        Assert.Equal(1, scheduler.DispatchedRequestCount);
    }

    [Fact]
    public async Task LiveSchedulerCanCancelPendingWorkWithoutDispatch()
    {
        var dispatched = 0;
        using var scheduler = new PSScriptAnalyzerLiveAnalysisScheduler((_, _) => { dispatched++; return Task.FromResult(true); }, TimeSpan.FromMilliseconds(80));
        var documentId = Guid.NewGuid();
        scheduler.Schedule(documentId, 1, null, "one", "All");
        scheduler.Cancel(documentId);
        await Task.Delay(140);
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public async Task LiveSchedulerPublishesARealBundledWorkerFinding()
    {
        var root = FindRepositoryRoot();
        var module = Path.Combine(root, "PS7ScriptDesk.PowerShell", "Dependencies", "PSScriptAnalyzer", "1.25.0", "PSScriptAnalyzer.psd1");
        var runtime = Environment.GetEnvironmentVariable("PS7_SCRIPT_DESK_TEST_PWSH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe");
        if (!File.Exists(runtime) || !File.Exists(module)) return;

        var identity = new ScriptDocumentIdentity();
        var revision = identity.AdvanceRevision();
        var store = new ScriptDiagnosticStore();
        await using var service = new PSScriptAnalyzerService(runtime, module);
        var coordinator = new PSScriptAnalyzerDiagnosticsCoordinator(service, store);
        using var completed = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var scheduler = new PSScriptAnalyzerLiveAnalysisScheduler((request, token) => coordinator.AnalyzeAndPublishAsync(request, token), TimeSpan.FromMilliseconds(20));
        scheduler.Schedule(identity.DocumentId, revision, "live.ps1", "Invoke-Expression 'Get-Date'", "All");
        while (store.GetDiagnostics(identity.DocumentId, ScriptDiagnosticSource.PSScriptAnalyzer).Count == 0 && !completed.IsCancellationRequested)
            await Task.Delay(50, completed.Token);
        Assert.Contains(store.GetDiagnostics(identity.DocumentId, ScriptDiagnosticSource.PSScriptAnalyzer), diagnostic => diagnostic.RuleId is not null && diagnostic.DocumentRevision == revision);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
