using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitBranchServiceTests
{
    [Fact]
    public async Task BranchListingUsesStructuredRefsAndPreservesRemoteSlashNames()
    {
        var runner = new RecordingBranchRunner("refs/heads/main\0origin/main\0abc123\0*\0refs/heads/feature/api\0\0def456\0 \0refs/remotes/origin/feature/api\0\0def456\0 \0");
        var service = new GitService(runner, new FixedLocator());

        var state = await service.GetBranchesAsync(Environment.CurrentDirectory);

        Assert.Equal("main", state.CurrentBranch);
        Assert.False(state.IsDetachedHead);
        Assert.Contains(state.Branches, branch => branch.Name == "feature/api" && !branch.IsRemote);
        Assert.Contains(state.Branches, branch => branch.Name == "origin/feature/api" && branch.IsRemote);
    }

    [Fact]
    public async Task BranchMutationsUseSafeSwitchAndNonForceDeleteArguments()
    {
        var runner = new RecordingBranchRunner(string.Empty);
        var service = new GitService(runner, new FixedLocator());

        await service.CreateBranchAsync(Environment.CurrentDirectory, "feature/test", switchTo: true);
        Assert.Equal(new[] { "switch", "-c", "feature/test" }, runner.Arguments);
        await service.DeleteBranchAsync(Environment.CurrentDirectory, "feature/test");
        Assert.Equal(new[] { "branch", "-d", "feature/test" }, runner.Arguments);
        Assert.DoesNotContain("-D", runner.Arguments);
    }

    private sealed class RecordingBranchRunner(string output) : IGitCommandRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitEnvironmentInfo(true, "git", "2", null, null));
        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            var isSymbolicRef = arguments.Contains("symbolic-ref");
            var isHead = arguments.Contains("rev-parse");
            var stdout = isSymbolicRef ? "main\n" : isHead ? "abc123\n" : output;
            return Task.FromResult(new GitCommandResult(true, 0, stdout, string.Empty, TimeSpan.Zero, false, false, null));
        }
    }

    private sealed class FixedLocator : IGitRepositoryLocator
    {
        public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitRepositoryInfo(true, folderPath, folderPath, "main", false, "abc123", false, false, false, null, null));
    }
}
