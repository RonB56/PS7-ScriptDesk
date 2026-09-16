using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitRemoteServiceTests
{
    [Fact]
    public async Task RemoteListingSanitizesEmbeddedCredentials()
    {
        var runner = new RecordingRunner("origin\thttps://user:secret@example.test/repo.git (fetch)\norigin\tgit@example.test:repo.git (push)\n");
        var service = new GitService(runner, new FixedLocator());

        var state = await service.GetRemotesAsync(Environment.CurrentDirectory);

        Assert.Single(state.Remotes);
        Assert.DoesNotContain("secret", state.Remotes[0].FetchUrl);
        Assert.Contains("example.test", state.Remotes[0].FetchUrl);
    }

    [Fact]
    public async Task RemoteOperationsUseNonForceArgumentLists()
    {
        var runner = new RecordingRunner(string.Empty);
        var service = new GitService(runner, new FixedLocator());
        await service.FetchAsync(Environment.CurrentDirectory);
        Assert.Equal(new[] { "fetch" }, runner.Arguments);
        await service.PullAsync(Environment.CurrentDirectory);
        Assert.Equal(new[] { "pull" }, runner.Arguments);
        await service.PushAsync(Environment.CurrentDirectory);
        Assert.Equal(new[] { "push" }, runner.Arguments);
        Assert.DoesNotContain("--force", runner.Arguments);
    }

    [Fact]
    public async Task LocalPathRemoteOutputParsesWithoutColonSplitting()
    {
        var runner = new RecordingRunner("origin\tZ:\\Test_Git\\Remote.git (fetch)\norigin\tZ:\\Test_Git\\Remote.git (push)\n");
        var service = new GitService(runner, new FixedLocator());

        var state = await service.GetRemotesAsync(Environment.CurrentDirectory);

        var remote = Assert.Single(state.Remotes);
        Assert.Equal("origin", remote.Name);
        Assert.Equal("Z:\\Test_Git\\Remote.git", remote.FetchUrl);
        Assert.Equal("Z:\\Test_Git\\Remote.git", remote.PushUrl);
    }

    [Fact]
    public async Task SshRemoteOutputParsesWithoutBreakingColonSyntax()
    {
        var runner = new RecordingRunner("origin\tgit@github.com:owner/repo.git (fetch)\norigin\tgit@github.com:owner/repo.git (push)\n");
        var service = new GitService(runner, new FixedLocator());

        var remote = Assert.Single((await service.GetRemotesAsync(Environment.CurrentDirectory)).Remotes);

        Assert.Equal("git@github.com:owner/repo.git", remote.FetchUrl);
        Assert.Equal("git@github.com:owner/repo.git", remote.PushUrl);
    }

    private sealed class RecordingRunner(string output) : IGitCommandRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GitEnvironmentInfo(true, "git", "2", null, null));
        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            return Task.FromResult(new GitCommandResult(true, 0, output, string.Empty, TimeSpan.Zero, false, false, null));
        }
    }

    private sealed class FixedLocator : IGitRepositoryLocator
    {
        public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitRepositoryInfo(true, folderPath, folderPath, "main", false, "abc123", false, false, false, null, null));
    }
}
