using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellRuntimeStateViewModelTests
{
    [Fact]
    public async Task SelectingRuntime_UpdatesSelectedStateImmediatelyWithoutPreferredDisplay()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var viewModel = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), new RecordingLiveConsoleService(), runtimeA);

        var runtimeBItem = new RuntimeItemViewModel(runtimeB);
        viewModel.DetectedRuntimes.Add(runtimeBItem);
        viewModel.SelectedRuntimeItem = runtimeBItem;

        Assert.Same(runtimeB, viewModel.EffectiveRuntimeInfo);
        Assert.Contains("PowerShell 7.6.5", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("Selected", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Equal("Terminal not started", viewModel.RunningRuntimeCompactText);
        Assert.True(viewModel.HasRunningRuntimeCompactText);
        Assert.Contains("PowerShell 7.6.5", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("Selected", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferred", viewModel.SelectedRuntimeCompactText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Preferred", runtimeBItem.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartConsole_AfterRuntimeSwitch_UpdatesRunningStateAndStatusBar()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var console = new RecordingLiveConsoleService();
        await console.StartSessionAsync(runtimeA, _ => { });
        console.Operations.Clear();

        var viewModel = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), console, runtimeA);
        viewModel.DetectedRuntimes.Add(new RuntimeItemViewModel(runtimeB));
        viewModel.SelectedRuntimeItem = viewModel.DetectedRuntimes.Single(item => item.RuntimeInfo == runtimeB);

        viewModel.RestartConsoleCommand.Execute(null);

        await WaitUntilAsync(() =>
            console.ActiveRuntime == runtimeB &&
            viewModel.SelectedRuntimeCompactText.Contains("7.6.5", StringComparison.Ordinal) &&
            viewModel.SelectedRuntimeCompactText.Contains("Running", StringComparison.Ordinal));

        Assert.Equal(["stop", "start"], console.Operations);
        Assert.Same(runtimeB, console.ActiveRuntime);
        Assert.Contains("PowerShell 7.6.5", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("Running", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.RunningRuntimeCompactText);
        Assert.False(viewModel.HasRunningRuntimeCompactText);
        Assert.Contains("PowerShell 7.6.5", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("Running", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.DoesNotContain("Selected", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerShell 7.6.3", viewModel.RuntimeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRuntimeSwitch_DoesNotReportFailedRuntimeAsRunning()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var console = new RecordingLiveConsoleService();
        await console.StartSessionAsync(runtimeA, _ => { });
        console.StartException = new InvalidOperationException("start failed");
        console.Operations.Clear();

        var viewModel = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB, validateRuntimeB: false), console, runtimeA);
        viewModel.DetectedRuntimes.Add(new RuntimeItemViewModel(runtimeB));
        viewModel.SelectedRuntimeItem = viewModel.DetectedRuntimes.Single(item => item.RuntimeInfo == runtimeB);

        viewModel.RestartConsoleCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.StatusText == "ConPTY terminal restart failed");

        Assert.NotSame(runtimeB, console.ActiveRuntime);
        Assert.DoesNotContain("PowerShell 7.6.5 - Running", viewModel.RunningRuntimeCompactText, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerShell 7.6.5 - Running", viewModel.RuntimeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeRefresh_KeepsMultipleAvailableRuntimesAndSelectsPersistedRuntime()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var settings = new ApplicationSettings
        {
            SelectedRuntimeExecutablePath = runtimeB.LaunchExecutablePath
        };
        var viewModel = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), new RecordingLiveConsoleService(), startupRuntime: null, settings);

        viewModel.RefreshRuntimesCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.DetectedRuntimes.Count == 2);

        Assert.Equal(2, viewModel.DetectedRuntimes.Count);
        Assert.Same(runtimeB, viewModel.SelectedRuntimeItem?.RuntimeInfo);
        Assert.Contains("PowerShell 7.6.5", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("Selected", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.All(viewModel.DetectedRuntimes, item => Assert.DoesNotContain("Preferred", item.DisplayText, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PersistedSelectedRuntime_IsRestoredAfterRestart()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var initial = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), new RecordingLiveConsoleService(), runtimeA);
        initial.DetectedRuntimes.Add(new RuntimeItemViewModel(runtimeB));
        initial.SelectedRuntimeItem = initial.DetectedRuntimes.Single(item => item.RuntimeInfo == runtimeB);

        var snapshot = initial.CreateApplicationSettingsSnapshot();
        var restored = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), new RecordingLiveConsoleService(), startupRuntime: null, snapshot);

        Assert.Equal(runtimeB.LaunchExecutablePath, snapshot.SelectedRuntimeExecutablePath);
        Assert.Same(runtimeB, restored.SelectedRuntimeItem?.RuntimeInfo);
        Assert.Contains("PowerShell 7.6.5", restored.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("Selected", restored.SelectedRuntimeCompactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeStatus_ShowsSelectedAndRunningOnlyWhenTheyDiffer()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var console = new RecordingLiveConsoleService();
        await console.StartSessionAsync(runtimeA, _ => { });

        var viewModel = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), console, runtimeA);
        viewModel.DetectedRuntimes.Add(new RuntimeItemViewModel(runtimeB));
        viewModel.SelectedRuntimeItem = viewModel.DetectedRuntimes.Single(item => item.RuntimeInfo == runtimeB);

        Assert.Contains("PowerShell 7.6.5", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("Selected", viewModel.SelectedRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("PowerShell 7.6.3", viewModel.RunningRuntimeCompactText, StringComparison.Ordinal);
        Assert.Contains("Running", viewModel.RunningRuntimeCompactText, StringComparison.Ordinal);
        Assert.True(viewModel.HasRunningRuntimeCompactText);
        Assert.Contains("PowerShell 7.6.3", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("Running", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("PowerShell 7.6.5", viewModel.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("Selected", viewModel.RuntimeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSelection_IsLockedWhileConsoleCommandOrDebuggingIsActive()
    {
        var runtimeA = CreateRuntime("7.6.3", isPreferred: true);
        var runtimeB = CreateRuntime("7.6.5", isPreferred: false);
        var console = new RecordingLiveConsoleService
        {
            IsSessionRunning = true,
            IsCommandInProgress = true
        };
        await console.StartSessionAsync(runtimeA, _ => { });

        var viewModel = await CreateViewModelAsync(new RuntimeStateRuntimeService(runtimeA, runtimeB), console, runtimeA);
        viewModel.DetectedRuntimes.Add(new RuntimeItemViewModel(runtimeB));

        Assert.False(viewModel.IsRuntimeListEnabled);
        Assert.Contains("locked", viewModel.RuntimeSelectionStatusText, StringComparison.OrdinalIgnoreCase);

        console.IsCommandInProgress = false;
        viewModel.IsDebugSessionActive = true;

        Assert.False(viewModel.IsRuntimeListEnabled);
        Assert.Contains("locked", viewModel.RuntimeSelectionStatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<MainWindowViewModel> CreateViewModelAsync(
        RuntimeStateRuntimeService runtimeService,
        RecordingLiveConsoleService console,
        PowerShellRuntimeInfo? startupRuntime,
        ApplicationSettings? initialSettings = null)
    {
        return Task.Run(() => new MainWindowViewModel(
            new FakeWorkspaceService(),
            runtimeService,
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            console,
            new FakeExeExportService(),
            initialSettings,
            startupRuntime));
    }

    private static PowerShellRuntimeInfo CreateRuntime(string version, bool isPreferred)
    {
        return new PowerShellRuntimeInfo(
            $"PowerShell {version} x64",
            "Core",
            version,
            Version.Parse(version),
            "x64",
            $@"C:\Program Files\PowerShell\{version}\pwsh.exe",
            "RuntimeStateTest",
            isPowerShell7OrLater: true,
            isWindowsPowerShell: false,
            isPreferred: isPreferred,
            isValidated: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class RuntimeStateRuntimeService : IRuntimeService
    {
        private readonly IReadOnlyList<PowerShellRuntimeInfo> _runtimes;
        private readonly bool _validateRuntimeB;

        public RuntimeStateRuntimeService(PowerShellRuntimeInfo runtimeA, PowerShellRuntimeInfo runtimeB, bool validateRuntimeB = true)
        {
            _runtimes = new[] { runtimeA, runtimeB };
            _validateRuntimeB = validateRuntimeB;
        }

        public RuntimeDiscoveryResult DiscoverRuntimes() => DiscoverRuntimes(requireLaunchValidation: false);

        public RuntimeDiscoveryResult DiscoverRuntimes(bool requireLaunchValidation)
        {
            return new RuntimeDiscoveryResult(
                _runtimes,
                _runtimes.FirstOrDefault(runtime => runtime.IsPreferred),
                $"Detected {_runtimes.Count} runtime(s).");
        }

        public PowerShellRuntimeInfo? TryResolveRuntimeIdentity(string executablePath)
        {
            return _runtimes.FirstOrDefault(runtime =>
                string.Equals(runtime.LaunchExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
        }

        public RuntimeValidationResult ValidateRuntimePath(string executablePath, string source)
        {
            var runtime = TryResolveRuntimeIdentity(executablePath);
            if (runtime is not null && (_validateRuntimeB || !runtime.VersionText.EndsWith("5", StringComparison.Ordinal)))
            {
                return new RuntimeValidationResult(runtime, CreateCandidate(runtime, source, failureReason: string.Empty));
            }

            return new RuntimeValidationResult(null, CreateCandidate(runtime, source, failureReason: "Runtime failed validation."));
        }

        private static RuntimeDiscoveryCandidateInfo CreateCandidate(PowerShellRuntimeInfo? runtime, string source, string failureReason)
        {
            return new RuntimeDiscoveryCandidateInfo(
                runtime?.LaunchExecutablePath ?? string.Empty,
                source,
                exists: runtime is not null,
                isWindowsAppsAlias: false,
                validationAttempted: true,
                launchSucceeded: string.IsNullOrWhiteSpace(failureReason),
                validationSucceeded: string.IsNullOrWhiteSpace(failureReason),
                timedOut: false,
                exitCode: string.IsNullOrWhiteSpace(failureReason) ? 0 : 1,
                edition: runtime?.Edition ?? string.Empty,
                versionText: runtime?.VersionText ?? string.Empty,
                architecture: runtime?.Architecture ?? string.Empty,
                resolvedExecutablePath: runtime?.LaunchExecutablePath ?? string.Empty,
                psHome: runtime?.PsHome ?? string.Empty,
                stdoutSummary: string.Empty,
                stderrSummary: string.Empty,
                fileVersion: runtime?.VersionText ?? string.Empty,
                productVersion: runtime?.VersionText ?? string.Empty,
                failureReason: failureReason);
        }
    }
}
