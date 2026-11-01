namespace PS7ScriptDesk.Tests;

public sealed class RenderedPromptReadinessContractTests
{
    [Fact]
    public void ViewModelPromotesWritableIdleRenderedPromptWithoutPromotingDuringExecution()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("TryPromoteRenderedPromptReadiness(generation, rawOutput)", source, StringComparison.Ordinal);
        Assert.Contains("_liveConsoleService.IsSessionRunning", source, StringComparison.Ordinal);
        Assert.Contains("_liveConsoleService.IsInputWritable", source, StringComparison.Ordinal);
        Assert.Contains("_liveConsoleService.IsCommandInProgress", source, StringComparison.Ordinal);
        Assert.Contains("IsExecutionRunning", source, StringComparison.Ordinal);
        Assert.Contains("InteractiveIdleAtPrompt", source, StringComparison.Ordinal);
    }
}
