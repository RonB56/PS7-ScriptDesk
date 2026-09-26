namespace PS7ScriptDesk.Shell.Debug;

internal static class DebugOutputAutoScrollLifecycle
{
    public static bool ShouldRequestScroll(
        bool controlsReady,
        bool windowLoaded,
        bool autoScrollEnabled,
        bool autoScrollSuppressed,
        int itemCount) =>
        controlsReady &&
        windowLoaded &&
        autoScrollEnabled &&
        !autoScrollSuppressed &&
        itemCount > 0;

    public static bool IsAtBottom(
        double verticalOffset,
        double scrollableHeight,
        double tolerance = 2d) =>
        scrollableHeight <= tolerance ||
        scrollableHeight - verticalOffset <= tolerance;
}
