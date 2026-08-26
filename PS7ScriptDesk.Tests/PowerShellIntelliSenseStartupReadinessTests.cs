using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellIntelliSenseStartupReadinessTests
{
    [Theory]
    [InlineData("$t", "$true")]
    [InlineData("$t", "$this")]
    [InlineData("$f", "$false")]
    [InlineData("$n", "$null")]
    [InlineData("$las", "$LASTEXITCODE")]
    [InlineData("$?", "$?")]
    [InlineData("$h", "$HOME")]
    [InlineData("$p", "$PWD")]
    [InlineData("$psv", "$PSVersionTable")]
    [InlineData("$e", "$Error")]
    [InlineData("$a", "$args")]
    [InlineData("$i", "$input")]
    public void AutomaticVariables_AreAvailableFromLocalColdStartPath(string fragment, string expectedVariable)
    {
        var candidates = PowerShellIntelliSenseService.GetLocalVariableCompletionCandidatesForTesting(
            fragment,
            fragment.Length);

        Assert.Contains(candidates, candidate => candidate.CompletionText == expectedVariable);
    }

    [Fact]
    public void LocalVariables_MergeWithAutomaticVariablesWithoutDuplicates()
    {
        const string script = "param($ComputerName)\n$customValue = 1\n$true\n$t";

        var candidates = PowerShellIntelliSenseService.GetLocalVariableCompletionCandidatesForTesting(
            script,
            script.Length);

        Assert.Contains(candidates, candidate => candidate.CompletionText == "$ComputerName");
        Assert.Contains(candidates, candidate => candidate.CompletionText == "$customValue");
        Assert.Contains(candidates, candidate => candidate.CompletionText == "$true");
        Assert.Equal(1, candidates.Count(candidate => candidate.CompletionText == "$true"));
    }

    [Fact]
    public void VariableCompletionContext_DoesNotQueryLivePowerShellEngine()
    {
        var shouldQueryEngine = PowerShellIntelliSenseService.ShouldQueryEngineForTesting(
            "$t",
            caretOffset: 2,
            includeEngine: true,
            engineWaitMilliseconds: 350,
            pwshExecutablePath: @"C:\Program Files\PowerShell\7\pwsh.exe");

        Assert.False(shouldQueryEngine);
    }

    [Fact]
    public void QuestionMarkAutomaticVariable_UsesWholeVariableReplacementSpan()
    {
        var candidates = PowerShellIntelliSenseService.GetLocalVariableCompletionCandidatesForTesting("$?", 2);
        var questionMarkVariable = Assert.Single(candidates, candidate => candidate.CompletionText == "$?");

        Assert.Equal(0, questionMarkVariable.ReplacementOffset);
        Assert.Equal(2, questionMarkVariable.ReplacementLength);
    }

    [Fact]
    public void CommandCompletionContext_CanStillQueryLivePowerShellEngine()
    {
        var shouldQueryEngine = PowerShellIntelliSenseService.ShouldQueryEngineForTesting(
            "Get-Ch",
            caretOffset: 6,
            includeEngine: true,
            engineWaitMilliseconds: 350,
            pwshExecutablePath: @"C:\Program Files\PowerShell\7\pwsh.exe");

        Assert.True(shouldQueryEngine);
    }

    [Fact]
    public void MetadataReadinessState_ReportsActiveReadyAndFailureAccurately()
    {
        var initializing = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.Scheduled,
            "PowerShell IntelliSense initializing...");
        var ready = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.Completed,
            "PowerShell IntelliSense ready",
            commandCount: 10,
            quickInfoCount: 10,
            parameterizedQuickInfoCount: 8,
            getChildItemParameterCount: 4);
        var failed = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.Failed,
            "PowerShell IntelliSense failed; see log");

        Assert.True(initializing.IsActive);
        Assert.False(initializing.HasReadyMetadata);
        Assert.True(ready.IsCompletedSuccessfully);
        Assert.True(ready.HasReadyMetadata);
        Assert.False(ready.IsActive);
        Assert.False(failed.IsActive);
        Assert.False(failed.HasReadyMetadata);
    }

    [Fact]
    public void CompletionEngineReadinessState_ReportsInitializingReadyAndFailureAccurately()
    {
        var initializing = new PowerShellCompletionEngineStatus(
            PowerShellCompletionEnginePhase.Initializing,
            "PowerShell IntelliSense initializing...");
        var ready = new PowerShellCompletionEngineStatus(
            PowerShellCompletionEnginePhase.Ready,
            "PowerShell IntelliSense ready",
            elapsedMilliseconds: 125);
        var failed = new PowerShellCompletionEngineStatus(
            PowerShellCompletionEnginePhase.Failed,
            "PowerShell IntelliSense failed; see log");

        Assert.True(initializing.IsActive);
        Assert.False(initializing.IsReady);
        Assert.True(ready.IsReady);
        Assert.False(ready.IsActive);
        Assert.Equal(125, ready.ElapsedMilliseconds);
        Assert.True(failed.IsFailed);
        Assert.False(failed.IsActive);
    }

    [Fact]
    public async Task LivePowerShellCompletionEngine_ReturnsAutomaticVariablesAfterReady()
    {
        var pwshExecutablePath = ResolvePwshExecutablePath();
        if (pwshExecutablePath is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var trueResult = await service.GetCompletionsAsync("$t", 2, pwshExecutablePath, cancellationTokenSource.Token);
        var psVersionResult = await service.GetCompletionsAsync("$psv", 4, pwshExecutablePath, cancellationTokenSource.Token);
        var lastExitCodeResult = await service.GetCompletionsAsync("$las", 4, pwshExecutablePath, cancellationTokenSource.Token);

        Assert.Contains(trueResult.Items, item => item.CompletionText == "$true");
        Assert.Contains(trueResult.Items, item => item.CompletionText == "$this");
        Assert.Contains(psVersionResult.Items, item => item.CompletionText == "$PSVersionTable");
        Assert.Contains(lastExitCodeResult.Items, item => item.CompletionText == "$LASTEXITCODE");
    }

    private static string? ResolvePwshExecutablePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PWSH"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache",
                "codex-runtimes",
                "codex-primary-runtime",
                "dependencies",
                "native",
                "powershell",
                "pwsh.exe"),
            @"C:\Program Files\PowerShell\7\pwsh.exe"
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(pathEntry, "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
