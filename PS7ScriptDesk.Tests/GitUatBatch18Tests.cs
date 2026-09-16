using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitUatBatch18Tests
{
    [Fact]
    public void CloneValidationRejectsIncompleteUrlAndMissingDestination()
    {
        Assert.Contains("complete repository URL", GitCloneValidation.Validate("https://", "C:\\temp", "repo"), StringComparison.Ordinal);
        Assert.Contains("destination parent", GitCloneValidation.Validate("https://github.com/org/repo.git", "", "repo"), StringComparison.Ordinal);
    }

    [Fact]
    public void CloneValidationAcceptsCorrectedInputsAndRejectsInvalidFolderName()
    {
        Assert.Equal(string.Empty, GitCloneValidation.Validate("https://github.com/org/repo.git", "C:\\temp", "repo"));
        Assert.Contains("folder name", GitCloneValidation.Validate("https://github.com/org/repo.git", "C:\\temp", "bad<repo>"), StringComparison.Ordinal);
    }

    [Fact]
    public void GitWorkspaceBatch18SourcesExposeBusyStateAndAuthoritativeRemoteRefresh()
    {
        var window = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        Assert.Contains("ProgressBar", window, StringComparison.Ordinal);
        Assert.Contains("IsGitMutationInProgress", window, StringComparison.Ordinal);
        Assert.Contains("_gitWorkspaceCoordinator?.PublishRemoteState(remoteState)", viewModel, StringComparison.Ordinal);
        Assert.Contains("Sync completed", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusText = $\"{operation} unavailable: no remote is configured", viewModel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GitOperationKind.Fetch, "Fetching...")]
    [InlineData(GitOperationKind.Pull, "Pulling...")]
    [InlineData(GitOperationKind.Push, "Pushing...")]
    [InlineData(GitOperationKind.Sync, "Syncing...")]
    [InlineData(GitOperationKind.Clone, "Cloning repository...")]
    public void GitOperationPresentationUsesOperationSpecificBusyText(GitOperationKind operation, string expected)
        => Assert.Equal(expected, GitOperationPresentation.GetBusyText(operation));

    [Fact]
    public void Batch19PreservesSyncOwnershipAndCondensesCloneErrors()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var prompt = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Services", "UserPromptService.cs");
        Assert.Contains("CurrentGitOperation = GitOperationKind.Sync", viewModel, StringComparison.Ordinal);
        Assert.Contains("SummarizeGitFailure", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusText = DescribeGitFailure(\"Clone\"", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Content = \"Clone\", IsDefault = true", prompt, StringComparison.Ordinal);
    }
}
