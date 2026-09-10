using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public sealed class GitService : IGitService
{
    private readonly IGitCommandRunner _commandRunner;
    private readonly IGitRepositoryLocator _repositoryLocator;

    public GitService(IGitCommandRunner commandRunner, IGitRepositoryLocator repositoryLocator)
    {
        _commandRunner = commandRunner;
        _repositoryLocator = repositoryLocator;
    }

    public Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
        => _commandRunner.GetEnvironmentAsync(cancellationToken);

    public Task<GitCommandResult> CloneAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) return Task.FromResult(GitCommandResult.Failed("InvalidDestination", "A clone source and destination are required.", TimeSpan.Zero));
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
        return parent is null || !Directory.Exists(parent)
            ? Task.FromResult(GitCommandResult.Failed("InvalidDestination", "The clone destination parent folder does not exist.", TimeSpan.Zero))
            : _commandRunner.RunAsync(parent, new[] { "clone", source, destination }, TimeSpan.FromMinutes(10), cancellationToken);
    }

    public Task<GitCommandResult> InitializeAsync(string folderPath, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)
            ? Task.FromResult(GitCommandResult.Failed("InvalidFolder", "The selected folder does not exist.", TimeSpan.Zero))
            : RunOperationAsync(folderPath, new[] { "init" }, cancellationToken, TimeSpan.FromSeconds(60));

    public Task<GitRepositoryInfo> DetectRepositoryAsync(string folderPath, CancellationToken cancellationToken = default)
        => _repositoryLocator.DetectRepositoryAsync(folderPath, cancellationToken);

    public Task<GitCommandResult> StageFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default)
        => RunPathOperationAsync(repositoryRoot, new[] { "add", "--", relativePath }, cancellationToken);

    public Task<GitCommandResult> StageAllAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "add", "--all", "--", "." }, cancellationToken);

    public Task<GitCommandResult> UnstageFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default)
        => RunPathOperationAsync(repositoryRoot, new[] { "restore", "--staged", "--", relativePath }, cancellationToken);

    public Task<GitCommandResult> UnstageAllAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "restore", "--staged", "--", "." }, cancellationToken);

    public Task<GitCommandResult> DiscardFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken = default)
        => RunPathOperationAsync(repositoryRoot, new[] { "restore", "--worktree", "--", relativePath }, cancellationToken);

    public async Task<GitCommandResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return GitCommandResult.Failed("InvalidMessage", "A commit message is required.", TimeSpan.Zero);
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return GitCommandResult.Failed("InvalidRepository", "The Git repository is no longer available.", TimeSpan.Zero);

        var messagePath = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-commit-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(messagePath, message, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var result = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), new[] { "commit", "-F", messagePath }, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            try { if (File.Exists(messagePath)) File.Delete(messagePath); } catch (IOException) { }
        }
    }

    public async Task<GitBranchState> GetBranchesAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return new(Array.Empty<GitBranch>(), null, false, null, "The Git repository is unavailable.");
        var root = Path.GetFullPath(repositoryRoot);
        var result = await _commandRunner.RunAsync(root,
            new[] { "for-each-ref", "--format=%(refname)%00%(upstream:short)%00%(objectname:short)%00%(HEAD)%00", "refs/heads", "refs/remotes" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success) return new(Array.Empty<GitBranch>(), null, false, null, result.StandardError.Trim());
        var currentResult = await _commandRunner.RunAsync(root, new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var headResult = await _commandRunner.RunAsync(root, new[] { "rev-parse", "--short", "HEAD" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var current = FirstLine(currentResult.StandardOutput);
        var head = FirstLine(headResult.StandardOutput);
        var detached = !currentResult.Success && !string.IsNullOrWhiteSpace(head);
        var fields = result.StandardOutput.Split('\0', StringSplitOptions.None);
        var branches = new List<GitBranch>();
        for (var i = 0; i + 3 < fields.Length; i += 4)
        {
            var fullName = fields[i];
            if (fullName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var remote = fullName.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = remote ? fullName[13..] : fullName[11..];
            branches.Add(new(name, fullName, fields[i + 3] == "*", remote, string.IsNullOrWhiteSpace(fields[i + 1]) ? null : fields[i + 1], null, null, fields[i + 2]));
        }
        for (var i = 0; i < branches.Count; i++)
        {
            var branch = branches[i];
            if (branch.IsRemote || string.IsNullOrWhiteSpace(branch.UpstreamName)) continue;
            var tracking = await _commandRunner.RunAsync(root, new[] { "rev-list", "--left-right", "--count", $"{branch.Name}...{branch.UpstreamName}" }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var parts = tracking.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tracking.Success && parts.Length >= 2 && int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind))
                branches[i] = branch with { AheadCount = ahead, BehindCount = behind };
        }
        return new(branches, current, detached, head);
    }

    public Task<GitCommandResult> CreateBranchAsync(string repositoryRoot, string branchName, bool switchTo, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, switchTo ? new[] { "switch", "-c", branchName } : new[] { "branch", branchName }, cancellationToken, TimeSpan.FromSeconds(60));

    public Task<GitCommandResult> SwitchBranchAsync(string repositoryRoot, string branchName, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "switch", branchName }, cancellationToken, TimeSpan.FromSeconds(60));

    public Task<GitCommandResult> RenameBranchAsync(string repositoryRoot, string oldName, string newName, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "branch", "-m", oldName, newName }, cancellationToken, TimeSpan.FromSeconds(60));

    public Task<GitCommandResult> DeleteBranchAsync(string repositoryRoot, string branchName, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "branch", "-d", branchName }, cancellationToken, TimeSpan.FromSeconds(60));

    public async Task<GitRemoteState> GetRemotesAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)) return new(Array.Empty<GitRemote>(), "The Git repository is unavailable.");
        var result = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), new[] { "remote", "-v" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success) return new(Array.Empty<GitRemote>(), result.StandardError.Trim());
        var remotes = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(group => new GitRemote(group.Key, SanitizeRemoteUrl(group.FirstOrDefault(parts => parts[2] == "(fetch)")?[1]), SanitizeRemoteUrl(group.FirstOrDefault(parts => parts[2] == "(push)")?[1])))
            .ToArray();
        return new(remotes);
    }

    public async Task<GitHistoryPage> GetHistoryPageAsync(string repositoryRoot, GitHistoryScope scope, int skip, int pageSize, string? search = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)) return new(Array.Empty<GitCommit>(), false, "The Git repository is unavailable.");
        var args = new List<string> { "log", "--date=iso-strict", "--decorate=short", $"--format=%H%x00%h%x00%P%x00%an%x00%ae%x00%aI%x00%cI%x00%s%x00%B%x00%D%x1e", $"--skip={Math.Max(0, skip)}", $"-n{Math.Max(1, pageSize) + 1}" };
        args.Add(scope == GitHistoryScope.AllLocalBranches ? "--branches" : "HEAD");
        if (!string.IsNullOrWhiteSpace(search)) args.Add($"--grep={search}");
        var result = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success) return new(Array.Empty<GitCommit>(), false, result.StandardError.Trim());
        var commits = GitHistoryParser.ParseCommits(result.StandardOutput);
        return new(commits.Take(Math.Max(1, pageSize)).ToArray(), commits.Count > pageSize);
    }

    public async Task<GitCommitDetails> GetCommitDetailsAsync(string repositoryRoot, string commitHash, CancellationToken cancellationToken = default)
    {
        var result = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), new[] { "show", "-s", "--date=iso-strict", "--decorate=short", $"--format=%H%x00%h%x00%P%x00%an%x00%ae%x00%aI%x00%cI%x00%s%x00%B%x00%D%x1e", commitHash }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success) return new(new GitCommit(commitHash, commitHash[..Math.Min(7, commitHash.Length)], Array.Empty<string>(), string.Empty, null, null, null, string.Empty, string.Empty, string.Empty), Array.Empty<GitCommitFileChange>(), result.StandardError.Trim());
        var commit = GitHistoryParser.ParseCommits(result.StandardOutput).FirstOrDefault();
        if (commit is null) return new(new GitCommit(commitHash, commitHash[..Math.Min(7, commitHash.Length)], Array.Empty<string>(), string.Empty, null, null, null, string.Empty, string.Empty, string.Empty), Array.Empty<GitCommitFileChange>(), "Git returned malformed commit metadata.");
        var filesResult = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), new[] { "show", "--format=", "--name-status", "-z", "--find-renames", commit.Hash }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(commit, filesResult.Success ? GitHistoryParser.ParseNameStatus(filesResult.StandardOutput) : Array.Empty<GitCommitFileChange>(), filesResult.Success ? null : filesResult.StandardError.Trim());
    }

    public async Task<GitDiff> GetCommitDiffAsync(string repositoryRoot, string commitHash, string relativePath, string? parentHash = null, CancellationToken cancellationToken = default)
    {
        var args = parentHash is null
            ? new[] { "show", "--format=", "--no-ext-diff", "--unified=3", "--find-renames", commitHash, "--", relativePath }
            : new[] { "diff", "--no-color", "--no-ext-diff", "--unified=3", "--find-renames", parentHash, commitHash, "--", relativePath };
        var result = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success && string.IsNullOrWhiteSpace(result.StandardOutput)) return new(relativePath, relativePath, GitDiffScope.Commit, Array.Empty<GitDiffLine>(), false, false, false, false, result.StandardError.Trim(), RepositoryRoot: repositoryRoot, RelativePath: relativePath, CommitHash: commitHash, ParentHash: parentHash);
        return GitDiffParser.Parse(result.StandardOutput, relativePath, GitDiffScope.Commit) with { RepositoryRoot = repositoryRoot, RelativePath = relativePath, CommitHash = commitHash, ParentHash = parentHash };
    }

    public Task<GitCommandResult> FetchAsync(string repositoryRoot, string? remote = null, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, string.IsNullOrWhiteSpace(remote) ? new[] { "fetch" } : new[] { "fetch", remote }, cancellationToken, TimeSpan.FromMinutes(5));

    public Task<GitCommandResult> PullAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "pull" }, cancellationToken, TimeSpan.FromMinutes(5));

    public Task<GitCommandResult> PushAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "push" }, cancellationToken, TimeSpan.FromMinutes(5));

    public Task<GitCommandResult> PublishBranchAsync(string repositoryRoot, string remote, string branchName, CancellationToken cancellationToken = default)
        => RunOperationAsync(repositoryRoot, new[] { "push", "-u", remote, branchName }, cancellationToken, TimeSpan.FromMinutes(5));

    public async Task<GitDiff> GetDiffAsync(string repositoryRoot, string relativePath, GitDiffScope scope, bool isUntracked = false, bool allowLarge = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(relativePath) || !Directory.Exists(repositoryRoot))
        {
            return new(relativePath, relativePath, scope, Array.Empty<GitDiffLine>(), false, isUntracked, false, false, "The repository is unavailable.", RepositoryRoot: repositoryRoot, RelativePath: relativePath);
        }

        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        if (isUntracked)
        {
            if (!File.Exists(fullPath))
            {
                return new("/dev/null", relativePath, scope, Array.Empty<GitDiffLine>(), false, true, false, false, "The untracked file no longer exists.", RepositoryRoot: repositoryRoot, RelativePath: relativePath);
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select((line, index) => new GitDiffLine(GitDiffLineKind.Added, line, null, index + 1)).ToArray();
            if (!allowLarge && (content.Length > 5_000_000 || lines.Length > 100_000))
                return new("/dev/null", relativePath, scope, Array.Empty<GitDiffLine>(), false, true, false, false, "This diff is very large. Use Load Anyway to display it.", IsLarge: true, RepositoryRoot: repositoryRoot, RelativePath: relativePath);
            return new("/dev/null", relativePath, scope, lines, false, true, false, false, RepositoryRoot: repositoryRoot, RelativePath: relativePath);
        }

        var arguments = scope == GitDiffScope.Staged
            ? new[] { "diff", "--cached", "--no-color", "--no-ext-diff", "--unified=3", "--", relativePath }
            : new[] { "diff", "--no-color", "--no-ext-diff", "--unified=3", "--", relativePath };
        var result = await _commandRunner.RunAsync(Path.GetFullPath(repositoryRoot), arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new(relativePath, relativePath, scope, Array.Empty<GitDiffLine>(), false, false, false, false, result.StandardError.Trim(), RepositoryRoot: repositoryRoot, RelativePath: relativePath);
        }

        if (!allowLarge && result.StandardOutput.Length > 5_000_000)
            return new(relativePath, relativePath, scope, Array.Empty<GitDiffLine>(), false, false, false, false, "This diff is very large. Use Load Anyway to display it.", IsLarge: true, RepositoryRoot: repositoryRoot, RelativePath: relativePath);

        return GitDiffParser.Parse(result.StandardOutput, relativePath, scope) with { RepositoryRoot = repositoryRoot, RelativePath = relativePath };
    }

    private Task<GitCommandResult> RunPathOperationAsync(string repositoryRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(arguments[^1]))
        {
            return Task.FromResult(GitCommandResult.Failed("InvalidPath", "A repository-relative path is required.", TimeSpan.Zero));
        }

        return RunOperationAsync(repositoryRoot, arguments, cancellationToken);
    }

    private async Task<GitCommandResult> RunOperationAsync(string repositoryRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            return GitCommandResult.Failed("InvalidRepository", "The Git repository is no longer available.", TimeSpan.Zero);
        }

        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        DeveloperDiagnostics.LogInfo("Git", "Git mutation dispatched to the repository root.", new Dictionary<string, object?>
        {
            ["repositoryRoot"] = normalizedRoot,
            ["arguments"] = arguments.ToArray(),
            ["pathKind"] = arguments.Count >= 3 && Path.IsPathFullyQualified(arguments[^1]) ? "Absolute" : "RepositoryRelative"
        });
        return await _commandRunner.RunAsync(normalizedRoot, arguments, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static string? FirstLine(string output)
        => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string? SanitizeRemoteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
        return value;
    }

    public async Task<GitRepositoryState> RefreshRepositoryStateAsync(
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        var environment = await GetEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!environment.IsAvailable)
        {
            return new GitRepositoryState(environment, GitRepositoryInfo.NotRepository(environment.Error, environment.FailureKind), workspacePath, DateTimeOffset.UtcNow);
        }

        var repository = string.IsNullOrWhiteSpace(workspacePath)
            ? GitRepositoryInfo.NotRepository("No workspace folder is open.", "NoWorkspace")
            : await DetectRepositoryAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var state = new GitRepositoryState(environment, repository, workspacePath, DateTimeOffset.UtcNow);
        if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.WorkingTreeRoot))
        {
            return state;
        }

        var statusResult = await _commandRunner.RunAsync(
            repository.WorkingTreeRoot,
            new[] { "status", "--porcelain=v2", "-z", "--untracked-files=all" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!statusResult.Success)
        {
            return state with { StatusError = statusResult.StandardError.Trim() };
        }

        var changes = GitStatusParser.Parse(statusResult.StandardOutput, repository.WorkingTreeRoot);
        var operationState = DetectOperationState(repository);
        DeveloperDiagnostics.LogInfo("Git", "Git status parsed for Source Control.", new Dictionary<string, object?>
        {
            ["repositoryRoot"] = repository.RepositoryRoot,
            ["changeCount"] = changes.Count,
            ["conflictCount"] = changes.Count(change => change.IsConflicted),
            ["untrackedCount"] = changes.Count(change => change.IsUntracked),
            ["stagedCount"] = changes.Count(change => change.IsStaged),
            ["operationState"] = operationState.ToString()
        });

        return state with
        {
            Changes = changes,
            StatusError = null,
            OperationState = operationState
        };
    }

    private static GitRepositoryOperationState DetectOperationState(GitRepositoryInfo repository)
    {
        if (string.IsNullOrWhiteSpace(repository.WorkingTreeRoot)) return GitRepositoryOperationState.None;

        var gitDirectory = Path.Combine(repository.WorkingTreeRoot, ".git");
        if (File.Exists(gitDirectory))
        {
            try
            {
                var gitFile = File.ReadAllText(gitDirectory).Trim();
                const string prefix = "gitdir:";
                if (gitFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    gitDirectory = Path.GetFullPath(Path.Combine(repository.WorkingTreeRoot, gitFile[prefix.Length..].Trim()));
            }
            catch (IOException) { return GitRepositoryOperationState.None; }
            catch (UnauthorizedAccessException) { return GitRepositoryOperationState.None; }
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD"))) return GitRepositoryOperationState.Merge;
        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply"))) return GitRepositoryOperationState.Rebase;
        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD"))) return GitRepositoryOperationState.CherryPick;
        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD"))) return GitRepositoryOperationState.Revert;
        return GitRepositoryOperationState.None;
    }
}
