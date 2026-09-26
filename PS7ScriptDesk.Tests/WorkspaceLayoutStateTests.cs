using System.Windows;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Layout;

namespace PS7ScriptDesk.Tests;

public sealed class WorkspaceLayoutStateTests
{
    [Fact]
    public void SettingsAdapterPreservesIndependentConsoleAndDebugIntents()
    {
        var settings = new ApplicationSettings
        {
            WorkspaceLayoutMode = "SideBySideSplit",
            ConsoleSideWidth = 340,
            DockedDebugPanelWidth = 275,
            IsDebugPanelVisible = true,
            IsBottomToolWindowVisible = false
        };

        var state = WorkspaceLayoutSettingsAdapter.FromSettings(settings);

        Assert.Equal(WorkspaceMode.SideBySideSplit, state.WorkspaceMode);
        Assert.Equal(340, state.Console.RequestedSideBySideWidth);
        Assert.Equal(275, state.Debug.RequestedDockedWidth);
        Assert.Equal(LayoutDockState.Docked, state.Debug.DockState);
        Assert.Equal(LayoutDockState.Hidden, state.LowerTools.DockState);
    }

    [Fact]
    public void SettingsAdapterRoundTripsConceptualDockAndWindowState()
    {
        var state = WorkspaceLayoutSettingsAdapter.FromSettings(new ApplicationSettings
        {
            WorkspaceLayoutMode = "ConsoleMaximized",
            WindowLeft = 20,
            WindowTop = 30,
            WindowWidth = 1200,
            WindowHeight = 800,
            IsBottomToolWindowVisible = true,
            IsBottomToolWindowFloating = true,
            SelectedBottomToolTab = "Activity"
        });
        state.Debug.DockState = LayoutDockState.Floating;
        state.Debug.FloatingBounds = new Rect(40, 50, 420, 300);

        var settings = new ApplicationSettings();
        WorkspaceLayoutSettingsAdapter.ApplyToSettings(state, settings);

        Assert.Equal("ConsoleMaximized", settings.WorkspaceLayoutMode);
        Assert.True(settings.IsDebugPanelVisible);
        Assert.True(settings.IsBottomToolWindowVisible);
        Assert.True(settings.IsBottomToolWindowFloating);
        Assert.Equal("Activity", settings.SelectedBottomToolTab);
        Assert.Equal(420, settings.DebugPaneWindowWidth);
        Assert.Equal(300, settings.DebugPaneWindowHeight);
    }

    [Fact]
    public void CoordinatorReportsOverflowAndInactiveGeometryAsInvariantViolations()
    {
        var state = WorkspaceLayoutSettingsAdapter.FromSettings(new ApplicationSettings());
        var coordinator = new WorkspaceLayoutCoordinator(state);
        var result = coordinator.Validate(new LayoutGeometrySnapshot(
            WorkspaceWidth: 1000,
            WorkspaceHeight: 700,
            ActiveHorizontalWidth: 1040,
            ActiveVerticalHeight: 680,
            ExplorerWidth: 220,
            EditorWidth: 320,
            ConsoleWidth: 260,
            DebugWidth: 160,
            LowerToolHeight: 0,
            InactiveColumnWidth: 12,
            InactiveRowHeight: 8,
            ExplorerVisible: true,
            DebugVisible: false,
            LowerToolsVisible: false,
            DebugFloating: false,
            LowerToolsFloating: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, violation => violation.Code == "LayoutOverflow");
        Assert.Contains(result.Violations, violation => violation.Code == "InactiveColumnGeometry");
        Assert.Contains(result.Violations, violation => violation.Code == "InactiveRowGeometry");
    }
}
