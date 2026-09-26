using System.Windows;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Layout;

public enum WorkspaceMode
{
    Default,
    EditorMaximized,
    ConsoleMaximized,
    HorizontalSplit,
    SideBySideSplit
}

public enum LayoutDockState
{
    Hidden,
    Docked,
    Floating
}

public enum LayoutBottomToolTab
{
    Problems,
    DebugOutput,
    Activity
}

public class PaneGeometryState
{
    public bool IsVisible { get; set; }

    public double RequestedWidth { get; set; }

    public double MinimumWidth { get; set; }

    public PaneGeometryState Clone()
        => new()
        {
            IsVisible = IsVisible,
            RequestedWidth = RequestedWidth,
            MinimumWidth = MinimumWidth
        };
}

public sealed class EditorGeometryState : PaneGeometryState
{
    public bool UsesElasticWidth { get; set; } = true;

    public EditorGeometryState CloneEditor()
        => new()
        {
            IsVisible = IsVisible,
            RequestedWidth = RequestedWidth,
            MinimumWidth = MinimumWidth,
            UsesElasticWidth = UsesElasticWidth
        };
}

public sealed class ConsoleGeometryState : PaneGeometryState
{
    public double RequestedVerticalHeight { get; set; }

    public double MinimumHeight { get; set; }

    public double RequestedSideBySideWidth { get; set; }

    public double MinimumSideBySideWidth { get; set; }

    public ConsoleGeometryState CloneConsole()
        => new()
        {
            IsVisible = IsVisible,
            RequestedWidth = RequestedWidth,
            MinimumWidth = MinimumWidth,
            RequestedVerticalHeight = RequestedVerticalHeight,
            MinimumHeight = MinimumHeight,
            RequestedSideBySideWidth = RequestedSideBySideWidth,
            MinimumSideBySideWidth = MinimumSideBySideWidth
        };
}

public sealed class DebugGeometryState : PaneGeometryState
{
    public LayoutDockState DockState { get; set; }

    public double RequestedDockedWidth { get; set; }

    public Rect? FloatingBounds { get; set; }

    public DebugGeometryState CloneDebug()
        => new()
        {
            IsVisible = IsVisible,
            RequestedWidth = RequestedWidth,
            MinimumWidth = MinimumWidth,
            DockState = DockState,
            RequestedDockedWidth = RequestedDockedWidth,
            FloatingBounds = FloatingBounds
        };
}

public sealed class LowerToolGeometryState
{
    public bool IsVisible { get; set; }

    public LayoutDockState DockState { get; set; }

    public LayoutBottomToolTab SelectedTab { get; set; }

    public double RequestedDockedHeight { get; set; }

    public double MinimumDockedHeight { get; set; }

    public Rect? FloatingBounds { get; set; }

    public LowerToolGeometryState Clone()
        => new()
        {
            IsVisible = IsVisible,
            DockState = DockState,
            SelectedTab = SelectedTab,
            RequestedDockedHeight = RequestedDockedHeight,
            MinimumDockedHeight = MinimumDockedHeight,
            FloatingBounds = FloatingBounds
        };
}

public sealed class MainWindowGeometryState
{
    public Rect? RestoreBounds { get; set; }

    public bool IsMaximized { get; set; }

    public double AvailableWorkspaceWidth { get; set; }

    public double AvailableWorkspaceHeight { get; set; }

    public MainWindowGeometryState Clone()
        => new()
        {
            RestoreBounds = RestoreBounds,
            IsMaximized = IsMaximized,
            AvailableWorkspaceWidth = AvailableWorkspaceWidth,
            AvailableWorkspaceHeight = AvailableWorkspaceHeight
        };
}

public sealed class WorkspaceLayoutState
{
    public WorkspaceMode WorkspaceMode { get; set; } = WorkspaceMode.HorizontalSplit;

    public PaneGeometryState Explorer { get; } = new();

    public EditorGeometryState Editor { get; } = new();

    public ConsoleGeometryState Console { get; } = new();

    public DebugGeometryState Debug { get; } = new();

    public LowerToolGeometryState LowerTools { get; } = new();

    public MainWindowGeometryState MainWindow { get; } = new();

    public string ActiveEditorRegion { get; set; } = "Editor";

