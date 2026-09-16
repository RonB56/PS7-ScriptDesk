using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitBranchServiceTests
{
    [Theory]
    [InlineData("refs/heads/main", false, "main")]
    [InlineData("refs/heads/feature/test", false, "feature/test")]
    [InlineData("refs/heads/name-with_dash-π", false, "name-with_dash-π")]
    [InlineData("refs/remotes/origin/feature-x", true, "origin/feature-x")]
    public void BranchModelSeparatesRawRefFromOperationAndDisplayName(string rawRef, bool isRemote, string expected)
    {
        var branch = new GitBranch(rawRef, rawRef, false, isRemote, null, null, null, "abc");

        Assert.Equal(rawRef, branch.FullName);
        Assert.Equal(expected, branch.OperationName);
        Assert.Equal(expected, branch.DisplayName);
    }

    [Fact]
    public void DetachedHeadRemainsExplicit()
    {
        var state = new GitBranchState(Array.Empty<GitBranch>(), null, true, "abc123");

        Assert.True(state.IsDetachedHead);
        Assert.Null(state.CurrentBranch);
    }

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
        Assert.DoesNotContain(state.Branches.Where(branch => !branch.IsRemote), branch => branch.Name.StartsWith('/'));
    }

    [Fact]
    public async Task BranchListingNormalizesLocalRefNamespaceWithoutRemovingHierarchy()
    {
        var runner = new RecordingBranchRunner("refs/heads/main\0\0abc123\0*\0refs/heads/uat-branch\0\0def456\0 \0refs/heads/feature/foo\0\0ghi789\0 \0");
        var service = new GitService(runner, new FixedLocator());

        var state = await service.GetBranchesAsync(Environment.CurrentDirectory);

        Assert.Contains(state.Branches, branch => !branch.IsRemote && branch.Name == "main");
        Assert.Contains(state.Branches, branch => !branch.IsRemote && branch.Name == "uat-branch");
        Assert.Contains(state.Branches, branch => !branch.IsRemote && branch.Name == "feature/foo");
        Assert.DoesNotContain(state.Branches.Where(branch => !branch.IsRemote), branch => branch.Name.StartsWith('/'));
    }

    [Fact]
    public async Task BranchListingTrimsNewlineRecordSeparatorsBeforeCanonicalization()
    {
        var runner = new RecordingBranchRunner("refs/heads/main\0\0abc123\0*\0\nrefs/heads/uat-branch\0\0def456\0 \0\nrefs/remotes/origin/feature-x\0\0ghi789\0 \0\n");
        var service = new GitService(runner, new FixedLocator());

        var state = await service.GetBranchesAsync(Environment.CurrentDirectory);

        Assert.Equal(["main", "uat-branch", "origin/feature-x"], state.Branches.Select(branch => branch.Name));
        Assert.Equal("refs/heads/uat-branch", state.Branches[1].FullName);
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

    [Fact]
    public async Task RealGitServiceRenameAndSwitchPreserveCanonicalBranchProjection()
    {
        using var repository = TemporaryRepository.Create();
        var runner = new GitCommandRunner();
        var service = new GitService(runner, new GitRepositoryLocator(runner));

        Assert.True((await runner.RunAsync(repository.Root, new[] { "init", "-q" })).Success);
        Assert.True((await runner.RunAsync(repository.Root, new[] { "config", "user.email", "test@example.invalid" })).Success);
        Assert.True((await runner.RunAsync(repository.Root, new[] { "config", "user.name", "ScriptDesk Test" })).Success);
        File.WriteAllText(Path.Combine(repository.Root, "README.md"), "test");
        Assert.True((await runner.RunAsync(repository.Root, new[] { "add", "README.md" })).Success);
        Assert.True((await runner.RunAsync(repository.Root, new[] { "commit", "-m", "initial" })).Success);
        Assert.True((await runner.RunAsync(repository.Root, new[] { "branch", "-m", "main" })).Success);
        Assert.True((await service.CreateBranchAsync(repository.Root, "uat-branch", switchTo: false)).Success);
        Assert.True((await service.CreateBranchAsync(repository.Root, "uat-create-only", switchTo: false)).Success);

        Assert.True((await service.RenameBranchAsync(repository.Root, "uat-branch", "uat-renamed")).Success);
        Assert.True((await service.SwitchBranchAsync(repository.Root, "uat-renamed")).Success);
        var state = await service.GetBranchesAsync(repository.Root);

        Assert.Contains(state.Branches, branch => branch.Name == "uat-renamed" && branch.FullName == "refs/heads/uat-renamed" && !branch.DisplayName.StartsWith("refs/heads/", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Branches, branch => branch.Name == "uat-branch");
        Assert.Equal("uat-renamed", state.CurrentBranch);
        Assert.True((await service.SwitchBranchAsync(repository.Root, "main")).Success);
        Assert.Equal("main", (await service.GetBranchesAsync(repository.Root)).CurrentBranch);
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

    private sealed class TemporaryRepository : IDisposable
    {
        private TemporaryRepository(string root) => Root = root;
        public string Root { get; }
        public static TemporaryRepository Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-Batch15-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TemporaryRepository(root);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
