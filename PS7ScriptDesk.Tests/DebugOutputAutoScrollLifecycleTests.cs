using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

public sealed class DebugOutputAutoScrollLifecycleTests
{
    [Fact]
    public void CheckedBeforeControlsExist_IsSafeAndDoesNotRequestScroll()
    {
        Assert.False(DebugOutputAutoScrollLifecycle.ShouldRequestScroll(false, false, true, false, 3));
    }

    [Fact]
    public void CheckedWithEmptyList_IsSafeAndDoesNotRequestScroll()
    {
        Assert.False(DebugOutputAutoScrollLifecycle.ShouldRequestScroll(true, true, true, false, 0));
    }

    [Fact]
    public void CheckedWithExistingItems_RequestsNewestItemNavigation()
    {
        Assert.True(DebugOutputAutoScrollLifecycle.ShouldRequestScroll(true, true, true, false, 2));
    }

    [Fact]
    public void SuppressedOrDisabledAutoScroll_DoesNotRequestNavigation()
    {
        Assert.False(DebugOutputAutoScrollLifecycle.ShouldRequestScroll(true, true, true, true, 2));
        Assert.False(DebugOutputAutoScrollLifecycle.ShouldRequestScroll(true, true, false, false, 2));
    }

    [Fact]
    public void BottomDetection_UsesScrollViewerGeometryWithTolerance()
    {
        Assert.True(DebugOutputAutoScrollLifecycle.IsAtBottom(98, 100));
        Assert.True(DebugOutputAutoScrollLifecycle.IsAtBottom(0, 0));
        Assert.False(DebugOutputAutoScrollLifecycle.IsAtBottom(40, 100));
    }
}
