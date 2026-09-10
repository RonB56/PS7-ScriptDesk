using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IGitService
{
    Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default);

    Task<GitRepositoryInfo> DetectRepositoryAsync(
        string folderPath,
        CancellationToken cancellationToken = default);

    Task<GitRepositoryState> RefreshRepositoryStateAsync(
        string? workspacePath,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> StageFileAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> StageAllAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> UnstageFileAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> UnstageAllAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> DiscardFileAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> CommitAsync(
        string repositoryRoot,
        string message,
        CancellationToken cancellationToken = default);

    Task<GitDiff> GetDiffAsync(
        string repositoryRoot,
        string relativePath,
        GitDiffScope scope,
        bool isUntracked = false,
        bool allowLarge = false,
        CancellationToken cancellationToken = default);

    Task<GitBranchState> GetBranchesAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<GitCommandResult> CreateBranchAsync(string repositoryRoot, string branchName, bool switchTo, CancellationToken cancellationToken = default);
    Task<GitCommandResult> SwitchBranchAsync(string repositoryRoot, string branchName, CancellationToken cancellationToken = default);
    Task<GitCommandResult> RenameBranchAsync(string repositoryRoot, string oldName, string newName, CancellationToken cancellationToken = default);
    Task<GitCommandResult> DeleteBranchAsync(string repositoryRoot, string branchName, CancellationToken cancellationToken = default);
    Task<GitRemoteState> GetRemotesAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<GitCommandResult> FetchAsync(string repositoryRoot, string? remote = null, CancellationToken cancellationToken = default);
    Task<GitCommandResult> PullAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<GitCommandResult> PushAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<GitCommandResult> PublishBranchAsync(string repositoryRoot, string remote, string branchName, CancellationToken cancellationToken = default);
    Task<GitHistoryPage> GetHistoryPageAsync(string repositoryRoot, GitHistoryScope scope, int skip, int pageSize, string? search = null, CancellationToken cancellationToken = default);
    Task<GitCommitDetails> GetCommitDetailsAsync(string repositoryRoot, string commitHash, CancellationToken cancellationToken = default);
    Task<GitDiff> GetCommitDiffAsync(string repositoryRoot, string commitHash, string relativePath, string? parentHash = null, CancellationToken cancellationToken = default);
    Task<GitCommandResult> CloneAsync(string source, string destination, CancellationToken cancellationToken = default);
    Task<GitCommandResult> InitializeAsync(string folderPath, CancellationToken cancellationToken = default);
}
