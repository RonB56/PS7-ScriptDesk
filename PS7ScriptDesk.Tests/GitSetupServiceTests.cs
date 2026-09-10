using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitSetupServiceTests
{
    [Fact]
    public async Task CloneUsesParentWorkingDirectoryAndSafeArguments()
    {
        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-clone-");
        try
        {
            var runner = new RecordingRunner();
            var service = new GitService(runner, new FixedLocator());
            var destination = Path.Combine(root.FullName, "Clone With Spaces");
            var result = await service.CloneAsync("file:///source/repo.git", destination);
            Assert.True(result.Success);
            Assert.Equal(root.FullName, runner.WorkingDirectory, ignoreCase: true);
            Assert.Equal(new[] { "clone", "file:///source/repo.git", destination }, runner.Arguments);
            Assert.Equal(TimeSpan.FromMinutes(10), runner.Timeout);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task InitializeUsesSelectedFolderAndDoesNotAddOtherArguments()
    {
        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-init-");
        try
        {
            var runner = new RecordingRunner();
            var service = new GitService(runner, new FixedLocator());
            var result = await service.InitializeAsync(root.FullName);
            Assert.True(result.Success);
            Assert.Equal(root.FullName, runner.WorkingDirectory, ignoreCase: true);
            Assert.Equal(new[] { "init" }, runner.Arguments);
        }
        finally { root.Delete(true); }
    }

    private sealed class RecordingRunner : IGitCommandRunner
    {
        public string WorkingDirectory { get; private set; } = string.Empty;
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public TimeSpan? Timeout { get; private set; }
        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GitEnvironmentInfo(true, "git", "2", null, null));
        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        { WorkingDirectory = workingDirectory; Arguments = arguments; Timeout = timeout; return Task.FromResult(new GitCommandResult(true, 0, string.Empty, string.Empty, TimeSpan.Zero, false, false, null)); }
    }

    private sealed class FixedLocator : IGitRepositoryLocator
    {
        public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default) => Task.FromResult(GitRepositoryInfo.NotRepository());
    }
}