    public string ActiveConsoleRegion { get; set; } = "Console";

    public string ActiveDebugRegion { get; set; } = "Debug";

    public string ActiveLowerToolRegion { get; set; } = "BottomTools";

    public WorkspaceLayoutState Clone()
    {
        var clone = new WorkspaceLayoutState
        {
            WorkspaceMode = WorkspaceMode,
            ActiveEditorRegion = ActiveEditorRegion,
            ActiveConsoleRegion = ActiveConsoleRegion,
            ActiveDebugRegion = ActiveDebugRegion,
            ActiveLowerToolRegion = ActiveLowerToolRegion
        };
        Copy(Explorer, clone.Explorer);
        Copy(Editor, clone.Editor);
        Copy(Console, clone.Console);
        Copy(Debug, clone.Debug);
        Copy(LowerTools, clone.LowerTools);
        Copy(MainWindow, clone.MainWindow);
        return clone;
    }

    private static void Copy(PaneGeometryState source, PaneGeometryState destination)
    {
        destination.IsVisible = source.IsVisible;
        destination.RequestedWidth = source.RequestedWidth;
        destination.MinimumWidth = source.MinimumWidth;
    }

    private static void Copy(EditorGeometryState source, EditorGeometryState destination)
    {
        Copy((PaneGeometryState)source, destination);
        destination.UsesElasticWidth = source.UsesElasticWidth;
    }

    private static void Copy(ConsoleGeometryState source, ConsoleGeometryState destination)
    {
        Copy((PaneGeometryState)source, destination);
        destination.RequestedVerticalHeight = source.RequestedVerticalHeight;
        destination.MinimumHeight = source.MinimumHeight;
        destination.RequestedSideBySideWidth = source.RequestedSideBySideWidth;
        destination.MinimumSideBySideWidth = source.MinimumSideBySideWidth;
    }

    private static void Copy(DebugGeometryState source, DebugGeometryState destination)
    {
        Copy((PaneGeometryState)source, destination);
        destination.DockState = source.DockState;
        destination.RequestedDockedWidth = source.RequestedDockedWidth;
        destination.FloatingBounds = source.FloatingBounds;
    }

    private static void Copy(LowerToolGeometryState source, LowerToolGeometryState destination)
    {
        destination.IsVisible = source.IsVisible;
        destination.DockState = source.DockState;
        destination.SelectedTab = source.SelectedTab;
        destination.RequestedDockedHeight = source.RequestedDockedHeight;
        destination.MinimumDockedHeight = source.MinimumDockedHeight;
        destination.FloatingBounds = source.FloatingBounds;
    }

    private static void Copy(MainWindowGeometryState source, MainWindowGeometryState destination)
    {
        destination.RestoreBounds = source.RestoreBounds;
        destination.IsMaximized = source.IsMaximized;
        destination.AvailableWorkspaceWidth = source.AvailableWorkspaceWidth;
        destination.AvailableWorkspaceHeight = source.AvailableWorkspaceHeight;
    }
}

public static class WorkspaceLayoutSettingsAdapter
{
    public static WorkspaceLayoutState FromSettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var state = new WorkspaceLayoutState
        {
            WorkspaceMode = ParseMode(settings.WorkspaceLayoutMode),
            MainWindow =
            {
                RestoreBounds = CreateRect(settings.WindowLeft, settings.WindowTop, settings.WindowWidth, settings.WindowHeight),
                IsMaximized = settings.StartMaximized
            }
        };
        state.Explorer.IsVisible = settings.IsExplorerVisible;
        state.Explorer.RequestedWidth = ValidOr(settings.ExplorerWidth, 220, 190);
        state.Explorer.MinimumWidth = 190;

        state.Editor.IsVisible = true;
        state.Editor.MinimumWidth = 320;
        state.Editor.UsesElasticWidth = true;

        state.Console.IsVisible = true;
        state.Console.RequestedVerticalHeight = ValidOr(settings.ConsoleHeight, 180, 160);
        state.Console.MinimumHeight = 160;
        state.Console.RequestedSideBySideWidth = ValidOr(settings.ConsoleSideWidth, 420, 220);
        state.Console.MinimumSideBySideWidth = 220;

