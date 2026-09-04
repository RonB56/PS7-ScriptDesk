using System.Text;
using System.Text.Json;
using PS7ScriptDesk.PowerShell.Services;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class PSScriptAnalyzerServiceTests
{
    [Fact]
    public async Task RealBundledModuleReturnsRawFindingAndPreservesRequestId()
    {
        var repositoryRoot = FindRepositoryRoot();
        var module = Path.Combine(repositoryRoot, "PS7ScriptDesk.PowerShell", "Dependencies", "PSScriptAnalyzer", "1.25.0", "PSScriptAnalyzer.psd1");
        var runtime = Environment.GetEnvironmentVariable("PS7_SCRIPT_DESK_TEST_PWSH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe");
        if (!File.Exists(runtime) || !File.Exists(module))
        {
            return;
        }

        await using var service = new PSScriptAnalyzerService(runtime, module);
        var result = await service.AnalyzeAsync(new PSScriptAnalyzerRequest("real-smoke", "document-1", 1, "script.ps1", "Invoke-Expression 'Get-Date'"));

        Assert.Null(result.Error);
        Assert.Equal("real-smoke", result.RequestId);
        Assert.Contains(result.Findings, finding => finding.RuleId is not null && finding.Line >= 1 && finding.Column >= 1);
    }

    [Fact]
    public async Task MissingRuntimeReturnsBoundedError()
    {
        await using var service = new PSScriptAnalyzerService(Path.Combine(Path.GetTempPath(), "missing-pwsh.exe"), Path.Combine(Path.GetTempPath(), "missing-pssa.psd1"));

        var result = await service.AnalyzeAsync(new PSScriptAnalyzerRequest("missing", "doc", 1, null, "x"));

        Assert.Equal("PowerShell runtime was not found.", result.Error);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task CancellationBeforeDispatchIsCooperative()
    {
        await using var service = new PSScriptAnalyzerService(Path.Combine(Path.GetTempPath(), "missing-pwsh.exe"), Path.Combine(Path.GetTempPath(), "missing-pssa.psd1"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(new PSScriptAnalyzerRequest("cancel", "doc", 1, null, "x"), cancellation.Token));
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("json")]
    [InlineData("mismatch")]
    [InlineData("exit")]
    public async Task MalformedOrUnexpectedFramesReturnBoundedErrors(string mode)
    {
        var worker = new ScriptedWorker((requestId, _) => mode switch
        {
            "base64" => Task.FromResult<string?>($"##PSSA_RESULT_{requestId}##not-base64"),
            "json" => Task.FromResult<string?>($"##PSSA_RESULT_{requestId}##{Encode("not-json")}"),
            "mismatch" => Task.FromResult<string?>($"##PSSA_RESULT_other##{Encode("{}")}"),
            _ => Task.FromResult<string?>(null)
        });
        await using var service = new PSScriptAnalyzerService(_ => Task.FromResult<IPSScriptAnalyzerWorker>(worker), TimeSpan.FromMilliseconds(60));

        var result = await service.AnalyzeAsync(new PSScriptAnalyzerRequest("bad", "doc", 1, null, "x"));

        Assert.NotNull(result.Error);
        Assert.Empty(result.Findings);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task TimeoutDisposesWorkerAndNextRequestUsesARecoveredWorker()
    {
        var factoryCalls = 0;
        var first = new ScriptedWorker(WaitForCancellation);
        var second = new ScriptedWorker((requestId, _) => Task.FromResult<string?>(Response(requestId, Array.Empty<PSScriptAnalyzerFinding>())));
        await using var service = new PSScriptAnalyzerService(_ => Task.FromResult<IPSScriptAnalyzerWorker>(++factoryCalls == 1 ? first : second), TimeSpan.FromMilliseconds(50));

        var firstResult = await service.AnalyzeAsync(new PSScriptAnalyzerRequest("timeout", "doc", 1, null, "x"));
        var secondResult = await service.AnalyzeAsync(new PSScriptAnalyzerRequest("restart", "doc", 2, null, "x"));

        Assert.NotNull(firstResult.Error);
        Assert.Null(secondResult.Error);
        Assert.Equal(2, factoryCalls);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task RequestsAreSerializedByTheSingleWorkerGate()
    {
        var worker = new ScriptedWorker(async (requestId, cancellationToken) =>
        {
            await Task.Delay(30, cancellationToken);
            return Response(requestId, Array.Empty<PSScriptAnalyzerFinding>());
        });
        await using var service = new PSScriptAnalyzerService(_ => Task.FromResult<IPSScriptAnalyzerWorker>(worker), TimeSpan.FromSeconds(1));

        await Task.WhenAll(
            service.AnalyzeAsync(new PSScriptAnalyzerRequest("one", "doc", 1, null, "x")),
            service.AnalyzeAsync(new PSScriptAnalyzerRequest("two", "doc", 2, null, "x")));

        Assert.Equal(1, worker.MaxConcurrentWrites);
    }

    [Fact]
    public async Task DisposalPreventsFutureRequestsAndDisposesIdleWorker()
    {
        var worker = new ScriptedWorker((requestId, _) => Task.FromResult<string?>(Response(requestId, Array.Empty<PSScriptAnalyzerFinding>())));
        var service = new PSScriptAnalyzerService(_ => Task.FromResult<IPSScriptAnalyzerWorker>(worker), TimeSpan.FromSeconds(1));
        await service.AnalyzeAsync(new PSScriptAnalyzerRequest("idle", "doc", 1, null, "x"));

        service.Dispose();

        Assert.True(worker.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.AnalyzeAsync(new PSScriptAnalyzerRequest("disposed", "doc", 2, null, "x")));
    }

    [Fact]
    public async Task HealthSnapshotTracksReadyWorkerAndWarmReuse()
    {
        var worker = new ScriptedWorker((requestId, _) => Task.FromResult<string?>(Response(requestId, Array.Empty<PSScriptAnalyzerFinding>())));
        await using var service = new PSScriptAnalyzerService(_ => Task.FromResult<IPSScriptAnalyzerWorker>(worker), TimeSpan.FromSeconds(1));
        await service.AnalyzeAsync(new PSScriptAnalyzerRequest("health-one", "doc", 1, null, "x"));
        var first = service.Health;
        await service.AnalyzeAsync(new PSScriptAnalyzerRequest("health-two", "doc", 2, null, "x"));
        var second = service.Health;
        Assert.Equal(PSScriptAnalyzerWorkerState.Ready, second.State);
        Assert.Equal(first.Generation, second.Generation);
        Assert.Equal(0, second.RestartCount);
        Assert.NotNull(second.LastAnalysisMilliseconds);
        Assert.NotNull(second.LastSuccessfulAnalysisMilliseconds);
    }

    private static string Response(string requestId, IReadOnlyList<PSScriptAnalyzerFinding> findings)
        => $"##PSSA_RESULT_{requestId}##{Encode(JsonSerializer.Serialize(new PSScriptAnalyzerResult(requestId, findings)))}";

    private static async Task<string?> WaitForCancellation(string _, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class ScriptedWorker : IPSScriptAnalyzerWorker
    {
        private readonly Func<string, CancellationToken, Task<string?>> _response;
        private string _requestId = string.Empty;
        private int _activeWrites;

        public ScriptedWorker(Func<string, CancellationToken, Task<string?>> response) => _response = response;
        public bool Disposed { get; private set; }
        public int MaxConcurrentWrites { get; private set; }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
            => _requestId.Length == 0 ? Task.FromResult<string?>("##PSSA_READY##") : _response(_requestId, cancellationToken);

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(line));
            _requestId = JsonSerializer.Deserialize<PSScriptAnalyzerRequest>(json)!.RequestId;
            var active = Interlocked.Increment(ref _activeWrites);
            MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, active);
            await Task.Yield();
            Interlocked.Decrement(ref _activeWrites);
        }

        public void Dispose() => Disposed = true;
    }
}
