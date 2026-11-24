namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputDispatcherPerformanceContractTests
{
    [Fact]
    public void OrdinaryExecutionOutputReturnsBeforeSchedulingViewModelWork()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var start = source.IndexOf("private void AppendExecutionOutput(ExecutionOutputRecord record)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private static bool ShouldSurfaceLifecycleMessageToUser", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = source[start..end];

        var ordinaryOutputGuard = method.IndexOf("record.StreamKind != ExecutionOutputStreamKind.Lifecycle", StringComparison.Ordinal);
        var uiPost = method.IndexOf("PostToUi(() =>", StringComparison.Ordinal);
        Assert.True(ordinaryOutputGuard >= 0 && ordinaryOutputGuard < uiPost);
        Assert.Contains("MainWindowViewModel.AppendExecutionOutput.LifecyclePost", method, StringComparison.Ordinal);
    }

}
