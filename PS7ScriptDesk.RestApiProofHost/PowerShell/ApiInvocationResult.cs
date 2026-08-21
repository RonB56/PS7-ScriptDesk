using System.Management.Automation;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public enum ApiInvocationStatus
{
    Success,
    InvalidFunction,
    QueueFull,
    QueueWaitTimedOut,
    CallerCanceled,
    InvocationTimedOut,
    PowerShellFailure,
    HostUnavailable,
    InternalFailure
}

public sealed class ApiInvocationResult
{
    public ApiInvocationStatus Status { get; init; }
    public IReadOnlyList<PSObject> Output { get; init; } = Array.Empty<PSObject>();
    public IReadOnlyList<ApiInvocationStreamRecord> Streams { get; init; } = Array.Empty<ApiInvocationStreamRecord>();
    public string SafeMessage { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }
    public int PoolGeneration { get; init; }
    public bool RequiresPoolRebuild { get; init; }
    public bool IsSuccess => Status == ApiInvocationStatus.Success;

    public static ApiInvocationResult Success(
        IReadOnlyList<PSObject> output,
        IReadOnlyList<ApiInvocationStreamRecord> streams,
        TimeSpan elapsed,
        int poolGeneration)
        => new()
        {
            Status = ApiInvocationStatus.Success,
            Output = output,
            Streams = streams,
            Elapsed = elapsed,
            PoolGeneration = poolGeneration
        };

    public static ApiInvocationResult Failure(
        ApiInvocationStatus status,
        string safeMessage,
        IReadOnlyList<ApiInvocationStreamRecord>? streams = null,
        TimeSpan elapsed = default,
        int poolGeneration = 0,
        bool requiresPoolRebuild = false)
        => new()
        {
            Status = status,
            SafeMessage = safeMessage,
            Streams = streams ?? Array.Empty<ApiInvocationStreamRecord>(),
            Elapsed = elapsed,
            PoolGeneration = poolGeneration,
            RequiresPoolRebuild = requiresPoolRebuild
        };
}

public sealed record ApiInvocationStreamRecord(string StreamName, string Message);
