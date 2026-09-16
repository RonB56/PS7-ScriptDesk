using System.Windows.Threading;

namespace PS7ScriptDesk.Tests;

public sealed class Batch10StartupDispatcherRegressionTests
{
    [Fact]
    public void MainWindow_TracksAndRecoversAbortedTerminalDrainOperations()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("private DispatcherOperation? _terminalOutputDrainOperation", source, StringComparison.Ordinal);
        Assert.Contains("operation.Aborted += TerminalOutputDrainOperation_Aborted", source, StringComparison.Ordinal);
        Assert.Contains("TerminalOutputDrainOperation_Aborted", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherBeginInvoke.RecoveredAfterAbort", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatcherOperation_AbortIsObservableOnAnStaDispatcher()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var operation = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { }));
                var aborted = false;
                operation.Aborted += (_, _) => aborted = true;
                operation.Abort();
                Assert.True(aborted || operation.Status == DispatcherOperationStatus.Aborted);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        thread.Join(TimeSpan.FromSeconds(1));
        Assert.Null(failure);
    }

    [Fact]
    public void Batch9PropertyChangedMarshalingRemainsInPlaceForWorkerNotifications()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("_uiSynchronizationContext.Post(_ =>", source, StringComparison.Ordinal);
        Assert.Contains("PropertyChanged?.Invoke(this, args)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Post(_ => OnPropertyChanged(propertyName)", source, StringComparison.Ordinal);
        Assert.Contains("await PostToUiAsync(() =>", source, StringComparison.Ordinal);
        Assert.Contains("EnsureRemoteAvailableAsync", source, StringComparison.Ordinal);
    }
}
