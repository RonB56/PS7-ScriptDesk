using System.Diagnostics;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public sealed class GitRepositoryLocator : IGitRepositoryLocator
{
    private readonly IGitCommandRunner _commandRunner;

    public GitRepositoryLocator(IGitCommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    public async Task<GitRepositoryInfo> DetectRepositoryAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return GitRepositoryInfo.NotRepository("No folder was supplied.", "InvalidPath");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(folderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return GitRepositoryInfo.NotRepository("The folder path is invalid.", "InvalidPath");
        }

        if (!Directory.Exists(normalizedPath))
        {
            return GitRepositoryInfo.NotRepository("The folder does not exist.", "MissingFolder");
        }

        var result = await _commandRunner.RunAsync(
            normalizedPath,
            new[] { "rev-parse", "--show-toplevel", "--absolute-git-dir", "--is-inside-work-tree", "--is-bare-repository", "--is-inside-git-dir" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            var notRepository = GitRepositoryInfo.NotRepository(
                "The folder is not inside a usable Git repository.",
                result.FailureKind is null or "GitExitCode" ? "NotRepository" : result.FailureKind);
            DeveloperDiagnostics.LogInfo("Git", "Repository detection found no usable repository.", new Dictionary<string, object?>
            {
                ["folderPath"] = normalizedPath,
                ["failureKind"] = notRepository.FailureKind,
                ["durationMilliseconds"] = stopwatch.ElapsedMilliseconds
            });
            return notRepository;
        }

        var lines = result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 5)
        {
            return GitRepositoryInfo.NotRepository("Git returned an incomplete repository context.", "MalformedOutput");
        }

        var repositoryRoot = NormalizePath(lines[0]);
        var gitDirectory = NormalizePath(lines[1]);
        var insideWorkTree = ParseGitBoolean(lines[2]);
        var isBareRepository = ParseGitBoolean(lines[3]);
        var insideGitDirectory = ParseGitBoolean(lines[4]);
        var branchResult = await _commandRunner.RunAsync(
            normalizedPath,
            new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var headResult = await _commandRunner.RunAsync(
            normalizedPath,
            new[] { "rev-parse", "--short", "HEAD" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var currentBranch = branchResult.Success
            ? FirstOutputLine(branchResult.StandardOutput)
            : null;
        var isDetachedHead = !string.IsNullOrWhiteSpace(repositoryRoot) && string.IsNullOrWhiteSpace(currentBranch);
        var headCommit = headResult.Success ? FirstOutputLine(headResult.StandardOutput) : null;
        var workingTreeRoot = insideWorkTree ? repositoryRoot : null;
        var isSubmodule = !string.IsNullOrWhiteSpace(gitDirectory) &&
                          gitDirectory.Contains(Path.Combine(".git", "modules"), StringComparison.OrdinalIgnoreCase);

        var info = new GitRepositoryInfo(
            IsRepository: !string.IsNullOrWhiteSpace(repositoryRoot) || isBareRepository || insideGitDirectory,
            RepositoryRoot: repositoryRoot,
            WorkingTreeRoot: workingTreeRoot,
            CurrentBranch: currentBranch,
            IsDetachedHead: isDetachedHead,
            HeadCommit: headCommit,
            IsBareRepository: isBareRepository,
            IsWorktree: insideWorkTree && !string.Equals(gitDirectory, Path.Combine(repositoryRoot ?? string.Empty, ".git"), StringComparison.OrdinalIgnoreCase),
            IsSubmodule: isSubmodule,
            Error: null,
            FailureKind: null);

        DeveloperDiagnostics.LogInfo("Git", "Repository detected.", new Dictionary<string, object?>
        {
            ["folderPath"] = normalizedPath,
            ["repositoryRoot"] = info.RepositoryRoot,
            ["currentBranch"] = info.CurrentBranch,
            ["isDetachedHead"] = info.IsDetachedHead,
            ["isBareRepository"] = info.IsBareRepository,
            ["isWorktree"] = info.IsWorktree,
            ["isSubmodule"] = info.IsSubmodule,
            ["durationMilliseconds"] = stopwatch.ElapsedMilliseconds
        });
        return info;
    }

    private static string? NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return value.Trim();
        }
    }

    private static string? FirstOutputLine(string output)
        => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static bool ParseGitBoolean(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
