using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class UnhandledExceptionStormGateTests
{
    [Fact]
    public void RepeatedEquivalentException_IsSuppressedWithinActiveFaultWindow()
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var gate = new UnhandledExceptionStormGate(TimeSpan.FromSeconds(30), () => now);
        var first = CreateException("same failure");
        var second = CreateException("same failure");

        var firstDecision = gate.TryBeginPresentation("Dispatcher unhandled exception", first);
        var summary = gate.EndPresentation(firstDecision.Signature);
        var secondDecision = gate.TryBeginPresentation("Dispatcher unhandled exception", second);

        Assert.True(firstDecision.ShouldPresent);
        Assert.Equal(1, summary.OccurrenceCount);
        Assert.False(secondDecision.ShouldPresent);
        Assert.Equal(2, secondDecision.OccurrenceCount);
        Assert.Contains("already reported", secondDecision.SuppressionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReentrantException_IsSuppressedWhileNotificationIsBeingPresented()
    {
        var gate = new UnhandledExceptionStormGate(TimeSpan.FromSeconds(30), () => DateTimeOffset.UtcNow);
        var first = CreateException("layout failure");
        var reentrant = CreateException("layout failure");

        var firstDecision = gate.TryBeginPresentation("Dispatcher unhandled exception", first);
        var reentrantDecision = gate.TryBeginPresentation("Dispatcher unhandled exception", reentrant);
        var summary = gate.EndPresentation(firstDecision.Signature);

        Assert.True(firstDecision.ShouldPresent);
        Assert.False(reentrantDecision.ShouldPresent);
        Assert.Contains("already being presented", reentrantDecision.SuppressionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, summary.OccurrenceCount);
    }

    private static Exception CreateException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
