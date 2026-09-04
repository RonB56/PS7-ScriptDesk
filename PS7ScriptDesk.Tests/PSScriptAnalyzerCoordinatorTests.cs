using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.PowerShell.Services;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class PSScriptAnalyzerCoordinatorTests
{
    [Fact]
    public async Task RealWorkerResultIsNormalizedAndPublishedWithoutErasingOtherSources()
    {
        var repositoryRoot = FindRepositoryRoot();
        var module = Path.Combine(repositoryRoot, "PS7ScriptDesk.PowerShell", "Dependencies", "PSScriptAnalyzer", "1.25.0", "PSScriptAnalyzer.psd1");
        var runtime = Environment.GetEnvironmentVariable("PS7_SCRIPT_DESK_TEST_PWSH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe");
        if (!File.Exists(runtime) || !File.Exists(module))
        {
            return;
        }

        var document = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var store = new ScriptDiagnosticStore();
        store.ReplaceDiagnostics(document, ScriptDiagnosticSource.Parser, 1, new[] { Diagnostic(document, ScriptDiagnosticSource.Parser, 1, "parser") });
        store.ReplaceDiagnostics(document, ScriptDiagnosticSource.Authoring, 1, new[] { Diagnostic(document, ScriptDiagnosticSource.Authoring, 1, "authoring") });
        await using var service = new PSScriptAnalyzerService(runtime, module);
        var coordinator = new PSScriptAnalyzerDiagnosticsCoordinator(service, store);

        Assert.True(await coordinator.AnalyzeAndPublishAsync(new PSScriptAnalyzerRequest("real-phase2", document.ToString(), 1, "script.ps1", "Invoke-Expression 'Get-Date'")));

        var diagnostics = store.GetDiagnostics(document);
        Assert.Contains(diagnostics, diagnostic => diagnostic.SourceId == ScriptDiagnosticSource.PSScriptAnalyzer && diagnostic.RuleId is not null && diagnostic.DocumentRevision == 1);
        Assert.Contains(diagnostics, diagnostic => diagnostic.SourceId == ScriptDiagnosticSource.Parser);
        Assert.Contains(diagnostics, diagnostic => diagnostic.SourceId == ScriptDiagnosticSource.Authoring);
    }

    [Fact]
    public async Task AnalyzeAndPublish_ReplacesOnlyAnalyzerSourceAndSupportsEmptySuccess()
    {
        var document = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new ScriptDiagnosticStore();
        store.ReplaceDiagnostics(document, ScriptDiagnosticSource.Parser, 1, new[] { Diagnostic(document, ScriptDiagnosticSource.Parser, 1, "parser") });
        store.ReplaceDiagnostics(document, ScriptDiagnosticSource.Authoring, 1, new[] { Diagnostic(document, ScriptDiagnosticSource.Authoring, 1, "authoring") });
        var service = new FakeService(new PSScriptAnalyzerResult("r1", new[] { new PSScriptAnalyzerFinding("rule", "analyzer", "Warning", 1, 1) }));
        var coordinator = new PSScriptAnalyzerDiagnosticsCoordinator(service, store);

        Assert.True(await coordinator.AnalyzeAndPublishAsync(new PSScriptAnalyzerRequest("r1", document.ToString(), 1, null, "x")));
        Assert.Equal(new[] { "parser", "authoring", "analyzer" }, store.GetDiagnostics(document).Select(diagnostic => diagnostic.Message));

        service.Result = new PSScriptAnalyzerResult("r2", Array.Empty<PSScriptAnalyzerFinding>());
        Assert.True(await coordinator.AnalyzeAndPublishAsync(new PSScriptAnalyzerRequest("r2", document.ToString(), 2, null, "x")));
        Assert.Equal(new[] { "parser", "authoring" }, store.GetDiagnostics(document).Select(diagnostic => diagnostic.Message));
    }

    [Fact]
    public async Task FailureCancellationAndStaleRevisionDoNotCorruptExistingFindings()
    {
        var document = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var store = new ScriptDiagnosticStore();
        var existing = Diagnostic(document, ScriptDiagnosticSource.PSScriptAnalyzer, 3, "existing");
        store.ReplaceDiagnostics(document, ScriptDiagnosticSource.PSScriptAnalyzer, 3, new[] { existing });
        var service = new FakeService(new PSScriptAnalyzerResult("r1", Array.Empty<PSScriptAnalyzerFinding>())) { Error = "failed" };
        var coordinator = new PSScriptAnalyzerDiagnosticsCoordinator(service, store);

        Assert.False(await coordinator.AnalyzeAndPublishAsync(new PSScriptAnalyzerRequest("r1", document.ToString(), 4, null, "x")));
        Assert.Equal("existing", Assert.Single(store.GetDiagnostics(document)).Message);
        service.Error = null;
        service.Result = new PSScriptAnalyzerResult("other", Array.Empty<PSScriptAnalyzerFinding>());
        Assert.False(await coordinator.AnalyzeAndPublishAsync(new PSScriptAnalyzerRequest("r1", document.ToString(), 2, null, "x")));
        Assert.Equal("existing", Assert.Single(store.GetDiagnostics(document)).Message);
    }

    private static ScriptDiagnostic Diagnostic(Guid document, ScriptDiagnosticSource source, long revision, string message)
        => new(document, revision, source, null, message, ScriptDiagnosticSeverity.Warning, null, 1, 1, 1, 2, RequestId: "r");

    private sealed class FakeService : IPSScriptAnalyzerService
    {
        public FakeService(PSScriptAnalyzerResult result) => Result = result;
        public PSScriptAnalyzerResult Result { get; set; }
        public string? Error { get; set; }
        public string? BundledAnalyzerVersion => "1.25.0";
        public Task<PSScriptAnalyzerResult> AnalyzeAsync(PSScriptAnalyzerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Error is null ? Result : Result with { RequestId = request.RequestId, Error = Error });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
