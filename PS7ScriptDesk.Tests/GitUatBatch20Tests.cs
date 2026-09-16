using System.Reflection;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitUatBatch20Tests
{
    [Fact]
    public void GitWorkspaceOpenRepositoryCommandRequeriesWhenSharedMutationCompletes()
    {
        var runner = new GitCommandRunner();
        var gitService = new GitService(runner, new GitRepositoryLocator(runner));
        var coordinator = new GitWorkspaceCoordinator(gitService);
        var host = new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            new FakeLiveConsoleService(),
            new FakeExeExportService(),
            gitService: gitService,
            gitWorkspaceCoordinator: coordinator);

        using var workspace = new GitWorkspaceViewModel(coordinator, host);
        var canExecuteChanged = 0;
        workspace.OpenRepositoryCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        var mutation = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsGitMutationInProgress))!;

        Assert.True(workspace.OpenRepositoryCommand.CanExecute(null));

        mutation.SetValue(host, true);
        Assert.False(workspace.OpenRepositoryCommand.CanExecute(null));

        mutation.SetValue(host, false);
        Assert.True(workspace.OpenRepositoryCommand.CanExecute(null));
        Assert.True(canExecuteChanged >= 2);
    }

    [Fact]
    public void Batch20KeepsSharedCleanupAndWrapperRequeryWiring()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var workspace = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitWorkspaceViewModel.cs");

        Assert.Contains("IsGitMutationInProgress = false", viewModel, StringComparison.Ordinal);
        Assert.Contains("RaiseCanExecuteChanged();", workspace, StringComparison.Ordinal);
        Assert.Contains("OpenRepositoryCommand", workspace, StringComparison.Ordinal);
    }
}
