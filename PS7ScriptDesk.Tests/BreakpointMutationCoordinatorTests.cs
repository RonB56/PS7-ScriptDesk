using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class BreakpointMutationCoordinatorTests
{
    [Fact]
    public async Task NoDebuggerToggleAndRemoveAllChangeOpenModels()
    {
        var first = Tab("first.ps1");
        var second = Tab("second.ps1");
        var coordinator = new BreakpointMutationCoordinator();
        var tabs = new[] { first, second };
        var noDebug = () => new BreakpointDebugContext(null, null, false, null);

        Assert.True((await coordinator.MutateAsync(tabs, first, 2, BreakpointMutationKind.Toggle, noDebug)).Added);
        Assert.True((await coordinator.MutateAsync(tabs, second, 3, BreakpointMutationKind.Toggle, noDebug)).Added);
        Assert.False((await coordinator.MutateAsync(tabs, first, 2, BreakpointMutationKind.Toggle, noDebug)).Added);
        Assert.Equal(1, (await coordinator.MutateAsync(tabs, null, 0, BreakpointMutationKind.ClearAll, noDebug)).ChangedCount);
        Assert.Empty(first.BreakpointLineNumbers);
        Assert.Empty(second.BreakpointLineNumbers);
    }

    [Fact]
    public async Task ActiveDebuggerAddAndRemoveSendNewSetBeforeModelCommit()
    {
        var tab = Tab("first.ps1");
        tab.ToggleBreakpoint(2);
        var observed = new List<int[]>();
        var coordinator = new BreakpointMutationCoordinator();
        var context = Context(tab, (lines, _) =>
        {
            observed.Add(lines.ToArray());
            Assert.Equal(observed.Count == 1 ? new[] { 2 } : new[] { 2, 4 }, tab.BreakpointLineNumbers);
            return Task.CompletedTask;
        });

        Assert.True((await coordinator.MutateAsync([tab], tab, 4, BreakpointMutationKind.Toggle, context)).Added);
        Assert.False((await coordinator.MutateAsync([tab], tab, 2, BreakpointMutationKind.Remove, context)).Added);
        Assert.Equal(new[] { 2, 4 }, observed[0]);
        Assert.Equal(new[] { 4 }, observed[1]);
        Assert.Equal(new[] { 4 }, tab.BreakpointLineNumbers);
    }

    [Fact]
    public async Task DapFailurePreservesAllMarkers()
    {
        var tab = Tab("first.ps1");
        tab.ToggleBreakpoint(2);
        var coordinator = new BreakpointMutationCoordinator();
        var context = Context(tab, (_, _) => throw new InvalidOperationException("DAP rejected"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MutateAsync([tab], tab, 2, BreakpointMutationKind.Toggle, context));
        Assert.Equal(new[] { 2 }, tab.BreakpointLineNumbers);
    }

    [Fact]
    public async Task RapidTogglesAreSerializedAndUseCommittedState()
    {
        var tab = Tab("first.ps1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sets = new List<int[]>();
        var coordinator = new BreakpointMutationCoordinator();
        var context = Context(tab, async (lines, _) =>
        {
            sets.Add(lines.ToArray());
            if (sets.Count == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        });

        var first = coordinator.MutateAsync([tab], tab, 3, BreakpointMutationKind.Toggle, context);
        await entered.Task;
        var second = coordinator.MutateAsync([tab], tab, 3, BreakpointMutationKind.Toggle, context);
        Assert.Empty(tab.BreakpointLineNumbers);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { 3 }, sets[0]);
        Assert.Empty(sets[1]);
        Assert.Empty(tab.BreakpointLineNumbers);
    }

    [Fact]
    public async Task InactiveTabDoesNotReplaceLaunchBreakpointsAndClearAllSynchronizesLaunch()
    {
        var active = Tab("active.ps1");
        var other = Tab("other.ps1");
        active.ToggleBreakpoint(1);
        other.ToggleBreakpoint(2);
        var sets = new List<int[]>();
        var coordinator = new BreakpointMutationCoordinator();
        var context = Context(active, (lines, _) => { sets.Add(lines.ToArray()); return Task.CompletedTask; });

        await coordinator.MutateAsync([active, other], other, 4, BreakpointMutationKind.Toggle, context);
        Assert.Empty(sets);
        var result = await coordinator.MutateAsync([active, other], null, 0, BreakpointMutationKind.ClearAll, context);
        Assert.Equal(3, result.ChangedCount);
        Assert.Single(sets);
        Assert.Empty(sets[0]);
        Assert.Empty(active.BreakpointLineNumbers);
        Assert.Empty(other.BreakpointLineNumbers);
    }

    [Fact]
    public async Task SessionChangeDuringDapRequestLeavesModelUnchanged()
    {
        var tab = Tab("active.ps1");
        var session = Guid.NewGuid();
        var coordinator = new BreakpointMutationCoordinator();
        var context = () => new BreakpointDebugContext(session, tab, true, (_, _) =>
        {
            session = Guid.NewGuid();
            return Task.CompletedTask;
        });

        var result = await coordinator.MutateAsync([tab], tab, 1, BreakpointMutationKind.Toggle, context);
        Assert.False(result.Accepted);
        Assert.Empty(tab.BreakpointLineNumbers);
    }

    [Fact]
    public async Task StaleActiveSourceRejectsMutationBeforeDapRequest()
    {
        var tab = Tab("active.ps1");
        tab.ToggleBreakpoint(2);
        var called = false;
        var coordinator = new BreakpointMutationCoordinator();
        var context = () => new BreakpointDebugContext(Guid.NewGuid(), tab, false, (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var result = await coordinator.MutateAsync([tab], tab, 2, BreakpointMutationKind.Remove, context);
        Assert.False(result.Accepted);
        Assert.False(called);
        Assert.Equal(new[] { 2 }, tab.BreakpointLineNumbers);
    }

    [Fact]
    public async Task RemoveAllDapFailurePreservesMultipleTabs()
    {
        var active = Tab("active.ps1");
        var inactive = Tab("inactive.ps1");
        active.ToggleBreakpoint(1);
        inactive.ToggleBreakpoint(3);
        var coordinator = new BreakpointMutationCoordinator();
        var context = Context(active, (_, _) => throw new TimeoutException("DAP timeout"));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            coordinator.MutateAsync([active, inactive], null, 0, BreakpointMutationKind.ClearAll, context));
        Assert.Equal(new[] { 1 }, active.BreakpointLineNumbers);
        Assert.Equal(new[] { 3 }, inactive.BreakpointLineNumbers);
    }

    private static EditorTabViewModel Tab(string path) => new(path, "one\ntwo\nthree\nfour", path);

    private static Func<BreakpointDebugContext> Context(
        EditorTabViewModel tab, Func<IReadOnlyList<int>, CancellationToken, Task> synchronize)
    {
        var session = Guid.NewGuid();
        return () => new BreakpointDebugContext(session, tab, true, synchronize);
    }
}
