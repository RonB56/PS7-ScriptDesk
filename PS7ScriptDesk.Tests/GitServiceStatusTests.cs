using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitServiceStatusTests
{
    [Fact]
    public async Task RefreshReadsPorcelainStatusWithoutOpeningDocuments()
    {
        var runner = new RecordingGitRunner(Directory.GetCurrentDirectory());
        var service = new GitService(runner, new GitRepositoryLocator(runner));

        var state = await service.RefreshRepositoryStateAsync(Directory.GetCurrentDirectory());

        Assert.True(state.IsRepository);
        Assert.Single(state.Changes);
        Assert.Equal("Scripts/Changed.ps1", state.Changes[0].RelativePath);
        Assert.Contains(runner.Arguments, arguments => arguments.SequenceEqual(new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all" }));
    }

    [Fact]
    public async Task NonRepositoryDoesNotAttemptStatusRefresh()
    {
        var runner = new RecordingGitRunner(Directory.GetCurrentDirectory()) { RepositoryAvailable = false };
        var service = new GitService(runner, new GitRepositoryLocator(runner));

        var state = await service.RefreshRepositoryStateAsync("C:\\not-a-repo");

        Assert.False(state.IsRepository);
        Assert.Empty(state.Changes);
        Assert.DoesNotContain(runner.Arguments, arguments => arguments.Contains("status", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Phase3MutationsUseSafeRepositoryRelativeArguments()
    {
        var runner = new RecordingGitRunner(Directory.GetCurrentDirectory());
        var service = new GitService(runner, new GitRepositoryLocator(runner));

        Assert.True((await service.StageFileAsync(Directory.GetCurrentDirectory(), "Scripts/space é.ps1")).Success);
        Assert.True((await service.StageAllAsync(Directory.GetCurrentDirectory())).Success);
        Assert.True((await service.UnstageFileAsync(Directory.GetCurrentDirectory(), "Scripts/space é.ps1")).Success);
        Assert.True((await service.UnstageAllAsync(Directory.GetCurrentDirectory())).Success);
        Assert.True((await service.DiscardFileAsync(Directory.GetCurrentDirectory(), "Scripts/space é.ps1")).Success);

        Assert.Contains(runner.Arguments, arguments => arguments.SequenceEqual(new[] { "add", "--", "Scripts/space é.ps1" }));
        Assert.Contains(runner.Arguments, arguments => arguments.SequenceEqual(new[] { "add", "--all", "--", "." }));
        Assert.Contains(runner.Arguments, arguments => arguments.SequenceEqual(new[] { "restore", "--staged", "--", "Scripts/space é.ps1" }));
        Assert.Contains(runner.Arguments, arguments => arguments.SequenceEqual(new[] { "restore", "--staged", "--", "." }));
        Assert.Contains(runner.Arguments, arguments => arguments.SequenceEqual(new[] { "restore", "--worktree", "--", "Scripts/space é.ps1" }));
        Assert.All(runner.WorkingDirectories, directory => Assert.Equal(Path.GetFullPath(Directory.GetCurrentDirectory()), directory));
    }

    [Fact]
    public async Task StageFile_UsesRepositoryRootForRootNestedSpaceAndUnicodePaths()
    {
        var repositoryRoot = Path.Combine(AppContext.BaseDirectory, $"PS7ScriptDesk-GitStage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryRoot);

        try
        {
            var runner = new GitCommandRunner();
            var service = new GitService(runner, new GitRepositoryLocator(runner));

            Assert.True((await runner.RunAsync(repositoryRoot, new[] { "init" })).Success);
            Assert.True((await runner.RunAsync(repositoryRoot, new[] { "config", "user.email", "scriptdesk-tests@example.invalid" })).Success);
            Assert.True((await runner.RunAsync(repositoryRoot, new[] { "config", "user.name", "ScriptDesk Tests" })).Success);

            var trackedPath = Path.Combine(repositoryRoot, "Tracked.txt");
            var nestedPath = Path.Combine(repositoryRoot, "Nested", "Space File.txt");
            var unicodePath = Path.Combine(repositoryRoot, "Unicode", "Test-Überprüfung.txt");
            var rootUntrackedPath = Path.Combine(repositoryRoot, "Untracked-Test.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(unicodePath)!);
            File.WriteAllText(trackedPath, "before");
            Assert.True((await runner.RunAsync(repositoryRoot, new[] { "add", "--", "Tracked.txt" })).Success);
            Assert.True((await runner.RunAsync(repositoryRoot, new[] { "commit", "-m", "baseline" })).Success);

            File.WriteAllText(trackedPath, "after");
            File.WriteAllText(nestedPath, "nested");
            File.WriteAllText(unicodePath, "unicode");
            File.WriteAllText(rootUntrackedPath, "root");

            foreach (var relativePath in new[] { "Tracked.txt", "Nested/Space File.txt", "Unicode/Test-Überprüfung.txt", "Untracked-Test.txt" })
            {
                var result = await service.StageFileAsync(repositoryRoot, relativePath);
                Assert.True(result.Success, result.StandardError);
            }

            var state = await service.RefreshRepositoryStateAsync(repositoryRoot);
            Assert.True(state.IsRepository);
            Assert.All(new[] { "Tracked.txt", "Nested/Space File.txt", "Unicode/Test-Überprüfung.txt", "Untracked-Test.txt" }, relativePath =>
                Assert.Contains(state.Changes, change => string.Equals(change.RelativePath.Replace('\\', '/'), relativePath, StringComparison.Ordinal)));
            Assert.All(state.Changes, change => Assert.True(change.IsStaged));
            Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), Path.GetFullPath(repositoryRoot));
        }
        finally
        {
            if (Directory.Exists(repositoryRoot))
            {
                try
                {
                    Directory.Delete(repositoryRoot, recursive: true);
                }
                catch (UnauthorizedAccessException)
                {
                    // The elevated Git test process can create protected object files; the exact
                    // isolated directory is removed by the test runner host after the test.
                }
            }
        }
    }

    private sealed class RecordingGitRunner : IGitCommandRunner
    {
        public RecordingGitRunner(string repositoryRoot) => RepositoryRoot = repositoryRoot;

        private string RepositoryRoot { get; }
        public bool RepositoryAvailable { get; init; } = true;
        public List<IReadOnlyList<string>> Arguments { get; } = new();
        public List<string> WorkingDirectories { get; } = new();

        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitEnvironmentInfo(true, "git.exe", "2.0", null, null));

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Arguments.Add(arguments);
            WorkingDirectories.Add(workingDirectory);
            if (arguments.SequenceEqual(new[] { "rev-parse", "--show-toplevel", "--absolute-git-dir", "--is-inside-work-tree", "--is-bare-repository", "--is-inside-git-dir" }))
            {
                return Task.FromResult(RepositoryAvailable
                    ? Success($"{RepositoryRoot}\n{Path.Combine(RepositoryRoot, ".git")}\ntrue\nfalse\nfalse\n")
                    : GitCommandResult.Failed("GitExitCode", "not a repository", TimeSpan.Zero));
            }

            if (arguments.SequenceEqual(new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }))
            {
                return Task.FromResult(Success("main\n"));
            }

            if (arguments.SequenceEqual(new[] { "rev-parse", "--short", "HEAD" }))
            {
                return Task.FromResult(Success("abc123\n"));
            }

            if (arguments.SequenceEqual(new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all" }))
            {
                return Task.FromResult(Success("1 .M N... 100644 100644 100644 abc def Scripts/Changed.ps1\0"));
            }

            if (arguments.SequenceEqual(new[] { "add", "--", "Scripts/space é.ps1" }) ||
                arguments.SequenceEqual(new[] { "add", "--all", "--", "." }) ||
                arguments.SequenceEqual(new[] { "restore", "--staged", "--", "Scripts/space é.ps1" }) ||
                arguments.SequenceEqual(new[] { "restore", "--staged", "--", "." }) ||
                arguments.SequenceEqual(new[] { "restore", "--worktree", "--", "Scripts/space é.ps1" }))
            {
                return Task.FromResult(Success(string.Empty));
            }

            throw new InvalidOperationException($"Unexpected Git arguments: {string.Join(' ', arguments)}");
        }

        private static GitCommandResult Success(string output)
            => new(true, 0, output, string.Empty, TimeSpan.Zero, false, false, null);
    }
}
