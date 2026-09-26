using System.Globalization;
using System.Windows;

namespace PS7ScriptDesk.Shell.Layout;

public sealed record LayoutGeometrySnapshot(
    double WorkspaceWidth,
    double WorkspaceHeight,
    double ActiveHorizontalWidth,
    double ActiveVerticalHeight,
    double ExplorerWidth,
    double EditorWidth,
    double ConsoleWidth,
    double DebugWidth,
    double LowerToolHeight,
    double InactiveColumnWidth,
    double InactiveRowHeight,
    bool ExplorerVisible,
    bool DebugVisible,
    bool LowerToolsVisible,
    bool DebugFloating,
    bool LowerToolsFloating);

public sealed class WorkspaceLayoutCoordinator
{
    private int _transitionDepth;

    public WorkspaceLayoutCoordinator(WorkspaceLayoutState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public WorkspaceLayoutState State { get; }

    public string? ActiveTransitionReason { get; private set; }

    public bool IsTransitionActive => _transitionDepth > 0;

    public LayoutTransitionScope BeginTransition(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _transitionDepth++;
        ActiveTransitionReason ??= reason;
        return new LayoutTransitionScope(this, reason, _transitionDepth == 1);
    }

    public void SetWorkspaceMode(WorkspaceMode mode)
    {
        State.WorkspaceMode = mode;
        State.ActiveConsoleRegion = mode == WorkspaceMode.SideBySideSplit ? "ConsoleSideBySide" : "Console";
    }

    public void SetDebugDockState(LayoutDockState dockState)
    {
        State.Debug.DockState = dockState;
        State.Debug.IsVisible = dockState != LayoutDockState.Hidden;
    }

    public void SetLowerToolDockState(LayoutDockState dockState)
    {
        State.LowerTools.DockState = dockState;
        State.LowerTools.IsVisible = dockState != LayoutDockState.Hidden;
    }

    public void CaptureExplorerWidth(double actualWidth)
    {
        if (IsValidDimension(actualWidth, State.Explorer.MinimumWidth))
        {
            State.Explorer.RequestedWidth = actualWidth;
        }
    }

    public void CaptureConsoleHeight(double actualHeight)
    {
        if (IsValidDimension(actualHeight, State.Console.MinimumHeight))
        {
            State.Console.RequestedVerticalHeight = actualHeight;
        }
    }

    public void CaptureConsoleSideWidth(double actualWidth)
    {
        if (IsValidDimension(actualWidth, State.Console.MinimumSideBySideWidth))
        {
            State.Console.RequestedSideBySideWidth = actualWidth;
        }
    }

    public void CaptureDebugDockedWidth(double actualWidth)
    {
        if (IsValidDimension(actualWidth, State.Debug.MinimumWidth))
        {
            State.Debug.RequestedDockedWidth = actualWidth;
        }
    }

    public void CaptureLowerToolHeight(double actualHeight)
    {
        if (IsValidDimension(actualHeight, State.LowerTools.MinimumDockedHeight))
        {
            State.LowerTools.RequestedDockedHeight = actualHeight;
        }
    }

    public LayoutInvariantResult Validate(LayoutGeometrySnapshot geometry)
    {
        var violations = new List<LayoutInvariantViolation>();
        ValidateFinite(violations, "ExplorerRequestedWidth", State.Explorer.RequestedWidth);
        ValidateFinite(violations, "ConsoleHeight", State.Console.RequestedVerticalHeight);
        ValidateFinite(violations, "ConsoleSideWidth", State.Console.RequestedSideBySideWidth);
        ValidateFinite(violations, "DebugDockedWidth", State.Debug.RequestedDockedWidth);
        ValidateFinite(violations, "BottomToolDockedHeight", State.LowerTools.RequestedDockedHeight);
        ValidateNonNegative(violations, "WorkspaceWidth", geometry.WorkspaceWidth);
        ValidateNonNegative(violations, "WorkspaceHeight", geometry.WorkspaceHeight);
        ValidateNonNegative(violations, "ActiveHorizontalWidth", geometry.ActiveHorizontalWidth);
        ValidateNonNegative(violations, "ActiveVerticalHeight", geometry.ActiveVerticalHeight);
        ValidateNonNegative(violations, "InactiveColumnWidth", geometry.InactiveColumnWidth);
        ValidateNonNegative(violations, "InactiveRowHeight", geometry.InactiveRowHeight);

        var budget = LayoutBudgetResult.FromActual(
            geometry.WorkspaceWidth,
            geometry.ActiveHorizontalWidth,
            geometry.WorkspaceHeight,
            geometry.ActiveVerticalHeight);

        if (geometry.InactiveColumnWidth > 1)
        {
            violations.Add(new LayoutInvariantViolation("InactiveColumnGeometry", $"Inactive column geometry is {geometry.InactiveColumnWidth.ToString("0.###", CultureInfo.InvariantCulture)}."));
        }

        if (geometry.InactiveRowHeight > 1)
        {
            violations.Add(new LayoutInvariantViolation("InactiveRowGeometry", $"Inactive row geometry is {geometry.InactiveRowHeight.ToString("0.###", CultureInfo.InvariantCulture)}."));
        }

        if (geometry.DebugFloating && geometry.DebugVisible)
        {
            violations.Add(new LayoutInvariantViolation("FloatingDebugRetainsDockedVisibility", "Floating Debug is still marked visibly docked.", false));
        }

        if (geometry.LowerToolsFloating && geometry.LowerToolsVisible)
        {
            violations.Add(new LayoutInvariantViolation("FloatingLowerToolsRetainsDockedVisibility", "Floating lower tools are still marked visibly docked.", false));
        }

        AddMinimumWarning(violations, "ExplorerMinimum", geometry.ExplorerVisible, geometry.ExplorerWidth, State.Explorer.MinimumWidth, geometry.WorkspaceWidth);
        AddMinimumWarning(violations, "DebugMinimum", geometry.DebugVisible && !geometry.DebugFloating, geometry.DebugWidth, State.Debug.MinimumWidth, geometry.WorkspaceWidth);
        AddMinimumWarning(violations, "ConsoleSideMinimum", State.WorkspaceMode == WorkspaceMode.SideBySideSplit, geometry.ConsoleWidth, State.Console.MinimumSideBySideWidth, geometry.WorkspaceWidth);

        if (budget.HasUnderfill)
        {
            violations.Add(new LayoutInvariantViolation("LayoutUnderfill", $"Active layout underfills workspace by {budget.UnusedHorizontalWidth:0.###} horizontal and {budget.UnusedVerticalHeight:0.###} vertical units.", false));
        }

        if (budget.HasOverflow)
        {
            violations.Add(new LayoutInvariantViolation("LayoutOverflow", $"Active layout exceeds workspace by {budget.OverflowHorizontalWidth:0.###} horizontal and {budget.OverflowVerticalHeight:0.###} vertical units."));
        }

        return new LayoutInvariantResult(violations, budget);
    }

    private static void ValidateFinite(ICollection<LayoutInvariantViolation> violations, string name, double value)
    {
        if (!double.IsFinite(value))
        {
            violations.Add(new LayoutInvariantViolation("NonFiniteRequestedDimension", $"{name} is {value}."));
        }
    }

    private static void ValidateNonNegative(ICollection<LayoutInvariantViolation> violations, string name, double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            violations.Add(new LayoutInvariantViolation("NegativeOrNonFiniteActualDimension", $"{name} is {value}."));
        }
    }

    private static void AddMinimumWarning(ICollection<LayoutInvariantViolation> violations, string code, bool visible, double actual, double minimum, double available)
    {
        if (visible && available >= minimum && actual + 1 < minimum)
        {
            violations.Add(new LayoutInvariantViolation(code, $"Visible pane actual size {actual:0.###} is below minimum {minimum:0.###}.", false));
        }
    }

    private static bool IsValidDimension(double value, double minimum)
        => double.IsFinite(value) && value >= minimum;

    private void EndTransition(LayoutTransitionScope scope)
    {
        if (_transitionDepth > 0)
        {
            _transitionDepth--;
        }

        if (_transitionDepth == 0 && string.Equals(ActiveTransitionReason, scope.Reason, StringComparison.Ordinal))
        {
            ActiveTransitionReason = null;
        }
    }

    public sealed class LayoutTransitionScope : IDisposable
    {
        private readonly WorkspaceLayoutCoordinator _owner;
        private bool _disposed;

        internal LayoutTransitionScope(WorkspaceLayoutCoordinator owner, string reason, bool isOuter)
        {
            _owner = owner;
            Reason = reason;
            IsOuter = isOuter;
        }

        public string Reason { get; }

        public bool IsOuter { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndTransition(this);
        }
    }
}
