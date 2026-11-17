namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Inputs for a passive, request-correlated PSES lifecycle comparison.</summary>
public sealed record HostForegroundTaskEndedCandidateFacts(
    Guid EditorRequestId,
    int? ProtocolRequestId,
    long? ResponseSequence,
    long? IdleSequence,
    Guid? RuntimeSessionId,
    long? RuntimeGeneration,
    int? TerminalGeneration,
    int? ProcessId,
    long? ProcessStartUtcTicks,
    bool ProtocolSucceeded,
    bool ActiveRequestOwnsForeground,
    bool IdentityStillCurrent,
    bool DebuggerOwnsSession,
    bool Canceled,
    bool Faulted,
    bool Superseded,
    bool PsesVersionVerified,
    bool RequestCorrelationMatches = true,
    bool ProcessIdentityMatches = true,
    bool RuntimeGenerationMatches = true,
    bool TerminalGenerationMatches = true,
    int IdleObservationCount = 1);

public sealed record HostForegroundTaskEndedCandidateResult(bool Complete, string Classification, string Reason);

/// <summary>
/// Classifies one completed observation from facts already collected by the PSES adapter.
/// It does not own execution state and cannot change readiness or request ownership.
/// </summary>
public static class HostForegroundTaskEndedCandidate
{
    public static HostForegroundTaskEndedCandidateResult Classify(HostForegroundTaskEndedCandidateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.EditorRequestId == Guid.Empty || facts.ProtocolRequestId is null)
            return Incomplete("request-correlation-missing");
        if (!facts.RequestCorrelationMatches) return new(false, "invalid", "wrong-request-id");
        if (!facts.ProtocolSucceeded) return new(false, "invalid", "evaluate-protocol-error");
        if (facts.IdleObservationCount > 1) return new(false, "ambiguous", "duplicate-idle-observation");
        if (facts.IdleObservationCount < 1) return Incomplete("post-evaluate-idle-missing");
        if (facts.ResponseSequence is null || facts.IdleSequence is null)
            return Incomplete(facts.ResponseSequence is null ? "evaluate-response-missing" : "post-evaluate-idle-missing");
        if (facts.IdleSequence <= facts.ResponseSequence)
            return new(false, "ambiguous", "idle-not-after-response");
        if (facts.RuntimeSessionId is null || facts.RuntimeSessionId == Guid.Empty || facts.RuntimeGeneration is null || facts.TerminalGeneration is null)
            return Incomplete("runtime-identity-missing");
        if (facts.ProcessId is null or <= 0 || facts.ProcessStartUtcTicks is null or <= 0)
            return Incomplete("process-identity-missing");
        if (facts.Canceled) return new(false, "invalid", "request-canceled-or-interrupted");
        if (facts.Faulted) return new(false, "invalid", "session-faulted-or-shutdown");
        if (facts.Superseded || !facts.IdentityStillCurrent || !facts.ProcessIdentityMatches || !facts.RuntimeGenerationMatches || !facts.TerminalGenerationMatches)
            return new(false, "stale", !facts.ProcessIdentityMatches ? "process-identity-mismatch" : !facts.RuntimeGenerationMatches ? "runtime-generation-mismatch" : !facts.TerminalGenerationMatches ? "terminal-generation-replaced" : "session-or-generation-replaced");
        if (!facts.ActiveRequestOwnsForeground) return new(false, "ambiguous", "foreground-owner-not-exclusive");
        if (facts.DebuggerOwnsSession) return new(false, "invalid", "debugger-owns-session");
        if (!facts.PsesVersionVerified) return new(false, "unsupported", "pses-version-contract-unverified");
        return new(true, "complete", "response-followed-by-qualifying-idle");
    }

    private static HostForegroundTaskEndedCandidateResult Incomplete(string reason) => new(false, "incomplete", reason);
}
