namespace PS7ScriptDesk.Tests;

public sealed class GitPhase3StructuralTests
{
    [Fact]
    public void GitMenuAndCommandPaletteExposeStageAndUnstageThroughViewModelCommands()
    {
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var sourceControlView = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("StageAllCommand", sourceControlView + viewModel, StringComparison.Ordinal);
        Assert.Contains("UnstageAllCommand", sourceControlView + viewModel, StringComparison.Ordinal);
        Assert.Contains("git.stageFile", shell, StringComparison.Ordinal);
        Assert.Contains("git.unstageFile", shell, StringComparison.Ordinal);
        Assert.Contains("IGitService", viewModel, StringComparison.Ordinal);
        Assert.Contains("StageFileAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("UnstageFileAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("ShowConfirmation", viewModel, StringComparison.Ordinal);
        Assert.Contains("Discard is blocked because this file is open with unsaved editor changes", viewModel, StringComparison.Ordinal);
        Assert.Contains("Untracked files are not deleted by Discard", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("reset --hard", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clean -fd", viewModel, StringComparison.OrdinalIgnoreCase);
    }
}
