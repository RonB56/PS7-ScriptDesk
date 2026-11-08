namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputDispatcherPerformanceContractTests
{
    [Fact]
    public void OrdinaryExecutionOutputReturnsBeforeSchedulingViewModelWork()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var start = source.IndexOf("private void AppendExecutionOutput(ExecutionOutputRecord record)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private void OnStructuredEditorExecutionEvent", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];

        var ordinaryOutputGuard = method.IndexOf("record.StreamKind != ExecutionOutputStreamKind.Lifecycle", StringComparison.Ordinal);
        var uiPost = method.IndexOf("PostToUi(() =>", StringComparison.Ordinal);
        Assert.True(ordinaryOutputGuard >= 0 && ordinaryOutputGuard < uiPost);
        Assert.Contains("MainWindowViewModel.AppendExecutionOutput.LifecyclePost", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererDrainHasWorkBudgetsAndUsesExistingReschedulePath()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        Assert.Contains("MaximumTerminalOutputDrainEnvelopes = 128", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTerminalOutputDrainCharacters = 64 * 1024", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTerminalOutputDrainMilliseconds = 2", source, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetElapsedTime(drainStartedAt).TotalMilliseconds >= MaximumTerminalOutputDrainMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("drainedCharacterCount >= MaximumTerminalOutputDrainCharacters", source, StringComparison.Ordinal);
        Assert.Contains("drainedEnvelopeCount >= MaximumTerminalOutputDrainEnvelopes", source, StringComparison.Ordinal);
        Assert.Contains("MainWindow.DispatcherBeginInvoke.Rescheduled", source, StringComparison.Ordinal);
    }
}
