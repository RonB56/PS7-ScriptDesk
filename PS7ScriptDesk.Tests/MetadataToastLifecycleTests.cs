using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class MetadataToastLifecycleTests
{
    [Fact]
    public void SuccessIsHandledOnceAndRoutineProgressCannotRestartItsLifetime()
    {
        var lifecycle = new MetadataToastLifecycleState();

        Assert.True(lifecycle.ObserveProgress(isExplicitStart: true));
        Assert.True(lifecycle.ObserveProgress(isExplicitStart: false));
        Assert.True(lifecycle.TryCompleteInitialization());
        Assert.False(lifecycle.TryCompleteInitialization());
        Assert.False(lifecycle.ObserveProgress(isExplicitStart: false));
        Assert.True(lifecycle.ObserveProgress(isExplicitStart: true));
    }

    [Fact]
    public void FailureCanBeFollowedByANewSuccessfulInitialization()
    {
        var lifecycle = new MetadataToastLifecycleState();

        Assert.True(lifecycle.ObserveProgress(isExplicitStart: true));
        lifecycle.EndInitialization();
        Assert.False(lifecycle.TryCompleteInitialization());
        Assert.True(lifecycle.ObserveProgress(isExplicitStart: true));
        Assert.True(lifecycle.TryCompleteInitialization());
    }

    [Fact]
    public void SuccessDismissalFiresAtThreeSecondsWithoutSleeping()
    {
        var scheduler = new FakeScheduler();
        bool dismissed = false;
        using var dismissal = new MetadataToastDismissal(scheduler.Schedule, scheduler.Cancel);

        dismissal.Schedule(
            TimeSpan.FromMilliseconds(MainWindow.MetadataToastSuccessDismissMilliseconds),
            () => dismissed = true);

        Assert.Equal(TimeSpan.FromSeconds(3), scheduler.LastDelay);
        Assert.False(dismissed);
        scheduler.Fire(0);
        Assert.True(dismissed);
    }

    [Fact]
    public void StaleDismissalCannotHideReplacementAndShutdownInvalidatesCallbacks()
    {
        var scheduler = new FakeScheduler();
        int dismissals = 0;
        var dismissal = new MetadataToastDismissal(scheduler.Schedule, scheduler.Cancel);

        dismissal.Schedule(TimeSpan.FromSeconds(3), () => dismissals++);
        dismissal.Schedule(TimeSpan.FromSeconds(9), () => dismissals++);
        scheduler.Fire(0);
        Assert.Equal(0, dismissals);
        scheduler.Fire(1);
        Assert.Equal(1, dismissals);

        dismissal.Schedule(TimeSpan.FromSeconds(3), () => dismissals++);
        dismissal.Dispose();
        scheduler.Fire(2);
        Assert.Equal(1, dismissals);
    }

    private sealed class FakeScheduler
    {
        private readonly List<Action> _callbacks = [];

        public TimeSpan LastDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            _callbacks.Add(callback);
        }

        public void Cancel()
        {
        }

        public void Fire(int index) => _callbacks[index]();
    }
}
