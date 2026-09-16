using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitUatBatch22Tests
{
    [Fact]
    public void InitializeHasSpecificOperationPresentation()
        => Assert.Equal("Initializing repository...", GitOperationPresentation.GetBusyText(GitOperationKind.Initialize));

    [Fact]
    public async Task RealLocalInitializeCreatesGitMetadataAndDetectsUnbornRepository()
    {
        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-batch22-init-");
        try
        {
            var runner = new GitCommandRunner();
            var service = new GitService(runner, new GitRepositoryLocator(runner));

            var result = await service.InitializeAsync(root.FullName);

            Assert.True(result.Success, result.StandardError);
            Assert.True(Directory.Exists(Path.Combine(root.FullName, ".git")));
            var detected = await service.DetectRepositoryAsync(root.FullName);
            Assert.True(detected.IsRepository);
            Assert.Equal(Path.GetFullPath(root.FullName), detected.WorkingTreeRoot, ignoreCase: true);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void InitializeLifecycleUsesSharedBusyStateAndCleansUpInFinally()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        Assert.Contains("CurrentGitOperation = GitOperationKind.Initialize", viewModel, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"Initializing repository...\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsGitMutationInProgress = false", viewModel, StringComparison.Ordinal);
        Assert.Contains("CurrentGitOperation = GitOperationKind.None", viewModel, StringComparison.Ordinal);
        Assert.Contains("Repository initialization service call started", viewModel, StringComparison.Ordinal);
        Assert.Contains("Authoritative repository refresh started after initialization", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void InitializeFailureUsesConcisePresentationAndDoesNotReuseRawFailureText()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        Assert.Contains("StatusText = SummarizeGitFailure(\"Initialize\", result)", viewModel, StringComparison.Ordinal);
        Assert.Contains("stderrPreview", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusText = DescribeGitFailure(\"Initialize\"", viewModel, StringComparison.Ordinal);
    }
}
