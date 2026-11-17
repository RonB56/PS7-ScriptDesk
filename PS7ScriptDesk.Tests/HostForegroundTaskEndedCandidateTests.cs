using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class HostForegroundTaskEndedCandidateTests
{
    private static HostForegroundTaskEndedCandidateFacts ValidFacts() => new(
        Guid.NewGuid(), 41, 100, 101, Guid.NewGuid(), 8, 8, 321, 654,
        ProtocolSucceeded: true, ActiveRequestOwnsForeground: true, IdentityStillCurrent: true, DebuggerOwnsSession: false,
        Canceled: false, Faulted: false, Superseded: false, PsesVersionVerified: true);

    [Fact]
    public void ResponseThenQualifyingIdleIsComplete()
    {
        var result = HostForegroundTaskEndedCandidate.Classify(ValidFacts());
        Assert.True(result.Complete);
        Assert.Equal("complete", result.Classification);
    }

    [Fact]
    public void IdleBeforeResponseIsAmbiguous()
    {
        var result = HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { ResponseSequence = 101, IdleSequence = 100 });
        Assert.False(result.Complete);
        Assert.Equal("ambiguous", result.Classification);
    }

    [Fact]
    public void MissingIdleIsIncomplete()
    {
        Assert.Equal("incomplete", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { IdleSequence = null }).Classification);
    }

    [Fact]
    public void IdleSequenceWithoutAnObservedIdleIsIncomplete()
    {
        Assert.Equal("incomplete", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { IdleObservationCount = 0 }).Classification);
    }

    [Fact]
    public void MissingEvaluationResponseIsIncomplete()
    {
        Assert.Equal("incomplete", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { ResponseSequence = null }).Classification);
    }

    [Fact]
    public void SupersededSequentialRequestIsStale()
    {
        Assert.Equal("stale", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { Superseded = true }).Classification);
    }

    [Fact]
    public void DuplicateIdleIsAmbiguous()
    {
        Assert.Equal("ambiguous", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { IdleObservationCount = 2 }).Classification);
    }

    [Fact]
    public void WrongRequestIdIsRejected()
    {
        Assert.Equal("invalid", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { RequestCorrelationMatches = false }).Classification);
    }

    [Fact]
    public void EvaluateProtocolErrorIsRejected()
    {
        Assert.Equal("invalid", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { ProtocolSucceeded = false }).Classification);
    }

    [Fact]
    public void WrongProcessIdentityIsStale()
    {
        Assert.Equal("stale", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { ProcessIdentityMatches = false }).Classification);
    }

    [Fact]
    public void WrongRuntimeGenerationIsStale()
    {
        Assert.Equal("stale", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { RuntimeGenerationMatches = false }).Classification);
    }

    [Fact]
    public void ReplacedTerminalGenerationIsStale()
    {
        Assert.Equal("stale", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { TerminalGenerationMatches = false }).Classification);
    }

    [Theory]
    [InlineData(true, false, "request-canceled-or-interrupted")]
    [InlineData(false, true, "session-faulted-or-shutdown")]
    public void CancellationAndInterruptionAreInvalid(bool canceled, bool faulted, string reason)
    {
        Assert.Equal(reason, HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { Canceled = canceled, Faulted = faulted }).Reason);
    }

    [Fact]
    public void DebuggerOwnershipRejectsCandidate()
    {
        Assert.Equal("invalid", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { DebuggerOwnsSession = true }).Classification);
    }

    [Fact]
    public void SessionShutdownRejectsCandidate()
    {
        Assert.Equal("invalid", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { Faulted = true }).Classification);
    }

    [Fact]
    public void UnverifiedPsesVersionIsUnsupported()
    {
        Assert.Equal("unsupported", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { PsesVersionVerified = false }).Classification);
    }

    [Fact]
    public void MissingExclusiveOwnerIsAmbiguous()
    {
        Assert.Equal("ambiguous", HostForegroundTaskEndedCandidate.Classify(ValidFacts() with { ActiveRequestOwnsForeground = false }).Classification);
    }

    [Fact]
    public void CandidateHasNoExecutionReadinessAuthority()
    {
        var result = HostForegroundTaskEndedCandidate.Classify(ValidFacts());
        Assert.True(result.Complete);
        Assert.Equal("complete", result.Classification);
        // Classification is a value-only diagnostic and does not expose an execution-state mutation API.
        Assert.DoesNotContain(typeof(HostForegroundTaskEndedCandidateResult).GetProperties(), property =>
            property.Name.Contains("Ready", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Lease", StringComparison.OrdinalIgnoreCase));
    }
}
