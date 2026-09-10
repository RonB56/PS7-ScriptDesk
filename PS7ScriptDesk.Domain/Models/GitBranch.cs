namespace PS7ScriptDesk.Domain.Models;

public sealed record GitBranch(
    string Name,
    string FullName,
    bool IsCurrent,
    bool IsRemote,
    string? UpstreamName,
    int? AheadCount,
    int? BehindCount,
    string? CommitHash,
    bool IsDetached = false)
{
    public string TrackingText => UpstreamName is null ? string.Empty
        : AheadCount is not null && BehindCount is not null ? $"↑{AheadCount} ↓{BehindCount}"
        : $"tracks {UpstreamName}";
}

public sealed record GitBranchState(
    IReadOnlyList<GitBranch> Branches,
    string? CurrentBranch,
    bool IsDetachedHead,
    string? HeadCommit,
    string? Error = null);
