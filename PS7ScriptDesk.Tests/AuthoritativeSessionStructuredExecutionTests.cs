using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class AuthoritativeSessionStructuredExecutionTests
{
    [Fact]
    public async Task AdapterUsesAuthoritativeGenerationAndReturnsOneStructuredResult()
    {
        var session = new FakeAuthoritativeSession();
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(session, new EditorExecutionFeatureGate(true));
        var sourceInfo = new EditorExecutionSourceInfo(Guid.NewGuid(), 12, 25, 40);
        var request = new EditorExecutionRequest(Guid.NewGuid(), session.SessionGeneration, EditorExecutionMode.RunSelection, "Test.ps1 (selection)", "Write-Output proof", ExecuteInCurrentScope: true, SourceInfo: sourceInfo);

        var result = await adapter.ExecuteAsync(request);

        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.Equal(AuthoritativeSessionStructuredExecutionBackend.BackendName, result.BackendId);
        Assert.Single(result.Outputs);
        Assert.Equal(session.ProcessId, session.LastProcessId);
        Assert.Equal(request.SessionGeneration, session.LastGeneration);
        Assert.Equal(request.RequestId, session.LastRequestId);
        Assert.Same(sourceInfo, session.LastSourceInfo);
        Assert.Equal(EditorExecutionMode.RunSelection, session.LastMode);
        Assert.True(session.LastExecuteInCurrentScope);
        Assert.Equal(1, session.InvocationCount);
    }

    [Fact]
    public async Task AdapterPassesOnlyExplicitlyCleanSavedScriptPath()
    {
        var session = new FakeAuthoritativeSession();
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(session, new EditorExecutionFeatureGate(true));
        var path = Path.Combine(Path.GetTempPath(), "SavedScript.ps1");
        var cleanRequest = new EditorExecutionRequest(Guid.NewGuid(), session.SessionGeneration,
            EditorExecutionMode.ScriptCall, "SavedScript.ps1", "Write-Output 'saved'", path, IsSavedClean: true);
        var dirtyRequest = cleanRequest with { RequestId = Guid.NewGuid(), IsSavedClean = false };

        await adapter.ExecuteAsync(cleanRequest);
        Assert.Equal(path, session.LastSavedScriptPath);

        await adapter.ExecuteAsync(dirtyRequest);
        Assert.Null(session.LastSavedScriptPath);
    }

    [Fact]
    public async Task AdapterRejectsStaleGenerationWithoutSubmittingScript()
    {
        var session = new FakeAuthoritativeSession();
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(session, new EditorExecutionFeatureGate(true));
        var request = new EditorExecutionRequest(Guid.NewGuid(), session.SessionGeneration + 1, EditorExecutionMode.RunSelection, "Selection", "$proof");

        var result = await adapter.ExecuteAsync(request);

        Assert.Equal(EditorExecutionStatus.Rejected, result.Status);
        Assert.Equal(0, session.InvocationCount);
    }

    [Fact]
    public void BackendAdvertisesAuthoritativeIdentityAndCancellation()
    {
        var backend = new AuthoritativeSessionStructuredExecutionBackend(
            new AuthoritativeSessionStructuredExecutionAdapter(new FakeAuthoritativeSession(), new EditorExecutionFeatureGate(true)));

        Assert.Equal("authoritative-pses-session", backend.BackendId);
        Assert.True(backend.Capabilities.SupportsCurrentScope);
        Assert.True(backend.Capabilities.SupportsCancellation);
        Assert.True(backend.Capabilities.SupportsTerminalInterrupt);
    }

    private sealed class FakeAuthoritativeSession : IAuthoritativeSessionStructuredExecution
    {
        public bool IsSessionRunning => true;
        public int? ProcessId => 45620;
        public int SessionGeneration => 7;
        public string? CurrentWorkingDirectory => Environment.CurrentDirectory;
        public int InvocationCount { get; private set; }
        public int? LastProcessId { get; private set; }
        public int LastGeneration { get; private set; }
        public Guid LastRequestId { get; private set; }
        public EditorExecutionSourceInfo? LastSourceInfo { get; private set; }
        public EditorExecutionMode LastMode { get; private set; }
        public bool LastExecuteInCurrentScope { get; private set; }
        public string? LastSavedScriptPath { get; private set; }

        public Task<AuthoritativeStructuredExecutionResult> ExecuteStructuredScriptAsync(
            Guid requestId,
            int sessionGeneration,
            string documentDisplayName,
            string scriptContent,
            bool executeInCurrentScope,
            Action<ExecutionOutputRecord> onOutput,
            CancellationToken cancellationToken = default,
            EditorExecutionSourceInfo? sourceInfo = null,
            EditorExecutionMode mode = EditorExecutionMode.ScriptCall,
            bool directRunSelectionApproved = false,
            string? savedScriptPath = null)
        {
            InvocationCount++;
            LastRequestId = requestId;
            LastGeneration = sessionGeneration;
            LastProcessId = ProcessId;
            LastSourceInfo = sourceInfo;
            LastMode = mode;
            LastExecuteInCurrentScope = executeInCurrentScope;
            LastSavedScriptPath = savedScriptPath;
            onOutput(new ExecutionOutputRecord(ExecutionOutputStreamKind.StandardOutput, "authoritative-proof\r\n", DateTime.UtcNow));
            return Task.FromResult(new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Completed, CurrentWorkingDirectory, null, true));
        }
    }
}
