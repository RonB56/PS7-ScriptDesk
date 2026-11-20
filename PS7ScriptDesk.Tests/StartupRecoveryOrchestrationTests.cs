namespace PS7ScriptDesk.Tests;

public sealed class StartupRecoveryOrchestrationTests
{
    [Fact]
    public void StartupRecoveryIsQueuedAfterIndependentInitializationAndUsesTrackedDispatcherCancellation()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.DoesNotContain("ViewModel.ProcessStartupDocumentRecovery();", source, StringComparison.Ordinal);
        var hostAttached = source.IndexOf("StartupLifecycleTrace.Write(\"MainWindow.TerminalHost\", \"ATTACHED\"", StringComparison.Ordinal);
        var warmStart = source.IndexOf("RequestConsoleWarmStart(\"TerminalHostAttached\")", hostAttached, StringComparison.Ordinal);
        var deferred = source.IndexOf("StartDeferredInitialization(ViewModel);", warmStart, StringComparison.Ordinal);
        var recovery = source.IndexOf("ScheduleStartupDocumentRecovery(ViewModel);", deferred, StringComparison.Ordinal);

        Assert.True(hostAttached >= 0);
        Assert.True(warmStart > hostAttached);
        Assert.True(deferred > warmStart);
        Assert.True(recovery > deferred);
        Assert.Contains("DispatcherPriority.ContextIdle", source, StringComparison.Ordinal);
        Assert.Contains("ProcessStartupDocumentRecovery(cancellation.Token)", source, StringComparison.Ordinal);
        Assert.Contains("CancelStartupDocumentRecovery();", source, StringComparison.Ordinal);
        Assert.Contains("ProcessStartupDocumentRecovery(CancellationToken cancellationToken = default)",
            TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs"),
            StringComparison.Ordinal);
    }
}
