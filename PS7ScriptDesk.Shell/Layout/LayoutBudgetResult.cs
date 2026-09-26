namespace PS7ScriptDesk.Shell.Layout;

public sealed record LayoutBudgetResult(
    double AvailableHorizontalWidth,
    double ActiveHorizontalWidth,
    double UnusedHorizontalWidth,
    double OverflowHorizontalWidth,
    double AvailableVerticalHeight,
    double ActiveVerticalHeight,
    double UnusedVerticalHeight,
    double OverflowVerticalHeight)
{
    public bool HasUnderfill => UnusedHorizontalWidth > 1 || UnusedVerticalHeight > 1;

    public bool HasOverflow => OverflowHorizontalWidth > 1 || OverflowVerticalHeight > 1;

    public static LayoutBudgetResult FromActual(
        double availableWidth,
        double activeWidth,
        double availableHeight,
        double activeHeight)
    {
        var horizontalDelta = availableWidth - activeWidth;
        var verticalDelta = availableHeight - activeHeight;
        return new LayoutBudgetResult(
            availableWidth,
            activeWidth,
            Math.Max(0, horizontalDelta),
            Math.Max(0, -horizontalDelta),
            availableHeight,
            activeHeight,
            Math.Max(0, verticalDelta),
            Math.Max(0, -verticalDelta));
    }
}
