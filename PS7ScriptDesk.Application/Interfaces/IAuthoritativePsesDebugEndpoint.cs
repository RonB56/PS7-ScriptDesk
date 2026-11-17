namespace PS7ScriptDesk.Application.Interfaces;

public enum DebuggerLifecycleObservation
{
    Unknown,
    None,
    Starting,
    Running,
    Suspended,
    Resuming,
    Stopping,
    Terminated,
    Stale
}

/// <summary>A read-only, generation-bound observation of who owns the PSES debuggee.</summary>
public sealed record DebuggerOwnershipSnapshot(
    Guid RuntimeSessionId,
    long? RuntimeGeneration,
    int TerminalGeneration,
    int? ProcessId,
    Guid? DebuggerSessionId,
    DebuggerLifecycleObservation State,
    bool IsValid)
{
    public bool MayOwnSession => !IsValid || State is not (DebuggerLifecycleObservation.None or DebuggerLifecycleObservation.Terminated);
}

/// <summary>Applies generation-bound diagnostic observations without replacing a live debugger owner.</summary>
public static class DebuggerOwnershipTransitionPolicy
{
    public static bool TryBegin(
        DebuggerOwnershipSnapshot current, Guid runtimeSessionId, long runtimeGeneration,
        int terminalGeneration, int? processId, Guid debuggerSessionId, out DebuggerOwnershipSnapshot next)
    {
        next = current;
        if (debuggerSessionId == Guid.Empty || runtimeSessionId == Guid.Empty || runtimeGeneration <= 0 || processId is null ||
            !MatchesCurrentIdentity(current, runtimeSessionId, runtimeGeneration, terminalGeneration, processId))
            return false;

        if (current.State == DebuggerLifecycleObservation.Starting && current.DebuggerSessionId == debuggerSessionId)
            return true;
        if (!current.IsValid || current.State is not (DebuggerLifecycleObservation.None or DebuggerLifecycleObservation.Terminated) ||
            current.State == DebuggerLifecycleObservation.Terminated && current.DebuggerSessionId == debuggerSessionId)
            return false;

        next = new(runtimeSessionId, runtimeGeneration, terminalGeneration, processId,
            debuggerSessionId, DebuggerLifecycleObservation.Starting, true);
        return true;
    }

    public static bool TryApplyObservation(
        DebuggerOwnershipSnapshot current, Guid runtimeSessionId, long runtimeGeneration,
        int terminalGeneration, int? processId, Guid debuggerSessionId,
        DebuggerLifecycleObservation state, out DebuggerOwnershipSnapshot next)
    {
        next = current;
        if (state == DebuggerLifecycleObservation.Starting)
            return TryBegin(current, runtimeSessionId, runtimeGeneration, terminalGeneration, processId, debuggerSessionId, out next);
        if (debuggerSessionId == Guid.Empty || current.DebuggerSessionId != debuggerSessionId ||
            !MatchesCurrentIdentity(current, runtimeSessionId, runtimeGeneration, terminalGeneration, processId))
            return false;

        next = current with
        {
            ProcessId = processId,
            State = state,
            IsValid = state is not (DebuggerLifecycleObservation.Unknown or DebuggerLifecycleObservation.Stale)
        };
        return true;
    }

    private static bool MatchesCurrentIdentity(DebuggerOwnershipSnapshot current, Guid runtimeSessionId,
        long runtimeGeneration, int terminalGeneration, int? processId) =>
        current.RuntimeSessionId == runtimeSessionId && current.RuntimeGeneration == runtimeGeneration &&
        current.TerminalGeneration == terminalGeneration && current.ProcessId == processId;
}

/// <summary>Describes the debug endpoint published by the authoritative PSES session.</summary>
public interface IAuthoritativePsesDebugEndpoint
{
    bool IsSessionRunning { get; }
    int SessionGeneration { get; }
    int? ProcessId { get; }
    DateTimeOffset? ProcessStartTimeUtc { get; }
    string? DebugServicePipeName { get; }

    DebuggerOwnershipSnapshot GetDebuggerOwnershipSnapshot();

    /// <summary>Publishes a diagnostic-only DAP lifecycle observation. It cannot mutate execution or readiness state.</summary>
    bool ReportDebuggerLifecycle(Guid debuggerSessionId, DebuggerLifecycleObservation state);
}
