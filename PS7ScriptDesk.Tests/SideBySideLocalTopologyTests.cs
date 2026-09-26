using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Layout;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class SideBySideLocalTopologyTests
{
    [Fact]
    public void LocalTopologyFillsSideBySideHostAcrossVisibilityAndResizeStates()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: true, debugVisible: false);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);
                ApplyMode(window, "SideBySideSplit");
                SetExplorer(window, true);
                SetDebug(window, true);
                Drain(window);
                AssertLocalProjection(window, debugVisible: true);
                Assert.Equal(GridUnitType.Star, Column(window, "SideBySideConsoleColumnDefinition").Width.GridUnitType);
                Assert.Equal(GridUnitType.Star, Column(window, "SideBySideDebugColumnDefinition").Width.GridUnitType);

                var consoleWidth = Column(window, "SideBySideConsoleColumnDefinition").ActualWidth;
                var debugWidth = Column(window, "SideBySideDebugColumnDefinition").ActualWidth;
                window.Width = 1600;
                Drain(window);
                AssertLocalProjection(window, debugVisible: true);
                Assert.InRange(Column(window, "SideBySideConsoleColumnDefinition").ActualWidth, consoleWidth - 1.5, consoleWidth + 1.5);
                Assert.InRange(Column(window, "SideBySideDebugColumnDefinition").ActualWidth, debugWidth - 1.5, debugWidth + 1.5);
                Assert.True(Column(window, "SideBySideEditorColumnDefinition").ActualWidth > 400);

                SetExplorer(window, false);
                AssertLocalProjection(window, debugVisible: true);
                SetDebug(window, false);
                Drain(window);
                AssertLocalProjection(window, debugVisible: false);
                Assert.Equal(GridUnitType.Pixel, Column(window, "SideBySideConsoleColumnDefinition").Width.GridUnitType);
                SetExplorer(window, true);
                AssertLocalProjection(window, debugVisible: false);
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public void LocalTopologyReleasesDockedDebugAllocationWhenFloatingAndRestoresItOnDockBack()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: false, debugVisible: true);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);
                ApplyMode(window, "SideBySideSplit");
                SetDebug(window, true);
                Drain(window);
                var dockedWidth = Column(window, "SideBySideDebugColumnDefinition").ActualWidth;
                Invoke(window, "PopOutDebugPane", "Stage2Test");
                Drain(window);
                Assert.Equal(LayoutDockState.Floating, window.LayoutState.Debug.DockState);
                Assert.InRange(Column(window, "SideBySideDebugColumnDefinition").ActualWidth, 0, 0.5);
                Assert.InRange(Column(window, "SideBySideConsoleDebugSplitterColumnDefinition").ActualWidth, 0, 0.5);
                AssertLocalProjection(window, debugVisible: false);

                Invoke(window, "DockDebugPane", "Stage2Test");
                Drain(window);
                Assert.Equal(LayoutDockState.Docked, window.LayoutState.Debug.DockState);
                Assert.InRange(Column(window, "SideBySideDebugColumnDefinition").ActualWidth, dockedWidth - 2, dockedWidth + 2);
                AssertLocalProjection(window, debugVisible: true);
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public void LocalTopologyPreservesRequestedWidthsAcrossRepeatedModeTransitionsAndMaximizeRestore()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: false, debugVisible: true);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);
                foreach (var mode in new[] { "HorizontalSplit", "SideBySideSplit", "EditorMaximized", "SideBySideSplit", "ConsoleMaximized", "SideBySideSplit" })
                {
                    ApplyMode(window, mode);
                    if (mode == "SideBySideSplit")
                    {
                        SetDebug(window, true);
                        Drain(window);
                        AssertLocalProjection(window, debugVisible: true);
                    }
                    Drain(window);
                }

                window.WindowState = WindowState.Maximized;
                Drain(window);
                AssertLocalProjection(window, debugVisible: true);
                window.WindowState = WindowState.Normal;
                Drain(window);
                AssertLocalProjection(window, debugVisible: true);
                Assert.Equal(260, window.LayoutState.Console.RequestedSideBySideWidth);
                Assert.Equal(160, window.LayoutState.Debug.RequestedDockedWidth);
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public void LocalTopologyCapturesRepeatedNativeSplitterResultsWithoutDrift()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: false, debugVisible: true);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);
                ApplyMode(window, "SideBySideSplit");
                SetDebug(window, true);
                Drain(window);

                var debugColumn = Column(window, "SideBySideDebugColumnDefinition");
                var consoleColumn = Column(window, "SideBySideConsoleColumnDefinition");
                for (var iteration = 0; iteration < 20; iteration++)
                {
                    var delta = iteration % 2 == 0 ? 4 : -4;
                    var debugWidth = Math.Clamp(debugColumn.ActualWidth + delta, 160, 240);
                    var actualDelta = debugWidth - debugColumn.ActualWidth;
                    debugColumn.Width = new GridLength(debugWidth, GridUnitType.Pixel);
                    consoleColumn.Width = new GridLength(Math.Max(220, consoleColumn.ActualWidth - actualDelta), GridUnitType.Pixel);
                    Drain(window);
                    Invoke(window, "SideBySideDebugSplitter_DragCompleted", null, null);
                    Drain(window);

                    AssertLocalProjection(window, debugVisible: true);
                    Assert.InRange(window.LayoutState.Debug.RequestedDockedWidth, 159, 241);
                    Assert.InRange(window.LayoutState.Console.RequestedSideBySideWidth, 219, double.PositiveInfinity);
                }
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public void LocalTopologyCommitsBothNativeSplitterWidthsBeforeProjectionAndPreservesReverseDrag()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: false, debugVisible: true);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);
                ApplyMode(window, "SideBySideSplit");
                SetDebug(window, true);
                Drain(window);

                var console = Column(window, "SideBySideConsoleColumnDefinition");
                var debug = Column(window, "SideBySideDebugColumnDefinition");

                debug.Width = new GridLength(260, GridUnitType.Pixel);
                console.Width = new GridLength(220, GridUnitType.Pixel);
                Drain(window);
                Invoke(window, "SideBySideDebugSplitter_DragCompleted", null, null);
                Drain(window);

                Assert.Equal(220, window.LayoutState.Console.RequestedSideBySideWidth, 1);
                Assert.Equal(260, window.LayoutState.Debug.RequestedDockedWidth, 1);
                Assert.Equal(220, console.ActualWidth, 1);
                Assert.Equal(260, debug.ActualWidth, 1);

                debug.Width = new GridLength(180, GridUnitType.Pixel);
                console.Width = new GridLength(300, GridUnitType.Pixel);
                Drain(window);
                Invoke(window, "SideBySideDebugSplitter_DragCompleted", null, null);
                Drain(window);

                Assert.Equal(300, window.LayoutState.Console.RequestedSideBySideWidth, 1);
                Assert.Equal(180, window.LayoutState.Debug.RequestedDockedWidth, 1);
                Assert.Equal(300, console.ActualWidth, 1);
                Assert.Equal(180, debug.ActualWidth, 1);
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public void ModeRoundTripPreservesHorizontalDebugResizeAndSideBySideCommitWithoutUnderfill()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: false, debugVisible: true);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);

                // Start in Horizontal/Top-Bottom and commit a root-side Debug resize.
                ApplyMode(window, "HorizontalSplit");
                SetDebug(window, true);
                Drain(window);
                var rootDebug = Column(window, "ConsoleSideColumnDefinition");
                rootDebug.Width = new GridLength(280, GridUnitType.Pixel);
                Drain(window);
                Invoke(window, "DebugPanelSplitter_DragCompleted", null, null);
                Drain(window);
                Assert.Equal(280, window.LayoutState.Debug.RequestedDockedWidth, 1);

                // Enter SideBySide, commit the local exchange, and verify it survives.
                ApplyMode(window, "SideBySideSplit");
                SetDebug(window, true);
                Drain(window);
                var sideConsole = Column(window, "SideBySideConsoleColumnDefinition");
                var sideDebug = Column(window, "SideBySideDebugColumnDefinition");
                sideConsole.Width = new GridLength(220, GridUnitType.Pixel);
                sideDebug.Width = new GridLength(260, GridUnitType.Pixel);
                Drain(window);
                Invoke(window, "SideBySideDebugSplitter_DragCompleted", null, null);
                Drain(window);
                Assert.Equal(220, window.LayoutState.Console.RequestedSideBySideWidth, 1);
                Assert.Equal(260, window.LayoutState.Debug.RequestedDockedWidth, 1);
                Assert.Equal(0, window.LastLayoutInvariantResult!.Budget.UnusedHorizontalWidth, 1.5);

                // Return to Horizontal and prove the root topology consumes its budget.
                ApplyMode(window, "HorizontalSplit");
                Drain(window);
                Assert.Equal(260, window.LayoutState.Debug.RequestedDockedWidth, 1);
                Assert.Equal(0, window.LastLayoutInvariantResult!.Budget.UnusedHorizontalWidth, 1.5);
                Assert.Equal(GridUnitType.Pixel, rootDebug.Width.GridUnitType);
                Assert.Equal(260, rootDebug.ActualWidth, 1);
                Assert.Equal(3, Grid.GetRowSpan(Find<GridSplitter>(window, "DebugPanelSplitter")));
                Assert.Equal(3, Grid.GetRowSpan(Find<FrameworkElement>(window, "DebugPanelBorder")));
                Assert.Equal(0, Column(window, "SideBySideConsoleColumnDefinition").Width.Value, 1);
                Assert.Equal(0, Column(window, "SideBySideDebugColumnDefinition").Width.Value, 1);

                // Repeat the round trip in the opposite direction.
                ApplyMode(window, "SideBySideSplit");
                SetDebug(window, true);
                Drain(window);
                Assert.Equal(3, Grid.GetRowSpan(Find<GridSplitter>(window, "DebugPanelSplitter")));
                Assert.Equal(1, Grid.GetRowSpan(Find<GridSplitter>(window, "SideBySideDebugSplitter")));
                Assert.Equal(1, Grid.GetRowSpan(Find<FrameworkElement>(window, "DebugPanelBorder")));
                sideConsole = Column(window, "SideBySideConsoleColumnDefinition");
                sideDebug = Column(window, "SideBySideDebugColumnDefinition");
                sideConsole.Width = new GridLength(300, GridUnitType.Pixel);
                sideDebug.Width = new GridLength(180, GridUnitType.Pixel);
                Drain(window);
                Invoke(window, "SideBySideDebugSplitter_DragCompleted", null, null);
                Drain(window);
                ApplyMode(window, "HorizontalSplit");
                Drain(window);
                Assert.Equal(180, window.LayoutState.Debug.RequestedDockedWidth, 1);
                Assert.Equal(0, window.LastLayoutInvariantResult!.Budget.UnusedHorizontalWidth, 1.5);
                Assert.Equal(180, rootDebug.ActualWidth, 1);
                Assert.Equal(0, Column(window, "SideBySideConsoleColumnDefinition").Width.Value, 1);
                Assert.Equal(0, Column(window, "SideBySideDebugColumnDefinition").Width.Value, 1);
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public void LocalTopologyConsoleDebugSplitterHitTestsToNativeSplitterAndReportsDirectionalCapacity()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings(explorerVisible: false, debugVisible: true);
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                Drain(window);
                ApplyMode(window, "SideBySideSplit");
                SetDebug(window, true);
                Drain(window);

                var host = Find<Grid>(window, "SideBySideGrid");
                var splitter = Find<GridSplitter>(window, "SideBySideDebugSplitter");
                var console = Column(window, "SideBySideConsoleColumnDefinition");
                var debug = Column(window, "SideBySideDebugColumnDefinition");
                var workspace = Find<Grid>(window, "WorkspaceGrid");
                Assert.Same(host, VisualTreeHelper.GetParent(splitter));
                Assert.Equal(3, Grid.GetColumn(splitter));
                Assert.Same(console, host.ColumnDefinitions[2]);
                Assert.Same(debug, host.ColumnDefinitions[4]);
                var splitterBounds = splitter.TransformToAncestor(host).TransformBounds(new Rect(0, 0, splitter.ActualWidth, splitter.ActualHeight));
                var splitterWorkspaceBounds = splitter.TransformToAncestor(workspace).TransformBounds(new Rect(0, 0, splitter.ActualWidth, splitter.ActualHeight));
                var splitterWindowBounds = splitter.TransformToAncestor(window).TransformBounds(new Rect(0, 0, splitter.ActualWidth, splitter.ActualHeight));
                var editor = Column(window, "SideBySideEditorColumnDefinition");
                var editorSplitter = Column(window, "SideBySideEditorConsoleSplitterColumnDefinition");
                var debugSplitter = Column(window, "SideBySideConsoleDebugSplitterColumnDefinition");
                var consoleBounds = new Rect(editor.ActualWidth + editorSplitter.ActualWidth, 0, console.ActualWidth, host.ActualHeight);
                var debugBounds = new Rect(consoleBounds.Right + debugSplitter.ActualWidth, 0, debug.ActualWidth, host.ActualHeight);
                var center = new Point(splitterBounds.Left + splitterBounds.Width / 2, splitterBounds.Top + splitterBounds.Height / 2);
                var probes = new[]
                {
                    ("center", center),
                    ("left1", new Point(center.X - 1, center.Y)),
                    ("right1", new Point(center.X + 1, center.Y)),
                    ("top", new Point(center.X, splitterBounds.Top + splitterBounds.Height * .25)),
                    ("middle", new Point(center.X, splitterBounds.Top + splitterBounds.Height * .5)),
                    ("bottom", new Point(center.X, splitterBounds.Top + splitterBounds.Height * .75))
                };
                var probeResults = probes.Select(probe =>
                {
                    var hit = host.InputHitTest(probe.Item2) as DependencyObject;
                    return $"{probe.Item1}={probe.Item2.X:0.###},{probe.Item2.Y:0.###}:{Describe(hit)}:routesToSplitter={FindAncestor<GridSplitter>(hit) is not null}:parents={DescribeParentChain(hit)}";
                }).ToArray();
                var centerHit = host.InputHitTest(center) as DependencyObject;
                var evidence = $"host={host.ActualWidth:0.###}x{host.ActualHeight:0.###}; splitterHost={FormatRect(splitterBounds)}; splitterWorkspace={FormatRect(splitterWorkspaceBounds)}; splitterWindow={FormatRect(splitterWindowBounds)}; consoleHost={FormatRect(consoleBounds)}; debugHost={FormatRect(debugBounds)}; consoleActual={console.ActualWidth:0.###}; consoleMin={console.MinWidth:0.###}; debugActual={debug.ActualWidth:0.###}; debugMin={debug.MinWidth:0.###}; probes={string.Join(" | ", probeResults)}";
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "PS7ScriptDesk_Stage2_UAT_SplitterHitTest.txt"), evidence);

                Assert.NotNull(FindAncestor<GridSplitter>(centerHit));
                Assert.Equal(6, splitterBounds.Width, 1);
                Assert.Equal(260, console.ActualWidth, 1);
                Assert.Equal(220, console.MinWidth, 1);
                Assert.Equal(160, debug.ActualWidth, 1);
                Assert.Equal(160, debug.MinWidth, 1);
                Assert.True(console.ActualWidth > console.MinWidth + 1.5, evidence);
                Assert.True(debug.ActualWidth <= debug.MinWidth + 1.5, evidence);
            }
            finally
            {
                Close(window);
            }
        });
    }

    private static void AssertLocalProjection(MainWindow window, bool debugVisible)
    {
        var host = Find<Grid>(window, "SideBySideGrid");
        Assert.Equal(Visibility.Visible, host.Visibility);
        var editor = Column(window, "SideBySideEditorColumnDefinition");
        var editorSplitter = Column(window, "SideBySideEditorConsoleSplitterColumnDefinition");
        var console = Column(window, "SideBySideConsoleColumnDefinition");
        var debugSplitter = Column(window, "SideBySideConsoleDebugSplitterColumnDefinition");
        var debug = Column(window, "SideBySideDebugColumnDefinition");
        var activeField = typeof(MainWindow).GetField("_sideBySideLocalProjectionActive", BindingFlags.Instance | BindingFlags.NonPublic);
        var projectionActive = activeField?.GetValue(window);
        var debugBorder = Find<FrameworkElement>(window, "DebugPanelBorder");
        var state = window.LayoutState.Debug.DockState;
        var activeWidth = editor.ActualWidth + editorSplitter.ActualWidth + console.ActualWidth + debugSplitter.ActualWidth + debug.ActualWidth;
        Assert.True(Math.Abs(activeWidth - host.ActualWidth) <= 1.5, $"local={activeWidth}, host={host.ActualWidth}, projectionActive={projectionActive}, state={state}, debugVisibility={debugBorder.Visibility}");
        Assert.Equal(GridUnitType.Star, editor.Width.GridUnitType);
        Assert.InRange(console.ActualWidth, 219, double.PositiveInfinity);
        if (debugVisible)
        {
            Assert.True(debug.ActualWidth >= 159, $"debugWidth={debug.ActualWidth}, debugSplitter={debugSplitter.ActualWidth}, projectionActive={projectionActive}, state={state}, debugVisibility={debugBorder.Visibility}");
            Assert.InRange(debugSplitter.ActualWidth, 5, 7);
        }
        else
        {
            Assert.True(debug.ActualWidth <= 0.5, $"debugWidth={debug.ActualWidth}, debugSplitter={debugSplitter.ActualWidth}, projectionActive={projectionActive}, state={state}, debugVisibility={debugBorder.Visibility}");
            Assert.InRange(debugSplitter.ActualWidth, 0, 0.5);
        }

        Assert.True(window.LastLayoutInvariantResult is not null);
        Assert.InRange(window.LastLayoutInvariantResult!.Budget.UnusedHorizontalWidth, 0, 1.5);
    }

    private static ApplicationSettings CreateSettings(bool explorerVisible, bool debugVisible)
        => new()
        {
            WindowWidth = 1200,
            WindowHeight = 760,
            IsExplorerVisible = explorerVisible,
            IsDebugPanelVisible = debugVisible,
            IsBottomToolWindowVisible = false,
            WorkspaceLayoutMode = "HorizontalSplit",
            ConsoleSideWidth = 260,
            DockedDebugPanelWidth = 160,
            IsDeveloperDiagnosticsEnabled = false
        };

    private static void ApplyMode(MainWindow window, string modeName)
    {
        var modeType = typeof(MainWindow).GetNestedType("WorkspaceLayoutMode", BindingFlags.NonPublic);
        Assert.NotNull(modeType);
        Invoke(window, "ApplyWorkspaceLayoutMode", Enum.Parse(modeType!, modeName), "Stage2TopologyTest");
    }

    private static void SetDebug(MainWindow window, bool visible)
        => Invoke(window, "SetDebugPanelVisible", visible);

    private static void SetExplorer(MainWindow window, bool visible)
    {
        var viewModel = RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetField("_isExplorerVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, visible);
        var openTabs = typeof(MainWindowViewModel).GetField("<OpenTabs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(openTabs);
        openTabs!.SetValue(viewModel, Activator.CreateInstance(openTabs.FieldType));
        typeof(MainWindow).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, viewModel);
        Find<FrameworkElement>(window, "ExplorerPaneBorder").Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Invoke(window, "ApplyExplorerVisibilityLayout");
        Drain(window);
    }

    private static ColumnDefinition Column(MainWindow window, string name)
        => Find<ColumnDefinition>(window, name);

    private static string Describe(DependencyObject? value)
        => value is FrameworkElement element
            ? $"{element.GetType().Name}({element.Name})"
            : value?.GetType().Name ?? "null";

    private static string DescribeParentChain(DependencyObject? value)
    {
        var names = new List<string>();
        for (var current = value; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            names.Add(Describe(current));
        }

        return string.Join(" -> ", names);
    }

    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject
    {
        for (var current = value; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string FormatRect(Rect value)
        => $"{value.Left:0.###},{value.Top:0.###},{value.Width:0.###},{value.Height:0.###}";

    private static T Find<T>(MainWindow window, string name) where T : class
        => Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void Invoke(MainWindow window, string methodName, params object?[] args)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, args);
    }

    private static void Drain(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(window.UpdateLayout));
        window.UpdateLayout();
    }

    private static void Close(MainWindow window)
    {
        typeof(MainWindow).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, null);
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static App EnsureShellApplication()
    {
        if (System.Windows.Application.Current is App existingApp)
        {
            return existingApp;
        }

        var app = new App();
        app.InitializeComponent();
        return app;
    }

    private sealed class InMemorySettingsService(ApplicationSettings settings) : IApplicationSettingsService
    {
        public string SettingsFilePath => "stage2-local-topology.settings.json";
        public ApplicationSettings LoadSettings() => settings;
        public void SaveSettings(ApplicationSettings settings) { }
    }
}
