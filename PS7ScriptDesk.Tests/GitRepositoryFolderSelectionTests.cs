namespace PS7ScriptDesk.Tests;

public sealed class GitRepositoryFolderSelectionTests
{
    [Fact]
    public void GitRepositoryCommandUsesTheEstablishedFolderPickerAndWorkspaceLoadPath()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("new RelayCommand(() => _ = OnOpenRepositoryFolderAsync(), CanOpenRepositoryFolder)", viewModel, StringComparison.Ordinal);
        Assert.Contains("private async Task OnOpenRepositoryFolderAsync()", viewModel, StringComparison.Ordinal);
        Assert.Contains("var folderPath = _userPromptService.ShowOpenFolderDialog();", viewModel, StringComparison.Ordinal);
        Assert.Contains("await LoadWorkspaceFolderAsync(folderPath);", viewModel, StringComparison.Ordinal);
        Assert.Contains("await RefreshGitRepositoryAsync(logOperation: true);", viewModel, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenGitWorkspace_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Open Git _Workspace", xaml, StringComparison.Ordinal);
        Assert.Contains("git.openRepositoryFolder", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void GitRepositoryFolderSelectionDoesNotUseAFilePickerOrOpenEditorDocuments()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var methodStart = viewModel.IndexOf("private async Task OnOpenRepositoryFolderAsync()", StringComparison.Ordinal);
        var methodEnd = viewModel.IndexOf("        private void OnGitDiagnostics()", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);

        var method = viewModel[methodStart..methodEnd];
        Assert.DoesNotContain("OpenFileDialog", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", method, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenTabs", method, StringComparison.Ordinal);
        Assert.Contains("Git repository folder selection canceled", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalFileOpenFolderCommandStillUsesTheSameFolderSelectionAbstraction()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("OpenWorkspaceFolderCommand = new RelayCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("private async Task OnBrowseWorkspaceFolderAsync()", viewModel, StringComparison.Ordinal);
        Assert.Contains("await LoadWorkspaceFolderAsync(folderPath);", viewModel, StringComparison.Ordinal);
        Assert.Contains("var folderPath = _userPromptService.ShowOpenFolderDialog();", shell, StringComparison.Ordinal);
        Assert.Contains("await ViewModel.LoadWorkspaceFolderAsync(folderPath);", shell, StringComparison.Ordinal);
    }
}
