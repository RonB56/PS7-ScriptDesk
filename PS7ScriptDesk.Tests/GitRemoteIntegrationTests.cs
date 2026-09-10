using System.Diagnostics;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitRemoteIntegrationTests
{
    [Fact]
    public async Task LocalBareRemoteSupportsPublishAndFetchWithoutForce()
    {
        var root = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-Remote-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "remote.git");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init", "--bare", bare);
            RunGit(root, "init", work);
            RunGit(work, "config", "user.name", "ScriptDesk Test");
            RunGit(work, "config", "user.email", "scriptdesk@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(work, "remote.ps1"), "Write-Output remote\n");
            RunGit(work, "add", "--", "remote.ps1");
            RunGit(work, "commit", "-m", "initial");
            RunGit(work, "remote", "add", "origin", bare);
            var branch = RunGit(work, "branch", "--show-current").Trim();
            var service = new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner()));

            var publish = await service.PublishBranchAsync(work, "origin", branch);
            var fetch = await service.FetchAsync(work);

            Assert.True(publish.Success, publish.StandardError);
            Assert.True(fetch.Success, fetch.StandardError);
            Assert.Contains(branch, RunGit(bare, "for-each-ref", "--format=%(refname:short)", "refs/heads"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
}
