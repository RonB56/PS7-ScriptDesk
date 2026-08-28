using PS7ScriptDesk.Shell;
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

    [Theory]
    [InlineData(CompletionItemKind.ProviderItem)]
    [InlineData(CompletionItemKind.ProviderContainer)]
    public void PathCompletion_WhitespaceDismissesInsteadOfCommitting(CompletionItemKind completionKind)
    {
        Assert.True(MainWindow.ShouldDismissPathCompletionForTextInput(completionKind, ' '));
        Assert.True(MainWindow.ShouldDismissPathCompletionForTextInput(completionKind, '\t'));
        Assert.False(MainWindow.ShouldCommitCompletionForTextInput('A'));
    }

    [Theory]
    [InlineData(CompletionItemKind.Command)]
    [InlineData(CompletionItemKind.ParameterName)]
    [InlineData(CompletionItemKind.Variable)]
    public void NonPathCompletion_PreservesExistingWhitespaceCommitBehavior(CompletionItemKind completionKind)
    {
        Assert.False(MainWindow.ShouldDismissPathCompletionForTextInput(completionKind, ' '));
        Assert.True(MainWindow.ShouldCommitCompletionForTextInput(' '));
        Assert.True(MainWindow.ShouldCommitCompletionForTextInput('('));
    }

    [Fact]
    public void MetadataReadinessState_ReportsActiveReadyAndFailureAccurately()
    {
        var initializing = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.Scheduled,
            "IntelliSense: Warming up...");
        var coreReady = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.CoreReady,
            "IntelliSense: Warming up...");
        var discoveringModules = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.DiscoveringModules,
            "IntelliSense: Warming up...");
        var ready = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.Completed,
            "IntelliSense: Ready",
            commandCount: 10,
            quickInfoCount: 10,
            parameterizedQuickInfoCount: 8,
            getChildItemParameterCount: 4);
        var failed = new EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase.Failed,
            "IntelliSense: Failed; see log");

        Assert.True(initializing.IsActive);
        Assert.False(initializing.HasReadyMetadata);
        Assert.True(coreReady.IsActive);
        Assert.False(coreReady.HasReadyMetadata);
        Assert.True(discoveringModules.IsActive);
        Assert.False(discoveringModules.HasReadyMetadata);
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
            "IntelliSense: Warming up...");
        var ready = new PowerShellCompletionEngineStatus(
            PowerShellCompletionEnginePhase.Ready,
            "IntelliSense: Ready",
            elapsedMilliseconds: 125);
        var failed = new PowerShellCompletionEngineStatus(
            PowerShellCompletionEnginePhase.Failed,
            "IntelliSense: Failed; see log");

        Assert.True(initializing.IsActive);
        Assert.False(initializing.IsReady);
        Assert.True(ready.IsReady);
        Assert.False(ready.IsActive);
        Assert.Equal(125, ready.ElapsedMilliseconds);
        Assert.True(failed.IsFailed);
        Assert.False(failed.IsActive);
    }

    [Fact]
    public void MetadataBuilder_ReportsModuleDiscoveryAsSeparateBackgroundPhase()
    {
        var builderSource = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "EditorMetadataBuilderHost.cs"));

        Assert.Contains("EditorMetadataWarmupPhase.DiscoveringModules", builderSource, StringComparison.Ordinal);
        Assert.Contains("Get-Module -ListAvailable -ErrorAction SilentlyContinue", builderSource, StringComparison.Ordinal);
        Assert.True(
            builderSource.IndexOf("EditorMetadataWarmupPhase.DiscoveringModules", StringComparison.Ordinal) <
            builderSource.IndexOf("Get-Module -ListAvailable -ErrorAction SilentlyContinue", StringComparison.Ordinal));
    }

    [Fact]
    public void LargeModuleInventory_DoesNotBlockLocalColdStartCompletions()
    {
        var scriptBuilder = new System.Text.StringBuilder();
        for (var index = 0; index < 150; index++)
        {
            scriptBuilder.Append("Import-Module VMware.");
            scriptBuilder.Append(index);
            scriptBuilder.AppendLine();
        }

        scriptBuilder.Append("$t");

        var candidates = PowerShellIntelliSenseService.GetLocalVariableCompletionCandidatesForTesting(
            scriptBuilder.ToString(),
            scriptBuilder.Length);

        Assert.Contains(candidates, candidate => candidate.CompletionText == "$true");
        Assert.Contains(candidates, candidate => candidate.CompletionText == "$this");
    }

    [Fact]
    public void CoreReadiness_DoesNotDependOnFullModuleFingerprintBeforeStatus()
    {
        var serviceSource = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "PowerShellCompletionService.cs"));

        Assert.Contains("RaiseCoreReadyStatusIfPossible", serviceSource, StringComparison.Ordinal);
        Assert.Contains("EditorMetadataWarmupPhase.CoreReady", serviceSource, StringComparison.Ordinal);
        Assert.True(
            serviceSource.IndexOf("RaiseCoreReadyStatusIfPossible", StringComparison.Ordinal) <
            serviceSource.IndexOf("LaunchMetadataBuilderProcess", StringComparison.Ordinal));
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

    private static string GetRepositoryPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.Shell")) &&
                Directory.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.Tests")))
            {
                return Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
