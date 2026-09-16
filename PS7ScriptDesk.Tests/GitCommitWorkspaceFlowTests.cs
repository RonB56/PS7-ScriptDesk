using System.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitCommitWorkspaceFlowTests
{
    [Fact]
    public async Task SuccessfulCommitClearsMessageAndKeepsCurrentBranchSelected()
    {
        var root = CreateRepository("initial.ps1", "Write-Output initial");
        try
        {
            var service = new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner()));
            var coordinator = new GitWorkspaceCoordinator(service);
            using var viewModel = CreateViewModel(service, coordinator);
            await viewModel.LoadWorkspaceFolderAsync(root);
            using var workspace = new GitWorkspaceViewModel(coordinator, viewModel);

            viewModel.CommitMessage = "UAT test commit";
            Assert.True(viewModel.CommitCommand.CanExecute(null));

            await viewModel.CommitAsync();

            Assert.Equal(string.Empty, viewModel.CommitMessage);
            Assert.False(viewModel.CommitCommand.CanExecute(null));
            Assert.Equal("main", workspace.Branches!.SelectedBranch?.Name);
            Assert.Contains(workspace.Branches.SelectedBranch, workspace.Branches.Branches);
            Assert.DoesNotContain(coordinator.CurrentState!.Changes, change => change.IsStaged);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public async Task FailedCommitPreservesMessage()
    {
        var root = CreateRepository("initial.ps1", "Write-Output initial");
        try
        {
            var runner = new FailingCommitRunner();
            var service = new GitService(runner, new GitRepositoryLocator(runner));
            var coordinator = new GitWorkspaceCoordinator(service);
            using var viewModel = CreateViewModel(service, coordinator);
            await viewModel.LoadWorkspaceFolderAsync(root);

            var secondPath = Path.Combine(root, "second.ps1");
            await File.WriteAllTextAsync(secondPath, "Write-Output second");
            RunGit(root, "add", "--", "second.ps1");
            await viewModel.LoadWorkspaceFolderAsync(root);
            viewModel.CommitMessage = "Preserve this message";

            await viewModel.CommitAsync();

            Assert.Equal("Preserve this message", viewModel.CommitMessage);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    private static MainWindowViewModel CreateViewModel(GitService service, GitWorkspaceCoordinator coordinator)
        => new(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            new FakeLiveConsoleService(),
            new FakeExeExportService(),
            gitService: service,
            gitWorkspaceCoordinator: coordinator);

    private static string CreateRepository(string fileName, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-GitWorkspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.name", "ScriptDesk Test");
        RunGit(root, "config", "user.email", "scriptdesk@example.invalid");
        File.WriteAllText(Path.Combine(root, fileName), content);
        RunGit(root, "add", "--", fileName);
        return root;
    }

    private static string RunGit(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static void DeleteRepository(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FailingCommitRunner : IGitCommandRunner
    {
        private readonly GitCommandRunner _inner = new();

        public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
            => _inner.GetEnvironmentAsync(cancellationToken);

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => arguments.Count > 0 && string.Equals(arguments[0], "commit", StringComparison.Ordinal)
                ? Task.FromResult(new GitCommandResult(false, 1, string.Empty, "simulated commit failure", TimeSpan.Zero, false, false, "SimulatedFailure"))
                : _inner.RunAsync(workingDirectory, arguments, timeout, cancellationToken);
    }
}
