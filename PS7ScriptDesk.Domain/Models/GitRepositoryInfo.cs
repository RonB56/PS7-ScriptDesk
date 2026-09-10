namespace PS7ScriptDesk.Domain.Models;

public sealed record GitRepositoryInfo(
    bool IsRepository,
    string? RepositoryRoot,
    string? WorkingTreeRoot,
    string? CurrentBranch,
    bool IsDetachedHead,
    string? HeadCommit,
    bool IsBareRepository,
    bool IsWorktree,
    bool IsSubmodule,
    string? Error,
    string? FailureKind)
{
    public static GitRepositoryInfo NotRepository(string? error = null, string? failureKind = null)
        => new(false, null, null, null, false, null, false, false, false, error, failureKind);
}
