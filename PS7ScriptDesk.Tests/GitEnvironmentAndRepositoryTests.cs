using System.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitEnvironmentAndRepositoryTests
{
    [Fact]
    public async Task GitEnvironment_DiscoveryReturnsAvailableVersionAndExecutable()
    {
        var runner = new GitCommandRunner();

        var environment = await runner.GetEnvironmentAsync();

        Assert.True(environment.IsAvailable, environment.Error);
        Assert.False(string.IsNullOrWhiteSpace(environment.ExecutablePath));
        Assert.False(string.IsNullOrWhiteSpace(environment.Version));
    }

    [Fact]
    public async Task GitCommandRunner_CapturesStdoutAndExitCode()
    {
        var runner = new GitCommandRunner();
        using var workspace = TemporaryGitWorkspace.Create();

        var result = await runner.RunAsync(workspace.Root, new[] { "--version" });

        Assert.True(result.Success, result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task GitCommandRunnerScopesSafeDirectoryToTheSelectedWorkingDirectory()
    {
        var runner = new GitCommandRunner();
        using var workspace = TemporaryGitWorkspace.Create();

        var init = await runner.RunAsync(workspace.Root, new[] { "init", "-q" });
        Assert.True(init.Success, init.StandardError);
        var result = await runner.RunAsync(workspace.Root, new[] { "rev-parse", "--show-toplevel" });

        Assert.True(result.Success, result.StandardError);
    }

    [Fact]
    public async Task GitCommandRunner_CapturesStderrAndExitCode()
    {
        var runner = new GitCommandRunner();
        using var workspace = TemporaryGitWorkspace.Create();

        var result = await runner.RunAsync(workspace.Root, new[] { "--definitely-not-a-real-git-command" });

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardError));
        Assert.Equal("GitExitCode", result.FailureKind);
    }

    [Fact]
    public async Task GitCommandRunner_ReportsCancellationAndTimeout()
    {
        var runner = new GitCommandRunner();
        using var workspace = TemporaryGitWorkspace.Create();
        await runner.GetEnvironmentAsync();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await runner.RunAsync(workspace.Root, new[] { "--version" }, cancellationToken: cancellation.Token);

        Assert.True(canceled.WasCancelled);
        Assert.Equal("Cancelled", canceled.FailureKind);

        var timedOut = await runner.RunAsync(workspace.Root, new[] { "--version" }, TimeSpan.Zero);
        Assert.True(timedOut.TimedOut);
        Assert.Equal("Timeout", timedOut.FailureKind);
    }

    [Fact]
    public async Task RepositoryLocator_DetectsRootFromNestedUnicodeSpacePath()
    {
        using var workspace = TemporaryGitWorkspace.Create("PS7ScriptDesk Git é");
        var runner = new GitCommandRunner();
        var init = await runner.RunAsync(workspace.Root, new[] { "init", "-q" });
        Assert.True(init.Success, init.StandardError);

        var nested = Directory.CreateDirectory(Path.Combine(workspace.Root, "Nested Folder", "Scripts"));
        var locator = new GitRepositoryLocator(runner);

        var repository = await locator.DetectRepositoryAsync(nested.FullName);

        Assert.True(repository.IsRepository, repository.Error);
        Assert.Equal(Path.GetFullPath(workspace.Root), repository.RepositoryRoot);
        Assert.Equal(Path.GetFullPath(workspace.Root), repository.WorkingTreeRoot);
        Assert.False(repository.IsBareRepository);
        Assert.False(repository.IsDetachedHead);
    }

    [Fact]
    public async Task RepositoryLocator_ReturnsStructuredNonRepositoryAndMissingFolderStates()
    {
        using var workspace = TemporaryGitWorkspace.Create();
        var runner = new GitCommandRunner();
        var locator = new GitRepositoryLocator(runner);

        var nonRepository = await locator.DetectRepositoryAsync(workspace.Root);
        var missing = await locator.DetectRepositoryAsync(Path.Combine(workspace.Root, "missing"));

        Assert.False(nonRepository.IsRepository);
        Assert.Equal("NotRepository", nonRepository.FailureKind);
        Assert.False(missing.IsRepository);
        Assert.Equal("MissingFolder", missing.FailureKind);
    }

    [Fact]
    public void MainWindow_ContainsGitMenuAfterToolsBeforeHelpAndUsesThemeBindings()
    {
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var toolsIndex = xaml.IndexOf("Header=\"_Tools\"", StringComparison.Ordinal);
        var gitIndex = xaml.IndexOf("Header=\"_Git\"", StringComparison.Ordinal);
        var helpIndex = xaml.IndexOf("Header=\"_Help\"", StringComparison.Ordinal);

        Assert.True(toolsIndex >= 0);
        Assert.True(gitIndex > toolsIndex);
        Assert.True(helpIndex > gitIndex);
        Assert.Contains("Command=\"{Binding RefreshGitStatusCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenGitWorkspace_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeStatusBarSeparatorStyle}\"", xaml, StringComparison.Ordinal);
    }

    private sealed class TemporaryGitWorkspace : IDisposable
    {
        private TemporaryGitWorkspace(string root) => Root = root;

        public string Root { get; }

        public static TemporaryGitWorkspace Create(string? suffix = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-Git-{suffix ?? Guid.NewGuid().ToString("N")}");
            Directory.CreateDirectory(root);
            return new TemporaryGitWorkspace(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Test cleanup is best effort; the directory is uniquely generated.
            }
        }
    }
}
