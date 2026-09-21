namespace PS7ScriptDesk.Tests;

public sealed class GitUatBatch24Tests
{
    [Fact]
    public void StartupRequestsTerminalWarmStartAfterHostAndRuntimeAreReady()
    {
        var mainWindow = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("RequestConsoleWarmStart(\"TerminalHostAttached\")", mainWindow, StringComparison.Ordinal);
        Assert.Contains("requestTerminalWarmStart: reason => RequestConsoleWarmStart(reason)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_terminalWarmStartRequestSink?.Invoke(\"RuntimeDiscoveryCompleted\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("EffectiveRuntimeInfo is not null", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void WarmStartIsOwnedWithoutTaskRunAndDefersWhenRuntimeIsNotSelected()
    {
        var mainWindow = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("_consoleWarmStartTask = RunConsoleWarmStartAsync(viewModel, reason)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("waiting-for-runtime-discovery", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_consoleWarmStartTask = Task.Run", mainWindow, StringComparison.Ordinal);
        Assert.Contains("private async Task RunConsoleWarmStartAsync", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalStartupStagesCoverConptyProcessAndReaderOwnership()
    {
        var terminal = TestRepositoryPaths.ReadFile("PS7ScriptDesk.PowerShell", "Services", "LiveConsoleService.cs");

        Assert.Contains("startup-entered", terminal, StringComparison.Ordinal);
        Assert.Contains("conpty-create-started", terminal, StringComparison.Ordinal);
        Assert.Contains("conpty-create-completed", terminal, StringComparison.Ordinal);
        Assert.Contains("process-launch-started", terminal, StringComparison.Ordinal);
        Assert.Contains("process-launch-completed", terminal, StringComparison.Ordinal);
        Assert.Contains("reader-started", terminal, StringComparison.Ordinal);
        Assert.Contains("_stdoutReaderTask = Task.Run", terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRemainsIndependentFromGitWorkspaceRefresh()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var mainWindow = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        var runtimeDiscoveryWarmStart = viewModel.IndexOf("RuntimeDiscoveryCompleted", StringComparison.Ordinal);
        var gitRefresh = viewModel.IndexOf("await RefreshGitRepositoryAsync(logOperation: false)", StringComparison.Ordinal);
        var terminalHost = mainWindow.IndexOf("InitializeTerminalHostAsync", StringComparison.Ordinal);

        Assert.True(runtimeDiscoveryWarmStart >= 0);
        Assert.True(gitRefresh >= 0);
        Assert.True(terminalHost >= 0);
        Assert.Contains("StartDeferredInitialization(ViewModel)", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void GitSubsystemRemainsDormantWithoutRepositoryMetadata()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        var gate = viewModel.IndexOf("HasGitMetadataCandidate(gitContextPath)", StringComparison.Ordinal);
        var dormant = viewModel.IndexOf("Git: dormant", StringComparison.Ordinal);
        var skipped = viewModel.IndexOf("repositoryDetectionSkipped", StringComparison.Ordinal);
        var explicitRefresh = viewModel.IndexOf("await RefreshGitRepositoryAsync(logOperation: true)", StringComparison.Ordinal);

        Assert.True(gate >= 0);
        Assert.True(dormant > gate);
        Assert.True(skipped > gate);
        Assert.True(explicitRefresh >= 0);
        Assert.Contains("Directory.Exists(gitPath) || File.Exists(gitPath)", viewModel, StringComparison.Ordinal);
        Assert.Contains("gitSubprocessStarted", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch23GitAsyncCommandOwnershipRemainsPresent()
    {
        var relayCommand = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "Commands", "RelayCommand.cs");
        var mainViewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("public sealed class AsyncRelayCommand", relayCommand, StringComparison.Ordinal);
        Assert.Contains("new AsyncRelayCommand(() => InitializeRepositoryAsync()", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("_uiSynchronizationContext.Post(_ => RefreshGitCommands()", mainViewModel, StringComparison.Ordinal);
    }
}
