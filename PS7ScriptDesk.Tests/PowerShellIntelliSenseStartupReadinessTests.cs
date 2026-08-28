using PS7ScriptDesk.Domain.Models;
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

    [Fact]
    public void StaticMemberCompletion_HasImmediateLocalCandidatesWithCorrectInsertionSpan()
    {
        const string documentText = "[System.IO.File]::";

        var candidates = PowerShellIntelliSenseService.GetLocalStaticMemberCompletionCandidatesForTesting(
            documentText,
            documentText.Length);

        Assert.True(PowerShellIntelliSenseService.HasImmediateLocalStaticMemberCompletionForTesting(documentText, documentText.Length));
        Assert.True(candidates.Count >= 9);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(documentText.Length, candidate.ReplacementOffset);
            Assert.Equal(0, candidate.ReplacementLength);
        });
    }

    [Fact]
    public void StaticMemberCompletion_BuildsLocalWindowBeforeLiveEngineRequest()
    {
        var source = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "PowerShellIntelliSenseService.cs"));
        var localStaticBranch = source.IndexOf("\"local-static-member\"", StringComparison.Ordinal);
        var liveEngineRequest = source.IndexOf("_completionService.GetCompletionsAsync", StringComparison.Ordinal);

        Assert.True(localStaticBranch >= 0);
        Assert.True(liveEngineRequest >= 0);
        Assert.True(localStaticBranch < liveEngineRequest);
    }

    [Fact]
    public void PositionalPathCompletion_RecognizesGetChildItemPathArgumentWithCorrectSpan()
    {
        const string documentText = "Get-ChildItem C:\\Pro";

        var context = PowerShellIntelliSenseService.GetParameterValueCompletionContextForTesting(
            documentText,
            documentText.Length);

        Assert.NotNull(context);
        Assert.True(context.IsPositionalPathFallback);
        Assert.Equal("Get-ChildItem", context.CommandName);
        Assert.Equal("Path", context.ParameterName);
        Assert.Equal("C:\\Pro", context.Fragment);
        Assert.Equal("Get-ChildItem ".Length, context.ReplacementOffset);
        Assert.Equal("C:\\Pro".Length, context.ReplacementLength);
    }

    [Fact]
    public void PositionalPathCompletion_GeneratesLocalCandidatesForPathLikeArgument()
    {
        using var fixture = LocalPathCompletionFixture.Create();
        var documentText = "Get-ChildItem " + Path.Combine(fixture.RootPath, "Pro");

        var candidates = PowerShellIntelliSenseService.GetLocalPathCompletionCandidatesForTesting(
            documentText,
            documentText.Length);

        Assert.Contains(candidates, candidate => candidate.DisplayText.EndsWith("ProjectAlpha\\", StringComparison.Ordinal));
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("Get-ChildItem ".Length, candidate.ReplacementOffset);
            Assert.Equal(Path.Combine(fixture.RootPath, "Pro").Length, candidate.ReplacementLength);
        });
    }

    [Fact]
    public void QuotedPositionalPathCompletion_PreservesQuotedValueContext()
    {
        using var fixture = LocalPathCompletionFixture.Create();
        var fragment = Path.Combine(fixture.RootPath, "Pro");
        var documentText = "Get-ChildItem \"" + fragment;

        var context = PowerShellIntelliSenseService.GetParameterValueCompletionContextForTesting(
            documentText,
            documentText.Length);
        var candidates = PowerShellIntelliSenseService.GetLocalPathCompletionCandidatesForTesting(
            documentText,
            documentText.Length);

        Assert.NotNull(context);
        Assert.True(context.IsQuotedValue);
        Assert.True(context.IsPositionalPathFallback);
        Assert.Contains(candidates, candidate => candidate.CompletionText.EndsWith("ProjectAlpha\\", StringComparison.Ordinal));
    }

    [Fact]
    public void NonPathPositionalArgument_DoesNotReceiveLocalFilesystemSuggestions()
    {
        const string documentText = "Write-Output Hello";

        var context = PowerShellIntelliSenseService.GetParameterValueCompletionContextForTesting(
            documentText,
            documentText.Length);
        var candidates = PowerShellIntelliSenseService.GetLocalPathCompletionCandidatesForTesting(
            documentText,
            documentText.Length);

        Assert.Null(context);
        Assert.Empty(candidates);
    }

    [Fact]
    public void ExplicitParameterPathCompletion_RemainsExplicitParameterValueContext()
    {
        using var fixture = LocalPathCompletionFixture.Create();
        var fragment = Path.Combine(fixture.RootPath, "Pro");
        var documentText = "Get-ChildItem -Path " + fragment;

        var context = PowerShellIntelliSenseService.GetParameterValueCompletionContextForTesting(
            documentText,
            documentText.Length);
        var candidates = PowerShellIntelliSenseService.GetLocalPathCompletionCandidatesForTesting(
            documentText,
            documentText.Length);

        Assert.NotNull(context);
        Assert.False(context.IsPositionalPathFallback);
        Assert.Equal("Path", context.ParameterName);
        Assert.Contains(candidates, candidate => candidate.DisplayText.EndsWith("ProjectAlpha\\", StringComparison.Ordinal));
    }

    [Fact]
    public void PositionalPathCompletion_CanStillQueryLivePowerShellEngine()
    {
        var shouldQueryEngine = PowerShellIntelliSenseService.ShouldQueryEngineForTesting(
            "Get-ChildItem C:\\Pro",
            caretOffset: "Get-ChildItem C:\\Pro".Length,
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
    public async Task LiveCompletionRequestWaitingForWarmupGetsResponseBudgetAfterReadiness()
    {
        var pwshExecutablePath = ResolvePwshExecutablePath();
        if (pwshExecutablePath is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        var initializing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateReadinessHandler(initializing, null, pwshExecutablePath);
        service.CompletionEngineStatusChanged += handler;
        try
        {
            service.StartCompletionEngineWarmup(CreateRuntime(pwshExecutablePath));
            await initializing.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await service.GetCompletionsAsync(
                "Get-ChildItem C:\\Pro",
                "Get-ChildItem C:\\Pro".Length,
                pwshExecutablePath,
                requestCancellation.Token,
                TimeSpan.FromMilliseconds(350));

            Assert.NotEmpty(result.Items);
        }
        finally
        {
            service.CompletionEngineStatusChanged -= handler;
        }
    }

    [Fact]
    public async Task ReadyCompletionKeepsTheExistingFastResponsePath()
    {
        var pwshExecutablePath = ResolvePwshExecutablePath();
        if (pwshExecutablePath is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        await StartWarmupAndWaitForReadyAsync(service, pwshExecutablePath);

        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await service.GetCompletionsAsync(
            "Get-Ch",
            "Get-Ch".Length,
            pwshExecutablePath,
            requestCancellation.Token,
            TimeSpan.FromMilliseconds(350));

        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task CancellationDuringWarmupDoesNotResetHealthyStartupHelper()
    {
        var pwshExecutablePath = ResolvePwshExecutablePath();
        if (pwshExecutablePath is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        var initializing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateReadinessHandler(initializing, ready, pwshExecutablePath);
        service.CompletionEngineStatusChanged += handler;
        try
        {
            service.StartCompletionEngineWarmup(CreateRuntime(pwshExecutablePath));
            await initializing.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using (var canceledRequest = new CancellationTokenSource(TimeSpan.FromMilliseconds(25)))
            {
                var canceledResult = await service.GetCompletionsAsync(
                    "Get-ChildItem C:\\Pro",
                    "Get-ChildItem C:\\Pro".Length,
                    pwshExecutablePath,
                    canceledRequest.Token,
                    TimeSpan.FromMilliseconds(350));
                Assert.Empty(canceledResult.Items);
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await service.GetCompletionsAsync(
                "Get-Ch",
                "Get-Ch".Length,
                pwshExecutablePath,
                requestCancellation.Token,
                TimeSpan.FromMilliseconds(350));

            Assert.NotEmpty(result.Items);
        }
        finally
        {
            service.CompletionEngineStatusChanged -= handler;
        }
    }

    [Fact]
    public async Task MultipleRequestsDuringWarmupCompleteWithoutDuplicateStartup()
    {
        var pwshExecutablePath = ResolvePwshExecutablePath();
        if (pwshExecutablePath is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        var initializing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var handler = new EventHandler<PowerShellCompletionEngineStatusChangedEventArgs>((_, args) =>
        {
            if (args.Status.Phase == PowerShellCompletionEnginePhase.Initializing)
            {
                initializing.TrySetResult(true);
            }
            else if (args.Status.IsReady)
            {
                Interlocked.Increment(ref readyCount);
            }
        });
        service.CompletionEngineStatusChanged += handler;
        try
        {
            service.StartCompletionEngineWarmup(CreateRuntime(pwshExecutablePath));
            await initializing.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var requests = Enumerable.Range(0, 3).Select(async _ =>
            {
                using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                return await service.GetCompletionsAsync(
                    "Get-Ch",
                    "Get-Ch".Length,
                    pwshExecutablePath,
                    requestCancellation.Token,
                    TimeSpan.FromMilliseconds(350));
            });

            var results = await Task.WhenAll(requests);
            Assert.All(results, result => Assert.NotEmpty(result.Items));
            Assert.Equal(1, Volatile.Read(ref readyCount));
        }
        finally
        {
            service.CompletionEngineStatusChanged -= handler;
        }
    }

    [Fact]
    public async Task ActiveResponseTimeoutResetsTransportAndAllowsRecovery()
    {
        var pwshExecutablePath = ResolvePwshExecutablePath();
        if (pwshExecutablePath is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        await StartWarmupAndWaitForReadyAsync(service, pwshExecutablePath);

        using (var shortRequestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var timedOutResult = await service.GetCompletionsAsync(
                "Get-ChildItem C:\\Pro",
                "Get-ChildItem C:\\Pro".Length,
                pwshExecutablePath,
                shortRequestCancellation.Token,
                TimeSpan.FromMilliseconds(1));
            Assert.Empty(timedOutResult.Items);
        }

        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var recoveredResult = await service.GetCompletionsAsync(
            "Get-Ch",
            "Get-Ch".Length,
            pwshExecutablePath,
            requestCancellation.Token,
            TimeSpan.FromMilliseconds(350));

        Assert.NotEmpty(recoveredResult.Items);
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

    private static async Task StartWarmupAndWaitForReadyAsync(PowerShellCompletionService service, string pwshExecutablePath)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateReadinessHandler(null, ready, pwshExecutablePath);
        service.CompletionEngineStatusChanged += handler;
        try
        {
            service.StartCompletionEngineWarmup(CreateRuntime(pwshExecutablePath));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            service.CompletionEngineStatusChanged -= handler;
        }
    }

    private static EventHandler<PowerShellCompletionEngineStatusChangedEventArgs> CreateReadinessHandler(
        TaskCompletionSource<bool>? initializing,
        TaskCompletionSource<bool>? ready,
        string pwshExecutablePath)
    {
        return (_, args) =>
        {
            if (!string.Equals(args.Status.RuntimePath, Path.GetFullPath(pwshExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (args.Status.Phase == PowerShellCompletionEnginePhase.Initializing)
            {
                initializing?.TrySetResult(true);
            }
            else if (args.Status.IsReady)
            {
                ready?.TrySetResult(true);
            }
        };
    }

    private static PowerShellRuntimeInfo CreateRuntime(string pwshExecutablePath)
    {
        return new PowerShellRuntimeInfo(
            "PowerShell 7",
            "Core",
            "7.x",
            new Version(7, 0),
            "x64",
            pwshExecutablePath,
            "test",
            isPowerShell7OrLater: true,
            isWindowsPowerShell: false,
            isPreferred: true,
            isValidated: true,
            resolvedExecutablePath: pwshExecutablePath);
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

    private sealed class LocalPathCompletionFixture : IDisposable
    {
        private LocalPathCompletionFixture(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static LocalPathCompletionFixture Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskCompletionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            Directory.CreateDirectory(Path.Combine(rootPath, "ProjectAlpha"));
            Directory.CreateDirectory(Path.Combine(rootPath, "Other"));
            return new LocalPathCompletionFixture(rootPath);
        }

        public void Dispose()
        {
            try
            {
                if (RootPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
