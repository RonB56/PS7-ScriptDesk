using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalResizeAwaitableAdmissionTests
{
    [Fact]
    public async Task AwaitingResizeReleaseThenAdmittingPreservesOriginalOrder()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(64);
        Assert.True(barrier.Begin(4, 9, 1, 100, 30).Accepted);
        var firstWait = barrier.WaitForReleaseAsync(4, 9);
        var secondWait = barrier.WaitForReleaseAsync(4, 9);
        Assert.False(firstWait.IsCompleted);
        Assert.False(secondWait.IsCompleted);

        Assert.True(barrier.Acknowledge(4, 9, 1, 100, 30).Accepted);
        Assert.True(await firstWait);
        Assert.True(await secondWait);
        Assert.Equal(3, flow.Enqueue(9, "one").AcceptedCharacters);
        Assert.Equal(3, flow.Enqueue(9, "two").AcceptedCharacters);

        var batch = Assert.IsType<TerminalOutputBatch>(flow.TryBeginDelivery());
        Assert.Equal("onetwo", batch.Data);
    }

    [Fact]
    public async Task ResizeReleaseUsesRegularCapacityWhenQueueIsNearlyFull()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(8);
        Assert.Equal(6, flow.Enqueue(9, "123456").AcceptedCharacters);
        Assert.True(barrier.Begin(4, 9, 2, 100, 30).Accepted);
        var releaseWait = barrier.WaitForReleaseAsync(4, 9);
        var capacityWait = flow.WaitForCapacityAsync(9, 3);
        Assert.False(capacityWait.IsCompleted);

        Assert.True(barrier.Acknowledge(4, 9, 2, 100, 30).Accepted);
        Assert.True(await releaseWait);
        Assert.False(capacityWait.IsCompleted);
        var existing = Assert.IsType<TerminalOutputBatch>(flow.TryBeginDelivery());
        Assert.Equal("123456", existing.Data);
        Assert.False(flow.Acknowledge(9, existing.Sequence));
        Assert.True(await capacityWait);
        Assert.Equal(3, flow.Enqueue(9, "new").AcceptedCharacters);
        Assert.Equal(0, flow.Enqueue(9, "overflow").AcceptedCharacters);
    }

    [Fact]
    public async Task CancellationAndExpirationWakeResizeWaiters()
    {
        var cancelledBarrier = CreateBarrier();
        Assert.True(cancelledBarrier.Begin(4, 9, 3, 100, 30).Accepted);
        var cancelledWait = cancelledBarrier.WaitForReleaseAsync(4, 9);
        cancelledBarrier.Cancel();
        Assert.True(await cancelledWait);

        var expiredBarrier = CreateBarrier();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        Assert.True(expiredBarrier.Begin(4, 9, 4, 100, 30, startedAt).Accepted);
        var expiredWait = expiredBarrier.WaitForReleaseAsync(4, 9);
        Assert.True(expiredBarrier.Expire().Expired);
        expiredBarrier.Cancel();
        Assert.True(await expiredWait);
    }

    [Fact]
    public async Task RendererRetirementCancelsResizeWaitAndRejectsFlowAdmission()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(8);
        Assert.True(barrier.Begin(4, 9, 5, 100, 30).Accepted);
        var releaseWait = barrier.WaitForReleaseAsync(4, 9);

        barrier.Cancel();
        flow.MarkRendererUnavailable();

        Assert.True(await releaseWait);
        Assert.False(await flow.WaitForCapacityAsync(9, 1));
        Assert.Equal(0, flow.Enqueue(9, "x").AcceptedCharacters);
    }

    [Fact]
    public async Task RendererReplacementDuringResizeWaitReobservesCurrentTransactionAndAdmitsOnce()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(64);
        Assert.True(barrier.Begin(4, 9, 10, 100, 30).Accepted);
        var retiredRendererWait = barrier.WaitForReleaseAsync(4, 9);

        // Renderer retirement releases the old transaction. Start the replacement
        // transaction before the waiter resumes so it must observe the new identity.
        barrier.Cancel();
        Assert.True(barrier.Begin(5, 9, 11, 120, 40).Accepted);
        Assert.False(await retiredRendererWait);

        var replacementWait = barrier.WaitForReleaseAsync(5, 9);
        Assert.False(replacementWait.IsCompleted);
        Assert.True(barrier.Acknowledge(5, 9, 11, 120, 40).Accepted);
        Assert.True(await replacementWait);

        Assert.Equal("pending-chunk".Length, flow.Enqueue(9, "pending-chunk").AcceptedCharacters);
        var delivered = Assert.IsType<TerminalOutputBatch>(flow.TryBeginDelivery());
        Assert.Equal("pending-chunk", delivered.Data);
        Assert.False(flow.Acknowledge(9, delivered.Sequence));
        Assert.Null(flow.TryBeginDelivery());
    }

    [Fact]
    public async Task SessionGenerationInvalidationDuringReplacementWaitRejectsStaleAdmission()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(64);
        Assert.True(barrier.Begin(4, 9, 12, 100, 30).Accepted);
        var oldGenerationWait = barrier.WaitForReleaseAsync(4, 9);

        barrier.Cancel();
        flow.InvalidateGeneration(9);
        flow.ActivateGeneration(10);
        flow.RestoreRenderer();
        Assert.False(flow.SetRendererReady());
        Assert.True(barrier.Begin(5, 10, 13, 120, 40).Accepted);

        Assert.False(await oldGenerationWait);
        Assert.Equal(0, flow.Enqueue(9, "stale").AcceptedCharacters);
        Assert.Equal(7, flow.Enqueue(10, "current").AcceptedCharacters);
    }

    [Fact]
    public async Task SessionInvalidationCancelsResizeWaitAndRejectsStaleAdmission()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(8);
        Assert.True(barrier.Begin(4, 9, 6, 100, 30).Accepted);
        var releaseWait = barrier.WaitForReleaseAsync(4, 9);

        barrier.Cancel();
        flow.InvalidateGeneration(9);

        Assert.True(await releaseWait);
        Assert.False(await flow.WaitForCapacityAsync(9, 1));
        Assert.Equal(1, flow.Enqueue(9, "x").RejectedStaleCharacters);
    }

    [Fact]
    public void RejectedOutputIsNotAddedToPersistentHistory()
    {
        var history = new TerminalPersistentOutputHistory(16);
        var flow = new TerminalOutputFlowController(maximumPendingCharacters: 4, maximumBatchCharacters: 4);
        history.ActivateGeneration(9);
        flow.ActivateGeneration(9);
        Assert.Equal(4, flow.Enqueue(9, "full").AcceptedCharacters);

        var rejected = flow.Enqueue(9, "x");
        if (rejected.AcceptedCharacters == 1)
        {
            history.Append(9, "x");
        }

        Assert.Equal(1, rejected.DroppedCharacters);
        Assert.Empty(history.Snapshot(9));
    }

    [Fact]
    public void RendererReplacement_RequeuesConfirmedHistoryBeforeNewAdmissionExactlyOnce()
    {
        var history = new TerminalPersistentOutputHistory(32);
        var flow = CreateReadyFlow(64);
        history.ActivateGeneration(9);

        var retiredQueue = flow.Enqueue(9, "accepted-before-retirement");
        Assert.Equal("accepted-before-retirement".Length, retiredQueue.AcceptedCharacters);
        Assert.True(history.Append(9, "accepted-before-retirement"));
        flow.MarkRendererUnavailable();
        Assert.Equal(0, flow.Enqueue(9, "pending-during-outage").AcceptedCharacters);

        flow.RestoreRenderer();
        foreach (var chunk in history.Snapshot(9))
            Assert.Equal(chunk.Length, flow.Enqueue(9, chunk).AcceptedCharacters);
        Assert.True(flow.SetRendererReady());
        Assert.Equal("new-output".Length, flow.Enqueue(9, "new-output").AcceptedCharacters);

        var replayedAndNewOutput = new System.Text.StringBuilder();
        while (flow.TryBeginDelivery() is { } batch)
        {
            replayedAndNewOutput.Append(batch.Data);
            flow.Acknowledge(9, batch.Sequence);
        }
        Assert.Equal("accepted-before-retirementnew-output", replayedAndNewOutput.ToString());
        Assert.Null(flow.TryBeginDelivery());
    }

    [Fact]
    public void RepeatedRendererReplacement_RebuildsOnlyBoundedConfirmedHistory()
    {
        var history = new TerminalPersistentOutputHistory(16);
        var flow = CreateReadyFlow(20);
        history.ActivateGeneration(9);
        foreach (var chunk in new[] { "first", "second", "third" })
        {
            Assert.Equal(chunk.Length, flow.Enqueue(9, chunk).AcceptedCharacters);
            Assert.True(history.Append(9, chunk));
        }

        for (var replacement = 0; replacement < 3; replacement++)
        {
            flow.MarkRendererUnavailable();
            flow.RestoreRenderer();
            foreach (var chunk in history.Snapshot(9))
                Assert.Equal(chunk.Length, flow.Enqueue(9, chunk).AcceptedCharacters);
            Assert.True(flow.SetRendererReady());
            Assert.Equal(16, flow.PendingCharacters);
            var replay = Assert.IsType<TerminalOutputBatch>(flow.TryBeginDelivery());
            Assert.Equal("firstsecondthird", replay.Data);
            Assert.False(flow.Acknowledge(9, replay.Sequence));
        }
    }

    [Fact]
    public async Task RepeatedResizeTransactionsReleaseOnceWithoutDuplicateOutput()
    {
        var barrier = CreateBarrier();
        var flow = CreateReadyFlow(64);
        for (var resizeGeneration = 1; resizeGeneration <= 5; resizeGeneration++)
        {
            Assert.True(barrier.Begin(4, 9, resizeGeneration, 100 + resizeGeneration, 30).Accepted);
            var releaseWait = barrier.WaitForReleaseAsync(4, 9);
            Assert.True(barrier.Acknowledge(4, 9, resizeGeneration, 100 + resizeGeneration, 30).Accepted);
            Assert.False(barrier.Acknowledge(4, 9, resizeGeneration, 100 + resizeGeneration, 30).Accepted);
            Assert.True(await releaseWait);
            Assert.Equal(1, flow.Enqueue(9, resizeGeneration.ToString()).AcceptedCharacters);
        }

        var batch = Assert.IsType<TerminalOutputBatch>(flow.TryBeginDelivery());
        Assert.Equal("12345", batch.Data);
    }

    private static TerminalResizeOutputBarrier CreateBarrier() =>
        new(maximumBufferedCharacters: 64, maximumBufferedChunks: 8, maximumDuration: TimeSpan.FromSeconds(2));

    private static TerminalOutputFlowController CreateReadyFlow(int capacity)
    {
        var flow = new TerminalOutputFlowController(capacity, Math.Min(capacity, 16));
        flow.ActivateGeneration(9);
        Assert.False(flow.SetRendererReady());
        return flow;
    }
}
