using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Layout;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class KnownBadLayoutCharacterizationTests
{
    private static readonly string[] ColumnNames =
    {
        "ExplorerColumnDefinition",
        "ExplorerSplitterColumnDefinition",
        "EditorColumnDefinition",
        "ConsoleSideSplitterColumnDefinition",
        "ConsoleSideColumnDefinition",
        "DebugPanelSplitterColumn",
        "DebugPanelColumn",
        "SideBySideEditorColumnDefinition",
        "SideBySideEditorConsoleSplitterColumnDefinition",
        "SideBySideConsoleColumnDefinition",
        "SideBySideConsoleDebugSplitterColumnDefinition",
        "SideBySideDebugColumnDefinition"
    };

    private static readonly string[] RowNames =
    {
        "EditorRowDefinition",
        "EditorConsoleRowSplitterDefinition",
        "ConsoleRowDefinition",
        "BottomToolWindowSplitterRowDefinition",
        "BottomToolWindowRowDefinition"
    };

    private static readonly string[] PaneNames =
    {
        "ExplorerPaneBorder",
        "EditorPaneBorder",
        "ConsolePaneBorder",
        "DebugPanelBorder",
        "BottomToolWindowBorder",
        "ConsoleBottomPane",
        "TerminalConsole"
    };

    [Fact]
    public void KnownBadStatesProduceRepeatableConceptualAndLiveGeometryEvidence()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var output = new StringBuilder();
            var settings = CreateSettings(workspaceMode: "SideBySideSplit", explorerVisible: false, debugVisible: true);

            CaptureCase(output, "A1", settings, window =>
            {
                ApplyMode(window, "SideBySideSplit");
                SetDebugVisible(window, true);
            });
            CaptureCase(output, "A2", CreateSettings("SideBySideSplit", explorerVisible: true, debugVisible: true), window =>
            {
                ApplyMode(window, "SideBySideSplit");
                SetDebugVisible(window, true);
            });
            CaptureCase(output, "A3", CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: false), window =>
            {
                ApplyMode(window, "SideBySideSplit");
                SetDebugVisible(window, false);
            });
            var largeSideBySide = CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: true);
            largeSideBySide.WindowWidth = 2552;
            largeSideBySide.WindowHeight = 900;
            largeSideBySide.ConsoleSideWidth = 260;
            largeSideBySide.DockedDebugPanelWidth = 160;
            CaptureCase(output, "A1_2552", largeSideBySide, window =>
            {
                ApplyMode(window, "SideBySideSplit");
                SetDebugVisible(window, true);
            });
            CaptureCase(output, "B1", CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: false), _ => { });
            CaptureCase(output, "B2", CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: false), window =>
            {
                window.Width += 40;
                DrainLayout(window);
            });
            CaptureStartupTimeline(output);
            CaptureCase(output, "C1", CreateSettings("HorizontalSplit", explorerVisible: false, debugVisible: false), window =>
            {
                ApplyMode(window, "HorizontalSplit");
                SetDebugVisible(window, false);
            });
            CaptureCase(output, "C2", CreateSettings("HorizontalSplit", explorerVisible: false, debugVisible: true), window =>
            {
                ApplyMode(window, "HorizontalSplit");
                SetDebugVisible(window, true);
            });

            CaptureSaveReopen(output);
            WriteArtifact(output.ToString());
        });
    }

    [Fact]
    public void Stage2CapturedSideBySideWidthsEliminateUnderfillAgainstConceptualState()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: true);
            settings.WindowWidth = 2552;
            settings.WindowHeight = 900;
            settings.ConsoleSideWidth = 260;
            settings.DockedDebugPanelWidth = 160;

            var window = new MainWindow(new RecordingSettingsService(settings), settings);
            try
            {
                window.Show();
                DrainLayout(window);
                SetTestExplorerVisibility(window, settings.IsExplorerVisible);
                ApplyMode(window, "SideBySideSplit");
                SetDebugVisible(window, true);
                DrainLayout(window);

                var snapshot = CaptureSnapshot(window, "KnownBadSideBySide2552");
                Assert.InRange(snapshot.UnusedHorizontal, -1.5, 1.5);
                Assert.InRange(Find<ColumnDefinition>(window, "SideBySideEditorColumnDefinition").ActualWidth, 2080, 2110);
                InvokePrivate(window, "ValidateLayoutState", "KnownBadSideBySide2552");
                Assert.DoesNotContain(window.LastLayoutInvariantResult!.Violations, violation => violation.Code == "LayoutUnderfill");
                var artifact = new StringBuilder("CASE KnownBadSideBySide2552_CAPTURED_WIDTHS\n");
                AppendSnapshot(artifact, snapshot);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "PS7ScriptDesk_Stage1_5_KnownBadSideBySide2552.txt"), artifact.ToString());
            }
            finally
            {
                CloseWindow(window);
            }
        });
    }

    private static void CaptureCase(StringBuilder output, string id, ApplicationSettings settings, Action<MainWindow> arrange)
    {
        var window = new MainWindow(new RecordingSettingsService(settings), settings);
        try
        {
            window.Show();
            DrainLayout(window);
            SetTestExplorerVisibility(window, settings.IsExplorerVisible);
            output.AppendLine($"CASE {id} S0_AFTER_SHOW");
            AppendSnapshot(output, CaptureSnapshot(window, $"{id}.S0"));
            arrange(window);
            DrainLayout(window);
            InvokePrivate(window, "ValidateLayoutState", $"Characterization:{id}");
            output.AppendLine($"CASE {id} S7_FINAL_UPDATE_LAYOUT");
            AppendSnapshot(output, CaptureSnapshot(window, $"{id}.S7"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static void CaptureSaveReopen(StringBuilder output)
    {
        var settings = CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: false);
        var service = new RecordingSettingsService(settings);
        var first = new MainWindow(service, settings);
        try
        {
            first.Show();
            DrainLayout(first);
            SetTestExplorerVisibility(first, settings.IsExplorerVisible);
            ApplyMode(first, "SideBySideSplit");
            SetDebugVisible(first, false);
            DrainLayout(first);
            typeof(MainWindow).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(first, null);
            InvokePrivate(first, "SaveApplicationSettings");
            output.AppendLine("CASE B2_B3_SAVE_REOPEN SAVED_SETTINGS");
            AppendSettings(output, service.Saved!);
        }
        finally
        {
            CloseWindow(first);
        }

        var reopenedSettings = service.Saved ?? settings;
        var reopened = new MainWindow(service, reopenedSettings);
        try
        {
            reopened.Show();
            DrainLayout(reopened);
            SetTestExplorerVisibility(reopened, reopenedSettings.IsExplorerVisible);
            output.AppendLine("CASE B3_AFTER_REOPEN");
            AppendSnapshot(output, CaptureSnapshot(reopened, "B3.Reopened"));
        }
        finally
        {
            CloseWindow(reopened);
        }
    }

    private static void CaptureStartupTimeline(StringBuilder output)
    {
        var settings = CreateSettings("SideBySideSplit", explorerVisible: false, debugVisible: false);
        var adapterState = WorkspaceLayoutSettingsAdapter.FromSettings(settings);
        output.AppendLine($"STARTUP S0_AFTER_SETTINGS_ADAPTER mode={adapterState.WorkspaceMode} explorerVisible={adapterState.Explorer.IsVisible} consoleHeight={adapterState.Console.RequestedVerticalHeight:0.###} consoleSideWidth={adapterState.Console.RequestedSideBySideWidth:0.###} debugDock={adapterState.Debug.DockState} lowerDock={adapterState.LowerTools.DockState}");
        var window = new MainWindow(new RecordingSettingsService(settings), settings);
        try
        {
            window.Show();
            DrainLayout(window);
            SetTestExplorerVisibility(window, settings.IsExplorerVisible);
            ApplyMode(window, "SideBySideSplit");
            output.AppendLine("STARTUP S1_AFTER_WORKSPACE_MODE");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S1"));
            InvokePrivate(window, "ApplyExplorerVisibilityLayout");
            output.AppendLine("STARTUP S2_AFTER_EXPLORER");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S2"));
            SetDebugVisible(window, false);
            output.AppendLine("STARTUP S3_AFTER_DEBUG");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S3"));
            InvokePrivate(window, "RestoreBottomToolWindowFromSettings");
            output.AppendLine("STARTUP S4_AFTER_LOWER_TOOLS");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S4"));
            InvokePrivate(window, "NormalizeLayoutBudget", "SettingsRestore");
            output.AppendLine("STARTUP S5_AFTER_NORMALIZATION");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S5"));
            window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(window.UpdateLayout));
            output.AppendLine("STARTUP S6_AFTER_DEFERRED_LOADED");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S6"));
            DrainLayout(window);
            output.AppendLine("STARTUP S7_AFTER_FINAL_UPDATE_LAYOUT");
            AppendSnapshot(output, CaptureSnapshot(window, "B1.S7_TIMELINE"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static LayoutSnapshot CaptureSnapshot(MainWindow window, string id)
    {
        var workspace = Find<Grid>(window, "WorkspaceGrid");
        var columns = ColumnNames.Select(name => (Name: name, Definition: Find<ColumnDefinition>(window, name))).ToArray();
        var rows = RowNames.Select(name => (Name: name, Definition: Find<RowDefinition>(window, name))).ToArray();
        var panes = PaneNames.Select(name => (Name: name, Element: Find<FrameworkElement>(window, name))).ToArray();
        var activeHorizontal = columns[0].Definition.ActualWidth + columns[1].Definition.ActualWidth + columns[2].Definition.ActualWidth;
        if (window.LayoutState.WorkspaceMode == WorkspaceMode.SideBySideSplit)
        {
            activeHorizontal += columns[3].Definition.ActualWidth + columns[4].Definition.ActualWidth + columns[5].Definition.ActualWidth + columns[6].Definition.ActualWidth;
        }
        else if (window.LayoutState.Debug.DockState == LayoutDockState.Docked)
        {
            activeHorizontal += columns[3].Definition.ActualWidth + columns[4].Definition.ActualWidth;
        }
        var activeVertical = rows
            .Where(item => item.Name is "EditorRowDefinition" or "EditorConsoleRowSplitterDefinition" or "ConsoleRowDefinition")
            .Sum(item => item.Definition.ActualHeight);

        var invariant = window.LastLayoutInvariantResult;
        return new LayoutSnapshot(
            id,
            window,
            window.LayoutState.Clone(),
            window.ActualWidth,
            window.ActualHeight,
            window.WindowState,
            workspace.ActualWidth,
            workspace.ActualHeight,
            activeHorizontal,
            activeVertical,
            workspace.ActualWidth - activeHorizontal,
            activeHorizontal - workspace.ActualWidth,
            workspace.ActualHeight - activeVertical,
            activeVertical - workspace.ActualHeight,
            columns,
            rows,
            panes,
            invariant);
    }

    private static void AppendSnapshot(StringBuilder output, LayoutSnapshot snapshot)
    {
        output.AppendLine($"{snapshot.Id} WINDOW actual={snapshot.WindowWidth:0.###}x{snapshot.WindowHeight:0.###} state={snapshot.WindowState} workspace={snapshot.WorkspaceWidth:0.###}x{snapshot.WorkspaceHeight:0.###}");
        output.AppendLine($"CONCEPTUAL mode={snapshot.State.WorkspaceMode} explorerVisible={snapshot.State.Explorer.IsVisible} explorerWidth={snapshot.State.Explorer.RequestedWidth:0.###} editorVisible={snapshot.State.Editor.IsVisible} editorElastic={snapshot.State.Editor.UsesElasticWidth} consoleVisible={snapshot.State.Console.IsVisible} consoleHeight={snapshot.State.Console.RequestedVerticalHeight:0.###} consoleSideWidth={snapshot.State.Console.RequestedSideBySideWidth:0.###} debugDock={snapshot.State.Debug.DockState} debugWidth={snapshot.State.Debug.RequestedDockedWidth:0.###} lowerDock={snapshot.State.LowerTools.DockState} lowerHeight={snapshot.State.LowerTools.RequestedDockedHeight:0.###} restore={FormatRect(snapshot.State.MainWindow.RestoreBounds)} available={snapshot.State.MainWindow.AvailableWorkspaceWidth:0.###}x{snapshot.State.MainWindow.AvailableWorkspaceHeight:0.###} regions={snapshot.State.ActiveEditorRegion}/{snapshot.State.ActiveConsoleRegion}/{snapshot.State.ActiveDebugRegion}/{snapshot.State.ActiveLowerToolRegion}");
        output.AppendLine($"BUDGET activeHorizontal={snapshot.ActiveHorizontal:0.###} unusedHorizontal={snapshot.UnusedHorizontal:0.###} overflowHorizontal={snapshot.OverflowHorizontal:0.###} activeVertical={snapshot.ActiveVertical:0.###} unusedVertical={snapshot.UnusedVertical:0.###} overflowVertical={snapshot.OverflowVertical:0.###} classification={snapshot.Classification} invariant={FormatInvariant(snapshot.Invariant)}");
        foreach (var column in snapshot.Columns)
        {
            output.AppendLine($"COLUMN {column.Name} type={column.Definition.Width.GridUnitType} value={column.Definition.Width.Value:0.###} min={column.Definition.MinWidth:0.###} actual={column.Definition.ActualWidth:0.###}");
        }
        foreach (var row in snapshot.Rows)
        {
            output.AppendLine($"ROW {row.Name} type={row.Definition.Height.GridUnitType} value={row.Definition.Height.Value:0.###} min={row.Definition.MinHeight:0.###} actual={row.Definition.ActualHeight:0.###}");
        }
        foreach (var pane in snapshot.Panes)
        {
            output.AppendLine($"PANE {pane.Name} visibility={pane.Element.Visibility} actual={pane.Element.ActualWidth:0.###}x{pane.Element.ActualHeight:0.###} bounds={FormatBounds(pane.Element, snapshot)}");
        }
    }

    private static void AppendSettings(StringBuilder output, ApplicationSettings settings)
    {
        output.AppendLine($"SETTINGS mode={settings.WorkspaceLayoutMode} explorerVisible={settings.IsExplorerVisible} explorerWidth={settings.ExplorerWidth} consoleHeight={settings.ConsoleHeight} consoleSideWidth={settings.ConsoleSideWidth} debugVisible={settings.IsDebugPanelVisible} debugWidth={settings.DockedDebugPanelWidth} lowerVisible={settings.IsBottomToolWindowVisible} lowerFloating={settings.IsBottomToolWindowFloating} lowerHeight={settings.DockedBottomToolWindowHeight}");
    }

    private static string FormatInvariant(LayoutInvariantResult? invariant)
        => invariant is null ? "none" : string.Join(",", invariant.Violations.Select(violation => violation.Code));

    private static string FormatRect(Rect? rect)
        => rect is Rect value ? $"{value.Left:0.###},{value.Top:0.###},{value.Width:0.###},{value.Height:0.###}" : "none";

    private static string FormatBounds(FrameworkElement element, LayoutSnapshot snapshot)
    {
        if (!element.IsVisible)
        {
            return "hidden";
        }

        try
        {
            var bounds = element.TransformToAncestor(snapshot.Window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return $"{bounds.Left:0.###},{bounds.Top:0.###},{bounds.Width:0.###},{bounds.Height:0.###}";
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }

    private static void WriteArtifact(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk_Stage1_5_KnownBadLayoutCharacterization.txt");
        File.WriteAllText(path, content);
    }

    private static ApplicationSettings CreateSettings(string workspaceMode, bool explorerVisible, bool debugVisible)
        => new()
        {
            WindowWidth = 1200,
            WindowHeight = 760,
            IsExplorerVisible = explorerVisible,
            IsDebugPanelVisible = debugVisible,
            IsBottomToolWindowVisible = false,
            IsBottomToolWindowFloating = false,
            WorkspaceLayoutMode = workspaceMode,
            ConsoleHeight = 180,
            ConsoleSideWidth = 260,
            DockedDebugPanelWidth = 160,
            DockedBottomToolWindowHeight = 180,
            IsDeveloperDiagnosticsEnabled = false
        };

    private static void ApplyMode(MainWindow window, string modeName)
    {
        var modeType = typeof(MainWindow).GetNestedType("WorkspaceLayoutMode", BindingFlags.NonPublic);
        Assert.NotNull(modeType);
        InvokePrivate(window, "ApplyWorkspaceLayoutMode", Enum.Parse(modeType!, modeName), "KnownBadCharacterization");
    }

    private static void SetDebugVisible(MainWindow window, bool visible)
        => InvokePrivate(window, "SetDebugPanelVisible", visible);

    private static void SetTestExplorerVisibility(MainWindow window, bool visible)
    {
        var viewModel = (dynamic)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetField("_isExplorerVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, visible);
        var openTabsField = typeof(MainWindowViewModel).GetField("<OpenTabs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(openTabsField);
        openTabsField!.SetValue(viewModel, Activator.CreateInstance(openTabsField.FieldType));
        var field = typeof(MainWindow).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(window, viewModel);
        Find<FrameworkElement>(window, "ExplorerPaneBorder").Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InvokePrivate(window, "ApplyExplorerVisibilityLayout");
        DrainLayout(window);
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, arguments);
    }

    private static T Find<T>(MainWindow window, string name) where T : class
        => Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void DrainLayout(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(window.UpdateLayout));
        window.UpdateLayout();
    }

    private static void CloseWindow(MainWindow window)
    {
        if (window.IsVisible)
        {
            typeof(MainWindow).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, null);
            window.Close();
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
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

    private sealed class RecordingSettingsService(ApplicationSettings settings) : IApplicationSettingsService
    {
        public string SettingsFilePath => "known-bad-layout-characterization.settings.json";

        public ApplicationSettings? Saved { get; private set; }

        public ApplicationSettings LoadSettings() => Saved ?? settings;

        public void SaveSettings(ApplicationSettings settings)
        {
            Saved = settings;
        }
    }

    private sealed record LayoutSnapshot(
        string Id,
        MainWindow Window,
        WorkspaceLayoutState State,
        double WindowWidth,
        double WindowHeight,
        WindowState WindowState,
        double WorkspaceWidth,
        double WorkspaceHeight,
        double ActiveHorizontal,
        double ActiveVertical,
        double UnusedHorizontal,
        double OverflowHorizontal,
        double UnusedVertical,
        double OverflowVertical,
        IReadOnlyList<(string Name, ColumnDefinition Definition)> Columns,
        IReadOnlyList<(string Name, RowDefinition Definition)> Rows,
        IReadOnlyList<(string Name, FrameworkElement Element)> Panes,
        LayoutInvariantResult? Invariant)
    {
        public string Classification
            => OverflowHorizontal > 1 || OverflowVertical > 1
                ? UnusedHorizontal > 1 || UnusedVertical > 1 ? "MIXED/INCONSISTENT" : "OVERFLOW"
                : UnusedHorizontal > 1 || UnusedVertical > 1 ? "UNDERFILL" : "HEALTHY";

        public override string ToString()
            => $"workspace={WorkspaceWidth:0.###} active={ActiveHorizontal:0.###} unused={UnusedHorizontal:0.###} overflow={OverflowHorizontal:0.###}";
    }
}
