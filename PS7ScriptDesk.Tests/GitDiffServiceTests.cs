using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitDiffServiceTests
{
    [Theory]
    [InlineData(GitDiffScope.Unstaged, "diff")]
    [InlineData(GitDiffScope.Staged, "--cached")]
    public async Task UsesTheRequestedDiffScopeAndSafePathArguments(GitDiffScope scope, string expectedArgument)
    {
        var runner = new RecordingRunner("--- a/File With Spaces.ps1\n+++ b/File With Spaces.ps1\n@@ -1 +1 @@\n-old\n+new\n");
        var service = new GitService(runner, new FixedLocator());

        var result = await service.GetDiffAsync(Environment.CurrentDirectory, "File With Spaces.ps1", scope);

        Assert.Null(result.Error);
        Assert.Contains(expectedArgument, runner.Arguments);
        Assert.Contains("--", runner.Arguments);
        Assert.Contains("File With Spaces.ps1", runner.Arguments);
        Assert.Equal(Environment.CurrentDirectory, runner.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public async Task LargeDiffIsBoundedUnlessExplicitlyAllowed()
    {
        var runner = new RecordingRunner(new string('x', 5_000_001));
        var service = new GitService(runner, new FixedLocator());
        var blocked = await service.GetDiffAsync(Environment.CurrentDirectory, "large.ps1", GitDiffScope.Unstaged);
        var loaded = await service.GetDiffAsync(Environment.CurrentDirectory, "large.ps1", GitDiffScope.Unstaged, allowLarge: true);
        Assert.True(blocked.IsLarge);
        Assert.Empty(blocked.Lines);
        Assert.False(loaded.IsLarge);
    }

    private sealed class RecordingRunner(string output) : IGitCommandRunner
    {
        public string WorkingDirectory { get; private set; } = string.Empty;
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GitEnvironmentInfo(true, "git", "2", null, null));
        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            WorkingDirectory = workingDirectory;
            Arguments = arguments;
            return Task.FromResult(new GitCommandResult(true, 0, output, string.Empty, TimeSpan.Zero, false, false, null));
        }
    }

    private sealed class FixedLocator : IGitRepositoryLocator
    {
        public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitRepositoryInfo(true, folderPath, folderPath, "main", false, null, false, false, false, null, null));
    }
}
