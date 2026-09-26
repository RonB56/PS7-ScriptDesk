using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PS7ScriptDesk.Shell.Debug;

public enum DebugTerminationReason
{
    NormalCompletion,
    UserStop,
    TerminatingException,
    ProtocolFailure,
    ChildProcessExit,
    StartupFailure,
    Cancellation,
    TransportFailure,
    UnknownFailure
}

public sealed record DebugTerminationInfo
{
    public const int MaxMessageLength = 2048;
    public const int MaxDetailsLength = 4096;

    public DebugTerminationInfo(
        Guid sessionId,
        DateTimeOffset timestampUtc,
        DebugTerminationReason reason,
        string message,
        int? exitCode = null,
        int? processId = null,
        bool wasUserRequested = false,
        bool wasExpected = false,
        string? details = null,
        DebugExceptionInfo? exception = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A debugger session identity is required.", nameof(sessionId));
        SessionId = sessionId;
        TimestampUtc = timestampUtc.ToUniversalTime();
        Reason = reason;
        Message = Bound(message, MaxMessageLength);
        ExitCode = exitCode;
        ProcessId = processId is > 0 ? processId : null;
        WasUserRequested = wasUserRequested;
        WasExpected = wasExpected;
        Details = BoundOptional(details, MaxDetailsLength);
        Exception = exception;
    }

    public Guid SessionId { get; }
    public DateTimeOffset TimestampUtc { get; }
    public DebugTerminationReason Reason { get; }
    public string Message { get; }
    public int? ExitCode { get; }
    public int? ProcessId { get; }
    public bool WasUserRequested { get; }
    public bool WasExpected { get; }
    public string? Details { get; }
    public DebugExceptionInfo? Exception { get; }

    private static string? BoundOptional(string? value, int maxLength)
        => string.IsNullOrEmpty(value) ? null : Bound(value, maxLength);

    private static string Bound(string? value, int maxLength)
    {
        var normalized = value ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

public sealed record DebugExceptionInfo
{
    public const int MaxFieldLength = 1024;
    public const int MaxPositionLength = 2048;

    public DebugExceptionInfo(
        string exceptionType,
        string message,
        string? fullyQualifiedErrorId = null,
        string? category = null,
        bool isTerminating = true,
        bool isHandled = false,
        string? scriptPath = null,
        int? lineNumber = null,
        int? columnNumber = null,
        string? invocationName = null,
        string? positionText = null,
        string? stackSummary = null,
        string? innerDetails = null)
    {
        ExceptionType = BoundRequired(exceptionType);
        Message = BoundRequired(message);
        FullyQualifiedErrorId = BoundOptional(fullyQualifiedErrorId);
        Category = BoundOptional(category);
        IsTerminating = isTerminating;
        IsHandled = isHandled;
        ScriptPath = BoundOptional(scriptPath, MaxFieldLength);
        LineNumber = lineNumber is > 0 ? lineNumber : null;
        ColumnNumber = columnNumber is > 0 ? columnNumber : null;
        InvocationName = BoundOptional(invocationName);
        PositionText = BoundOptional(positionText, MaxPositionLength);
        StackSummary = BoundOptional(stackSummary, MaxPositionLength);
        InnerDetails = BoundOptional(innerDetails, MaxPositionLength);
    }

    public string ExceptionType { get; }
    public string Message { get; }
    public string? FullyQualifiedErrorId { get; }
    public string? Category { get; }
    public bool IsTerminating { get; }
    public bool IsHandled { get; }
    public string? ScriptPath { get; }
    public int? LineNumber { get; }
    public int? ColumnNumber { get; }
    public string? InvocationName { get; }
    public string? PositionText { get; }
    public string? StackSummary { get; }
    public string? InnerDetails { get; }

    private static string BoundRequired(string value)
        => Bound(value, MaxFieldLength);

    private static string? BoundOptional(string? value, int maxLength = MaxFieldLength)
        => string.IsNullOrEmpty(value) ? null : Bound(value, maxLength);

    private static string Bound(string? value, int maxLength)
    {
        var normalized = value ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

internal static class DebugExceptionEnvelope
{
    internal const string Marker = "__PSS_DEBUG_EXCEPTION__";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string Encode(DebugExceptionInfo exception)
    {
        var json = JsonSerializer.Serialize(exception);
        return Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static bool TryDecode(string line, out DebugExceptionInfo? exception)
    {
        exception = null;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(line[Marker.Length..].Trim()));
            exception = JsonSerializer.Deserialize<DebugExceptionInfo>(json, JsonOptions);
            return exception is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal static class DebugTerminationPolicy
{
    internal static DebugTerminationReason Select(
        bool userStopRequested,
        bool exceptionObserved,
        bool startupFailed,
        bool transportFailed,
        bool protocolFailed,
        bool normalCompletionObserved,
        bool processExitedUnexpectedly)
    {
        if (userStopRequested) return DebugTerminationReason.UserStop;
        if (exceptionObserved) return DebugTerminationReason.TerminatingException;
        if (startupFailed) return DebugTerminationReason.StartupFailure;
        if (transportFailed) return DebugTerminationReason.TransportFailure;
        if (protocolFailed) return DebugTerminationReason.ProtocolFailure;
        if (normalCompletionObserved) return DebugTerminationReason.NormalCompletion;
        if (processExitedUnexpectedly) return DebugTerminationReason.ChildProcessExit;
        return DebugTerminationReason.UnknownFailure;
    }
}
