namespace PS7ScriptDesk.Domain.Models;

public sealed record GitRepositoryState(
    GitEnvironmentInfo Environment,
    GitRepositoryInfo Repository,
    string? WorkspacePath,
    DateTimeOffset RefreshedAtUtc)
{
    public bool IsRepository => Repository.IsRepository;

    public IReadOnlyList<GitFileStatus> Changes { get; init; } = Array.Empty<GitFileStatus>();

    public string? StatusError { get; init; }

    public GitRepositoryOperationState OperationState { get; init; } = GitRepositoryOperationState.None;

    public bool HasUnresolvedConflicts => Changes.Any(change => change.IsConflicted);
}

public enum GitRepositoryOperationState
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert
}
