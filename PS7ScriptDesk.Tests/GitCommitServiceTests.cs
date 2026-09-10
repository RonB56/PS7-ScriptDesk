using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using System.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class GitCommitServiceTests
{
    [Fact]
    public async Task CommitUsesSafeMessageFileTransportAndCleansItUp()
    {
        var runner = new RecordingCommitRunner();
        var service = new GitService(runner, new FixedLocator());
        const string message = "Fix quotes: 'quoted' \"value\"\nUnicode: café";

        var result = await service.CommitAsync(Environment.CurrentDirectory, message);

        Assert.True(result.Success);
        Assert.Equal(new[] { "commit", "-F", runner.MessagePath }, runner.Arguments);
        Assert.Equal(message, runner.MessageReadDuringInvocation);
        Assert.False(File.Exists(runner.MessagePath));
        Assert.Equal(TimeSpan.FromSeconds(120), runner.Timeout);
    }

    [Fact]
    public async Task RealGitCommitUsesIndexOnlyAndLeavesLaterWorkingTreeChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-GitCommit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init");
            RunGit(root, "config", "user.name", "ScriptDesk Test");
            RunGit(root, "config", "user.email", "scriptdesk@example.invalid");
            var path = Path.Combine(root, "same file.ps1");
            await File.WriteAllTextAsync(path, "one\ntwo\n");
            RunGit(root, "add", "--", "same file.ps1");
            await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");

            var service = new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner()));
            var result = await service.CommitAsync(root, "Index only ✅");

            Assert.True(result.Success, result.StandardError);
            Assert.Contains("three", await File.ReadAllTextAsync(path));
            Assert.Equal("one\ntwo\n", RunGit(root, "show", "HEAD:same file.ps1"));
            Assert.Contains(" M \"same file.ps1\"", RunGit(root, "status", "--short"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class RecordingCommitRunner : IGitCommandRunner
    {
        public string MessagePath { get; private set; } = string.Empty;
        public string MessageReadDuringInvocation { get; private set; } = string.Empty;
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
        public TimeSpan? Timeout { get; private set; }

        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitEnvironmentInfo(true, "git", "2", null, null));

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            Timeout = timeout;
            MessagePath = arguments[^1];
            MessageReadDuringInvocation = File.ReadAllText(MessagePath);
            return Task.FromResult(new GitCommandResult(true, 0, "[main abc1234] Fix", string.Empty, TimeSpan.Zero, false, false, null));
        }
    }

    private sealed class FixedLocator : IGitRepositoryLocator
    {
        public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitRepositoryInfo(true, folderPath, folderPath, "main", false, null, false, false, false, null, null));
    }
}
