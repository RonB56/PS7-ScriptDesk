using PS7ScriptDesk.Shell.Editor;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class LiveSyntaxPumpStateTests
{
    [Fact]
    public void FaultedWorkerCanBeReplacedOnlyOnceAndKeepsLatestRequest()
    {
        using var state = new LiveSyntaxPumpState();
        var request = Work("latest", registration: 3, version: 9);
        state.Publish(request);
        var failedWorker = Task.FromException(new InvalidOperationException("parser worker fault"));
        _ = failedWorker.Exception;
        Assert.True(state.SetWorkerTask(failedWorker));

        Assert.True(state.TryClaimRecovery(failedWorker));
        Assert.Equal(1, state.RecoveryAttempts);
        Assert.Same(request, state.LatestWorkItem);

        var replacementWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(state.SetWorkerTask(replacementWorker.Task));
        Assert.True(state.SignalLatestWorkItem());
        Assert.Equal(1, state.Signal.CurrentCount);

        replacementWorker.SetException(new InvalidOperationException("persistent parser worker fault"));
        Assert.False(state.TryClaimRecovery(replacementWorker.Task));
        Assert.Equal(1, state.RecoveryAttempts);
    }

    [Fact]
    public async Task PublishesCoalesceToLatestSnapshotWithOnePendingWakeSignal()
    {
        using var state = new LiveSyntaxPumpState();
        state.Publish(Work("first", registration: 1, version: 1));
        var latest = Work("latest", registration: 1, version: 3);
        state.Publish(Work("middle", registration: 1, version: 2));
        state.Publish(latest);

        Assert.Equal(1, state.Signal.CurrentCount);
        await state.WaitForSignalAsync(CancellationToken.None);
        Assert.Same(latest, state.LatestWorkItem);
        Assert.Equal(0, state.Signal.CurrentCount);
    }

    [Fact]
    public void DisposalCancelsWorkerAndPreventsRecoveryOrFurtherPublication()
    {
        var state = new LiveSyntaxPumpState();
        state.Publish(Work("pending", registration: 1, version: 1));
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(state.SetWorkerTask(worker.Task));

        state.Dispose();
        worker.SetException(new InvalidOperationException("late failure"));
        Assert.True(state.CancellationToken.IsCancellationRequested);
        Assert.False(state.TryClaimRecovery(worker.Task));
        Assert.True(state.IsDisposed);
        state.Publish(Work("ignored", registration: 2, version: 2));
        Assert.Equal("pending", state.LatestWorkItem?.ScriptSnapshot);
    }

    [Fact]
    public void StaleRequestIsRejectedByRegistrationVersionRequestVersionOrDocumentSnapshot()
    {
        var workItem = Work("current", registration: 4, version: 8);

        var newerSnapshot = CreateSnapshot("newer text");
        Assert.True(LiveSyntaxRequestFreshness.IsCurrent(4, 8, workItem.DocumentSnapshot, workItem));
        Assert.False(LiveSyntaxRequestFreshness.IsCurrent(5, 8, workItem.DocumentSnapshot, workItem));
        Assert.False(LiveSyntaxRequestFreshness.IsCurrent(4, 9, workItem.DocumentSnapshot, workItem));
        Assert.False(LiveSyntaxRequestFreshness.IsCurrent(4, 8, newerSnapshot, workItem));
    }

    private static LiveSyntaxWorkItem Work(string text, int registration, int version)
        => new(new EditorTextSnapshotMaterialization(CreateSnapshot(text)), "pwsh.exe", registration, version, 1, "test.ps1", null);

    private static EditorDocumentSnapshot CreateSnapshot(string text)
    {
        EditorDocumentSnapshot? snapshot = null;
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("test.ps1", text);
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            session.Register();
            snapshot = session.CaptureSnapshot();
        });
        return snapshot!;
    }
}
