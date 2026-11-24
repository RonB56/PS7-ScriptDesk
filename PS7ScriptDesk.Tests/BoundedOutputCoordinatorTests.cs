using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class BoundedOutputCoordinatorTests
{
    [Fact]
    public async Task AdmissionIsFifoAndCreditsRequireRendererCompletion()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var fill = await coordinator.AdmitAsync("fill", new string('x', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        var first = coordinator.AdmitAsync("p1", "first").AsTask();
        var second = coordinator.AdmitAsync("p2", "second").AsTask();
        Assert.Equal(2, coordinator.Snapshot.WaitingProducers);
        Assert.Equal(11, coordinator.Snapshot.PendingUtf16CodeUnits);

        Assert.Same(fill, await coordinator.ReadAsync());
        Assert.Equal(AcknowledgmentResult.NotRendererSubmitted, coordinator.Acknowledge(fill.Acknowledgment));
        Assert.Equal(0, coordinator.Snapshot.QueuedFrames);
        Assert.Equal(BoundedOutputCoordinator.MaximumUtf16CodeUnits, coordinator.Snapshot.ReservedUtf16CodeUnits);
        Assert.True(coordinator.MarkRendererSubmitted(fill.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(fill.Acknowledgment));

        var firstFrame = await first.WaitAsync(TimeSpan.FromSeconds(2));
        var secondFrame = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(firstFrame.Sequence < secondFrame.Sequence);
        Assert.Equal("first", (await DispatchAsync(coordinator)).Text);
        Assert.Equal("second", (await DispatchAsync(coordinator)).Text);
    }

    [Fact]
    public async Task PrematureAcknowledgmentsCannotReleaseQueueOrFrameCapacity()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var frames = new List<OutputFrame>(BoundedOutputCoordinator.MaximumFrames);
        for (var i = 0; i < BoundedOutputCoordinator.MaximumFrames; i++)
            frames.Add(await coordinator.AdmitAsync("frame-limit", string.Empty));

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(AcknowledgmentResult.NotRendererSubmitted, coordinator.Acknowledge(frames[0].Acknowledgment));
        Assert.Equal(BoundedOutputCoordinator.MaximumFrames, coordinator.Snapshot.QueuedFrames);
        Assert.Equal(BoundedOutputCoordinator.MaximumFrames, coordinator.Snapshot.OutstandingFrames);
        var blocked = coordinator.AdmitAsync("blocked", string.Empty).AsTask();
        Assert.False(blocked.IsCompleted);

        var first = await coordinator.ReadAsync();
        Assert.Equal(AcknowledgmentResult.NotRendererSubmitted, coordinator.Acknowledge(first!.Acknowledgment));
        Assert.True(coordinator.MarkRendererSubmitted(first.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(first.Acknowledgment));
        _ = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.Snapshot.QueuedFrames <= BoundedOutputCoordinator.MaximumFrames);
        Assert.True(coordinator.Snapshot.OutstandingFrames <= BoundedOutputCoordinator.MaximumFrames);
    }

    [Fact]
    public async Task TextCapacityAndAcknowledgedCapacityAreBounded()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var full = await coordinator.AdmitAsync("text-limit", new string('a', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        var blocked = coordinator.AdmitAsync("blocked", "z").AsTask();
        Assert.False(blocked.IsCompleted);
        await SubmitAsync(coordinator, full);
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(full.Acknowledgment));
        Assert.Equal("z", (await blocked.WaitAsync(TimeSpan.FromSeconds(2))).Text);
        Assert.True(coordinator.Snapshot.ReservedUtf16CodeUnits <= BoundedOutputCoordinator.MaximumUtf16CodeUnits);
        Assert.True(coordinator.Snapshot.OutstandingFrames <= BoundedOutputCoordinator.MaximumFrames);
    }

    [Fact]
    public async Task DequeuedFrameCannotBeAcknowledgedUntilMarkedSubmitted()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var frame = await coordinator.AdmitAsync("producer", "12345");
        Assert.Equal(5, coordinator.Snapshot.ReservedUtf16CodeUnits);
        Assert.Same(frame, await coordinator.ReadAsync());
        Assert.Equal(AcknowledgmentResult.NotRendererSubmitted, coordinator.Acknowledge(frame.Acknowledgment));
        Assert.Equal(5, coordinator.Snapshot.ReservedUtf16CodeUnits);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.ReadAsync());
        Assert.True(coordinator.MarkRendererSubmitted(frame.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(frame.Acknowledgment));
        Assert.Equal(0, coordinator.Snapshot.ReservedUtf16CodeUnits);
        Assert.Equal(0, coordinator.Snapshot.OutstandingFrames);
    }

    [Fact]
    public async Task ReadDispatchIsSingleConsumerAndOrdered()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var first = await coordinator.AdmitAsync("producer", "one");
        var second = await coordinator.AdmitAsync("producer", "two");
        var dispatchedFirst = await coordinator.ReadAsync();
        Assert.Same(first, dispatchedFirst);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.ReadAsync());
        Assert.True(coordinator.MarkRendererSubmitted(first.Acknowledgment));
        var dispatchedSecond = await coordinator.ReadAsync();
        Assert.Same(second, dispatchedSecond);
        Assert.True(coordinator.MarkRendererSubmitted(second.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(first.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(second.Acknowledgment));
    }

    [Fact]
    public async Task PendingTextBudgetHasExplicitObservableRejection()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        _ = await coordinator.AdmitAsync("fill", new string('x', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        var exact = coordinator.AdmitAsync("pending", new string('p', BoundedOutputCoordinator.MaximumPendingUtf16CodeUnits)).AsTask();
        Assert.Equal(BoundedOutputCoordinator.MaximumPendingUtf16CodeUnits, coordinator.Snapshot.PendingUtf16CodeUnits);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.AdmitAsync("overflow", "rejected"));
        Assert.Equal(1, coordinator.Snapshot.WaitingProducers);
        Assert.Equal(BoundedOutputCoordinator.MaximumPendingUtf16CodeUnits, coordinator.Snapshot.PendingUtf16CodeUnits);
        Assert.Contains("AdmissionRejected", DrainDiagnostics(coordinator));
        await coordinator.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => exact);
        Assert.Equal(0, coordinator.Snapshot.PendingUtf16CodeUnits);
    }

    [Fact]
    public async Task CancellationRemovesBlockedProducerAndReleasesPendingBudget()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        _ = await coordinator.AdmitAsync("fill", new string('a', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        using var cts = new CancellationTokenSource();
        var blocked = coordinator.AdmitAsync("producer", "never", cts.Token).AsTask();
        Assert.Equal(5, coordinator.Snapshot.PendingUtf16CodeUnits);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, coordinator.Snapshot.WaitingProducers);
        Assert.Equal(0, coordinator.Snapshot.PendingUtf16CodeUnits);
    }

    [Fact]
    public async Task CancellationRacingWithAcknowledgmentMakesProgressExactlyOnce()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var full = await coordinator.AdmitAsync("fill", new string('a', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        using var cts = new CancellationTokenSource();
        var waiting = coordinator.AdmitAsync("racer", "payload", cts.Token).AsTask();
        await SubmitAsync(coordinator, full);
        var cancel = Task.Run(cts.Cancel);
        var ack = Task.Run(() => coordinator.Acknowledge(full.Acknowledgment));
        await Task.WhenAll(cancel, ack).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AcknowledgmentResult.Acknowledged, await ack);

        if (waiting.IsCompletedSuccessfully)
        {
            var admitted = await waiting;
            await SubmitAsync(coordinator, admitted);
            Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(admitted.Acknowledgment));
            Assert.Equal(2, coordinator.Snapshot.AcknowledgedFrames);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, coordinator.Snapshot.PendingUtf16CodeUnits);
            Assert.Equal(0, coordinator.Snapshot.WaitingProducers);
        }
        Assert.InRange(coordinator.Snapshot.ReservedUtf16CodeUnits, 0, BoundedOutputCoordinator.MaximumUtf16CodeUnits);
    }

    [Fact]
    public async Task RetirementWakesOldReaderWithoutCrossGenerationDelivery()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var oldGeneration = coordinator.GenerationId;
        var oldRead = coordinator.ReadAsync().AsTask();
        Assert.True(coordinator.RetireGeneration(oldGeneration, "session stopped"));
        Assert.NotEqual(oldGeneration, coordinator.GenerationId);
        var next = await coordinator.AdmitAsync("p", "new generation");
        Assert.True(next.Sequence > 0);
        Assert.NotEqual(oldGeneration, next.GenerationId);
        var nextRead = coordinator.ReadAsync().AsTask();
        Assert.Null(await oldRead.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(next, await nextRead.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(coordinator.MarkRendererSubmitted(next.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(next.Acknowledgment));

        var retired = DrainDiagnosticItems(coordinator).Single(item => item.Kind == "GenerationRetired");
        Assert.Equal(oldGeneration, retired.GenerationId);
        Assert.Equal(coordinator.Snapshot.AdmittedUtf16CodeUnits,
            coordinator.Snapshot.AcknowledgedUtf16CodeUnits + coordinator.Snapshot.ReservedUtf16CodeUnits + coordinator.Snapshot.RetiredUtf16CodeUnits);
    }

    [Fact]
    public async Task RetiredSourceIdentityCannotBeRelabeledIntoNewCoordinatorEpoch()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var oldEpoch = coordinator.GenerationId;
        Assert.True(coordinator.BindSessionGeneration(oldEpoch, 41));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.AdmitAsync("unbound", "missing identity"));
        var admitted = await coordinator.AdmitAsync(oldEpoch, 41, "Interactive", null, 1, "old session");
        Assert.Equal(41, admitted.SessionGeneration);
        Assert.True(coordinator.RetireGeneration(oldEpoch, "PSES session stopped"));

        var newEpoch = coordinator.GenerationId;
        Assert.NotEqual(oldEpoch, newEpoch);
        Assert.False(coordinator.BindSessionGeneration(oldEpoch, 42));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.AdmitAsync(oldEpoch, 41, "Interactive", null, 2, "stale producer"));

        Assert.True(coordinator.BindSessionGeneration(newEpoch, 42));
        var current = await coordinator.AdmitAsync(newEpoch, 42, "StructuredEditor", Guid.NewGuid(), 7, "new session");
        Assert.Equal(newEpoch, current.GenerationId);
        Assert.Equal(42, current.SessionGeneration);
        Assert.Equal(7, current.SourceSequence);
        Assert.Equal(coordinator.Snapshot.AdmittedUtf16CodeUnits,
            coordinator.Snapshot.AcknowledgedUtf16CodeUnits + coordinator.Snapshot.ReservedUtf16CodeUnits + coordinator.Snapshot.RetiredUtf16CodeUnits);
    }

    [Fact]
    public async Task RetireVersusAdmitNeverReservesOldSourceInNewEpoch()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var oldEpoch = coordinator.GenerationId;
        Assert.True(coordinator.BindSessionGeneration(oldEpoch, 3));
        using var start = new ManualResetEventSlim();
        var producer = Task.Run(async () =>
        {
            start.Wait();
            try
            {
                var frame = await coordinator.AdmitAsync(oldEpoch, 3, "Interactive", null, 1, "race");
                Assert.Equal(oldEpoch, frame.GenerationId);
                Assert.Equal(3, frame.SessionGeneration);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
        var retire = Task.Run(() =>
        {
            start.Wait();
            return coordinator.RetireGeneration(oldEpoch, "retire/admit interleave");
        });
        start.Set();
        Assert.True(await retire.WaitAsync(TimeSpan.FromSeconds(2)));
        _ = await producer.WaitAsync(TimeSpan.FromSeconds(2));

        var currentEpoch = coordinator.GenerationId;
        Assert.True(coordinator.BindSessionGeneration(currentEpoch, 4));
        var current = await coordinator.AdmitAsync(currentEpoch, 4, "Interactive", null, 2, "current");
        Assert.Equal(currentEpoch, current.GenerationId);
        Assert.Equal(4, current.SessionGeneration);
        Assert.Equal(1, coordinator.Snapshot.OutstandingFrames);
    }

    [Fact]
    public async Task CancellationRacingWithRetirementCompletesPromptly()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var generation = coordinator.GenerationId;
        _ = await coordinator.AdmitAsync("fill", new string('x', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        using var cts = new CancellationTokenSource();
        var waiting = coordinator.AdmitAsync("racer", "text", cts.Token).AsTask();
        var cancel = Task.Run(cts.Cancel);
        var retire = Task.Run(() => coordinator.RetireGeneration(generation, "race"));
        await Task.WhenAll(cancel, retire).WaitAsync(TimeSpan.FromSeconds(3));
        try { _ = await waiting.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        Assert.True(waiting.IsCompleted);
        Assert.Equal(0, coordinator.Snapshot.PendingUtf16CodeUnits);
        Assert.Equal(0, coordinator.Snapshot.WaitingProducers);
    }

    [Fact]
    public async Task DuplicateUnknownLateStaleAndAfterDisposeAcknowledgmentsAreDeterministic()
    {
        var coordinator = new BoundedOutputCoordinator();
        var frame = await coordinator.AdmitAsync("p", "a");
        Assert.Equal(AcknowledgmentResult.Unknown, coordinator.Acknowledge(new OutputAcknowledgment(frame.GenerationId, long.MaxValue)));
        Assert.Equal(AcknowledgmentResult.NotRendererSubmitted, coordinator.Acknowledge(frame.Acknowledgment));
        await SubmitAsync(coordinator, frame);
        Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(frame.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Duplicate, coordinator.Acknowledge(frame.Acknowledgment));
        Assert.Equal(AcknowledgmentResult.Invalid, coordinator.Acknowledge(new OutputAcknowledgment(frame.GenerationId, 0)));
        for (var i = 0; i < BoundedOutputCoordinator.MaximumFrames; i++)
        {
            var more = await coordinator.AdmitAsync("history", "x");
            await SubmitAsync(coordinator, more);
            Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(more.Acknowledgment));
        }
        Assert.Equal(AcknowledgmentResult.Late, coordinator.Acknowledge(frame.Acknowledgment));
        Assert.True(coordinator.RetireGeneration(frame.GenerationId, "stale check"));
        Assert.Equal(AcknowledgmentResult.StaleGeneration, coordinator.Acknowledge(frame.Acknowledgment));
        await coordinator.DisposeAsync();
        Assert.Equal(AcknowledgmentResult.Disposed, coordinator.Acknowledge(new OutputAcknowledgment(coordinator.GenerationId, 99)));
    }

    [Fact]
    public async Task DisposalRetiresFramesAndCompletesWaitingTasks()
    {
        var coordinator = new BoundedOutputCoordinator();
        _ = await coordinator.AdmitAsync("fill", new string('a', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        var blocked = coordinator.AdmitAsync("p", "blocked").AsTask();
        await coordinator.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => blocked);
        Assert.Null(await coordinator.ReadAsync());
        Assert.Equal(0, coordinator.Snapshot.ReservedUtf16CodeUnits);
        Assert.Equal(0, coordinator.Snapshot.PendingUtf16CodeUnits);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await coordinator.AdmitAsync("p", "after dispose"));
    }

    [Fact]
    public async Task ConcurrentProducersAndOrderedConsumerPreserveIdentityAndAccounting()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        const int producerCount = 8;
        const int perProducer = 100;
        var consumers = Task.Run(async () =>
        {
            var seen = new HashSet<long>();
            while (seen.Count < producerCount * perProducer)
            {
                var frame = (await coordinator.ReadAsync())!;
                Assert.True(seen.Add(frame.Sequence));
                Assert.True(coordinator.MarkRendererSubmitted(frame.Acknowledgment));
                Assert.Equal(AcknowledgmentResult.Acknowledged, coordinator.Acknowledge(frame.Acknowledgment));
            }
            return seen;
        });
        var producers = Enumerable.Range(0, producerCount).Select(async producer =>
        {
            for (var i = 0; i < perProducer; i++)
                await coordinator.AdmitAsync($"p{producer}", $"{producer}:{i}");
        });
        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(10));
        var seen = await consumers.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(producerCount * perProducer, seen.Count);
        Assert.Equal(0, coordinator.Snapshot.OutstandingFrames);
        Assert.Equal(producerCount * perProducer, coordinator.Snapshot.AcknowledgedFrames);
    }

    [Fact]
    public async Task DiagnosticOverflowIsCountedSeparatelyFromPayloadOutcomes()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        var frame = await coordinator.AdmitAsync("p", "text");
        for (var i = 0; i < 400; i++)
            Assert.Equal(AcknowledgmentResult.NotRendererSubmitted, coordinator.Acknowledge(frame.Acknowledgment));
        Assert.True(coordinator.Snapshot.DroppedDiagnosticRecords > 0);
        Assert.Equal(1, coordinator.Snapshot.OutstandingFrames);
        Assert.Equal(4, coordinator.Snapshot.ReservedUtf16CodeUnits);
    }

    [Fact]
    public async Task InvalidInputAndWaiterLimitReportExplicitFailure()
    {
        await using var coordinator = new BoundedOutputCoordinator();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await coordinator.AdmitAsync("large", new string('x', BoundedOutputCoordinator.MaximumUtf16CodeUnits + 1)));
        _ = await coordinator.AdmitAsync("fill", new string('x', BoundedOutputCoordinator.MaximumUtf16CodeUnits));
        var waiters = Enumerable.Range(0, 256).Select(i => coordinator.AdmitAsync($"p{i}", "pending").AsTask()).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.AdmitAsync("overflow", "explicit failure"));
        Assert.Equal(256, coordinator.Snapshot.WaitingProducers);
        Assert.True(coordinator.Snapshot.FailureCount > 0);
        _ = DrainDiagnosticItems(coordinator);
        Assert.True(coordinator.Snapshot.FailureCount > 0);
        await coordinator.DisposeAsync();
        await Task.WhenAll(waiters.Select(task => Assert.ThrowsAsync<ObjectDisposedException>(() => task)));
    }

    private static async Task<OutputFrame> DispatchAsync(BoundedOutputCoordinator coordinator)
    {
        var frame = (await coordinator.ReadAsync())!;
        Assert.True(coordinator.MarkRendererSubmitted(frame.Acknowledgment));
        return frame;
    }

    private static async Task SubmitAsync(BoundedOutputCoordinator coordinator, OutputFrame frame)
    {
        Assert.Same(frame, await coordinator.ReadAsync());
        Assert.True(coordinator.MarkRendererSubmitted(frame.Acknowledgment));
    }

    private static OutputCoordinatorDiagnostic[] DrainDiagnosticItems(BoundedOutputCoordinator coordinator)
    {
        var items = new List<OutputCoordinatorDiagnostic>();
        while (coordinator.TryReadDiagnostic(out var item)) items.Add(item!);
        return items.ToArray();
    }

    private static string[] DrainDiagnostics(BoundedOutputCoordinator coordinator) =>
        DrainDiagnosticItems(coordinator).Select(item => item.Kind).ToArray();
}