        state.Debug.IsVisible = settings.IsDebugPanelVisible;
        state.Debug.DockState = settings.IsDebugPanelVisible ? LayoutDockState.Docked : LayoutDockState.Hidden;
        state.Debug.RequestedDockedWidth = ValidOr(settings.DockedDebugPanelWidth, 220, 160);
        state.Debug.MinimumWidth = 160;
        state.Debug.FloatingBounds = CreateRect(settings.DebugPaneWindowLeft, settings.DebugPaneWindowTop, settings.DebugPaneWindowWidth, settings.DebugPaneWindowHeight);

        state.LowerTools.IsVisible = settings.IsBottomToolWindowVisible;
        state.LowerTools.DockState = settings.IsBottomToolWindowVisible
            ? settings.IsBottomToolWindowFloating ? LayoutDockState.Floating : LayoutDockState.Docked
            : LayoutDockState.Hidden;
        state.LowerTools.SelectedTab = ParseBottomTab(settings.SelectedBottomToolTab);
        state.LowerTools.RequestedDockedHeight = ValidOr(settings.DockedBottomToolWindowHeight, 180, 120);
        state.LowerTools.MinimumDockedHeight = 120;
        state.LowerTools.FloatingBounds = CreateRect(settings.BottomToolWindowLeft, settings.BottomToolWindowTop, settings.BottomToolWindowWidth, settings.BottomToolWindowHeight);
        return state;
    }

    public static void ApplyToSettings(WorkspaceLayoutState state, ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        if (state.MainWindow.RestoreBounds is Rect bounds)
        {
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }

        settings.StartMaximized = state.MainWindow.IsMaximized;
        settings.IsExplorerVisible = state.Explorer.IsVisible;
        settings.ExplorerWidth = state.Explorer.RequestedWidth;
        settings.ConsoleHeight = state.Console.RequestedVerticalHeight;
        settings.ConsoleSideWidth = state.Console.RequestedSideBySideWidth;
        settings.WorkspaceLayoutMode = state.WorkspaceMode.ToString();
        settings.IsDebugPanelVisible = state.Debug.DockState != LayoutDockState.Hidden;
        settings.DockedDebugPanelWidth = state.Debug.RequestedDockedWidth;
        settings.IsBottomToolWindowVisible = state.LowerTools.DockState != LayoutDockState.Hidden;
        settings.IsBottomToolWindowFloating = state.LowerTools.DockState == LayoutDockState.Floating;
        settings.SelectedBottomToolTab = state.LowerTools.SelectedTab.ToString();
        settings.DockedBottomToolWindowHeight = state.LowerTools.RequestedDockedHeight;
        ApplyRect(state.Debug.FloatingBounds, (left, top, width, height) =>
        {
            settings.DebugPaneWindowLeft = left;
            settings.DebugPaneWindowTop = top;
            settings.DebugPaneWindowWidth = width;
            settings.DebugPaneWindowHeight = height;
        });
        ApplyRect(state.LowerTools.FloatingBounds, (left, top, width, height) =>
        {
            settings.BottomToolWindowLeft = left;
            settings.BottomToolWindowTop = top;
            settings.BottomToolWindowWidth = width;
            settings.BottomToolWindowHeight = height;
        });
    }

    public static WorkspaceMode ParseMode(string? value)
        => Enum.TryParse<WorkspaceMode>(value, true, out var mode) ? mode : WorkspaceMode.HorizontalSplit;

    private static LayoutBottomToolTab ParseBottomTab(string? value)
        => Enum.TryParse<LayoutBottomToolTab>(value, true, out var tab) ? tab : LayoutBottomToolTab.Problems;

    private static double ValidOr(double? value, double fallback, double minimum)
        => value is double number && double.IsFinite(number) && number >= minimum ? number : fallback;

    private static Rect? CreateRect(double? left, double? top, double? width, double? height)
        => left is double l && top is double t && width is double w && height is double h &&
           double.IsFinite(l) && double.IsFinite(t) && double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0
            ? new Rect(l, t, w, h)
            : null;

    private static void ApplyRect(Rect? rect, Action<double, double, double, double> apply)
    {
        if (rect is Rect value)
        {
            apply(value.Left, value.Top, value.Width, value.Height);
        }
    }
}
