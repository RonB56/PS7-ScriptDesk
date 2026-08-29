using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models;

public enum ApiStreamingInvocationEventKind
{
    InvocationStarted,
    Output,
    Warning,
    Verbose,
    Debug,
    Information,
    Error,
    InvocationCompleted,
    InvocationCanceled,
    InvocationFailed
}

public sealed class ApiStreamingInvocationRequest
{
    public string InvocationId { get; init; } = string.Empty;
    public string EndpointId { get; init; } = string.Empty;
    public string FunctionName { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public string? ConnectionId { get; init; }
    public string? SessionId { get; init; }
    public TimeSpan? Timeout { get; init; }
    public int EventCapacity { get; init; } = 64;
}

public sealed record ApiStreamingInvocationEvent(
    string InvocationId,
    string EndpointId,
    string? ConnectionId,
    string? SessionId,
    long Sequence,
    ApiStreamingInvocationEventKind Kind,
    DateTimeOffset Timestamp,
    object? Payload = null,
    string? Message = null,
    string? StatusCode = null,
    long? ElapsedMilliseconds = null)
{
    public bool IsTerminal => Kind is
        ApiStreamingInvocationEventKind.InvocationCompleted or
        ApiStreamingInvocationEventKind.InvocationCanceled or
        ApiStreamingInvocationEventKind.InvocationFailed;
}
