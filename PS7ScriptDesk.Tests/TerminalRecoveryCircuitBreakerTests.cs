using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalRecoveryCircuitBreakerTests
{
    [Fact]
    public void AutomaticRestarts_AreBoundedWithinFailureWindow()
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var breaker = new TerminalRecoveryCircuitBreaker(
            maxAttempts: 2,
            window: TimeSpan.FromSeconds(30),
            baseBackoff: TimeSpan.Zero,
            maxBackoff: TimeSpan.Zero,
            clock: () => now);

        Assert.True(breaker.TryBeginAutomaticRestart().IsAllowed);
        Assert.True(breaker.TryBeginAutomaticRestart().IsAllowed);

        var blocked = breaker.TryBeginAutomaticRestart();
        var blockedAgain = breaker.TryBeginAutomaticRestart();

        Assert.False(blocked.IsAllowed);
        Assert.True(blocked.ShouldNotifyUnavailable);
        Assert.False(blockedAgain.IsAllowed);
        Assert.False(blockedAgain.ShouldNotifyUnavailable);
    }

    [Fact]
    public void ManualRetry_ClosesOpenCircuitAndAllowsRestartAgain()
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var breaker = new TerminalRecoveryCircuitBreaker(
            maxAttempts: 1,
            window: TimeSpan.FromMinutes(1),
            baseBackoff: TimeSpan.Zero,
            maxBackoff: TimeSpan.Zero,
            clock: () => now);

        Assert.True(breaker.TryBeginAutomaticRestart().IsAllowed);
        Assert.False(breaker.TryBeginAutomaticRestart().IsAllowed);

        breaker.ResetForManualRetry();

        Assert.True(breaker.TryBeginAutomaticRestart().IsAllowed);
    }
}
