using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PromptRestorationSignalTests
{
    [Fact]
    public async Task StatusBeforePrompt_CompletesRestoration()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 7);

        Assert.True(signal.ObservePrompt(7, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.True(await signal.Completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task PromptBeforeStatus_IsRetainedUntilStatusAppears()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 7);

        Assert.True(signal.ObservePrompt(7, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true));
        Assert.False(signal.Completion.IsCompleted);
        // The production method observes status visibility by polling, then
        // awaits this same signal without receiving another prompt event.
        signal.ObserveStatusFileVisible();

        Assert.True(await signal.Completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task DuplicateAndStaleEvents_CompleteOnlyCurrentExecutionOnce()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 12);
        var completion = signal.Completion;
        using var simultaneousStart = new Barrier(2);

        Assert.False(signal.ObservePrompt(11, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.False(signal.ObserveCursor(11, visible: true));
        await Task.WhenAll(
            Task.Run(() =>
            {
                simultaneousStart.SignalAndWait();
                return signal.ObservePrompt(12, statusFileExists: false, invocationEchoRemoved: true, cursorVisible: true);
            }),
            Task.Run(() =>
            {
                simultaneousStart.SignalAndWait();
                return signal.ObserveStatusFileVisible();
            }));

        Assert.True(completion.IsCompleted);
        Assert.True(signal.ObserveCursor(12, visible: true));
        Assert.False(signal.ObservePrompt(12, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true));
        Assert.Same(completion, signal.Completion);

        Assert.True(await completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task CancellationDuringRestoration_IsBoundedAndDoesNotCompleteSignal()
    {
        var signal = new PromptRestorationSignal(expectedGeneration: 3);
        using var cancellation = new CancellationTokenSource();
        var wait = signal.Completion.WaitAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(signal.Completion.IsCompleted);
    }
}
