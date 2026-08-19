using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalFocusRestorePolicyTests
{
    private static readonly TerminalFocusRestoreReadiness Ready = new(
        RendererReady: true,
        ConsoleVisible: true,
        ApplicationActive: true,
        ModalDialogOpen: false);

    [Fact]
    public void ResetWhileTerminalFocused_CapturesAndBindsFocusIntent()
    {
        var policy = new TerminalFocusRestorePolicy();

        var intent = policy.Capture(terminalHadFocus: true, previousGeneration: 4);

        Assert.True(intent.IsRequested);
        Assert.True(policy.BindReplacementGeneration(5));
    }

    [Fact]
    public void ReplacementReady_RestoresFocusExactlyOnce()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryConsume(5, Ready));
        Assert.Equal(TerminalFocusRestoreDecision.NoPendingIntent, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void ResetWhileEditorFocused_DoesNotCreateFocusIntent()
    {
        var policy = new TerminalFocusRestorePolicy();

        Assert.False(policy.Capture(terminalHadFocus: false, previousGeneration: 4).IsRequested);
        Assert.Equal(TerminalFocusRestoreDecision.NoPendingIntent, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void UserFocusMoveBeforeReplacementReady_CancelsRestoration()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.True(policy.Cancel());
        Assert.Equal(TerminalFocusRestoreDecision.NoPendingIntent, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void StaleOldGeneration_CannotConsumeReplacementFocusIntent()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.Equal(TerminalFocusRestoreDecision.StaleGeneration, policy.TryConsume(4, Ready));
        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void RepeatedReset_ReplacesEarlierIntentWithFinalGeneration()
    {
        var policy = CreateBoundPolicy(4, 5);

        policy.Capture(terminalHadFocus: true, previousGeneration: 5);
        Assert.True(policy.BindReplacementGeneration(6));

        Assert.Equal(TerminalFocusRestoreDecision.StaleGeneration, policy.TryConsume(5, Ready));
        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryConsume(6, Ready));
    }

    [Fact]
    public void TerminalFailure_CancelsPendingFocusIntent()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.True(policy.Cancel());
        Assert.False(policy.HasPendingIntent);
    }

    [Fact]
    public void ApplicationShutdown_CancelsPendingFocusIntent()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.True(policy.Cancel());
        Assert.Equal(TerminalFocusRestoreDecision.NoPendingIntent, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void HiddenConsoleTab_IsNeverFocused()
    {
        var policy = CreateBoundPolicy(4, 5);
        var hidden = Ready with { ConsoleVisible = false };

        Assert.Equal(TerminalFocusRestoreDecision.ConsoleHidden, policy.TryConsume(5, hidden));
        Assert.Equal(TerminalFocusRestoreDecision.NoPendingIntent, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void ModalDialog_PreventsFocusStealing()
    {
        var policy = CreateBoundPolicy(4, 5);
        var modal = Ready with { ModalDialogOpen = true };

        Assert.Equal(TerminalFocusRestoreDecision.ModalDialogOpen, policy.TryConsume(5, modal));
        Assert.Equal(TerminalFocusRestoreDecision.NoPendingIntent, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void RendererNotReady_DefersRatherThanDroppingIntent()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.Equal(
            TerminalFocusRestoreDecision.RendererNotReady,
            policy.TryConsume(5, Ready with { RendererReady = false }));
        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryConsume(5, Ready));
    }

    [Fact]
    public void SessionReadyAlone_DoesNotBeginFocusUntilRendererIsReady()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.Equal(
            TerminalFocusRestoreDecision.RendererNotReady,
            policy.TryBeginFocusAttempt(5, Ready with { RendererReady = false }));
        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryBeginFocusAttempt(5, Ready));
    }

    [Fact]
    public void FailedFocusVerification_AllowsOneRetryOnlyForTheSameGeneration()
    {
        var policy = CreateBoundPolicy(4, 5);

        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryBeginFocusAttempt(5, Ready));
        Assert.True(policy.CompleteFocusAttempt(5, succeeded: false));
        Assert.Equal(TerminalFocusRestoreDecision.StaleGeneration, policy.TryBeginFocusAttempt(4, Ready));
        Assert.Equal(TerminalFocusRestoreDecision.Restore, policy.TryBeginFocusAttempt(5, Ready));
        Assert.False(policy.CompleteFocusAttempt(5, succeeded: false));
        Assert.False(policy.HasPendingIntent);
    }

    private static TerminalFocusRestorePolicy CreateBoundPolicy(int previousGeneration, int replacementGeneration)
    {
        var policy = new TerminalFocusRestorePolicy();
        policy.Capture(terminalHadFocus: true, previousGeneration: previousGeneration);
        Assert.True(policy.BindReplacementGeneration(replacementGeneration));
        return policy;
    }
}
