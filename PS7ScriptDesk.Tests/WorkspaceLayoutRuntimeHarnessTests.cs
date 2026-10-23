using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class WorkspaceLayoutRuntimeHarnessTests
{
    [Fact]
    public void SupportedWorkspaceModesArrangeNamedPanesInsideWorkspaceBounds()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var window = new MainWindow(new InMemorySettingsService(), CreateSettings());
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Width = 1600;
                window.Height = 1000;
                window.Left = 0;
                window.Top = 0;
                window.ShowActivated = true;
                window.Show();
                window.Activate();
                DrainLayout(window);

                foreach (var mode in new[] { "Default", "HorizontalSplit", "SideBySideSplit", "EditorMaximized", "ConsoleMaximized" })
                {
                    ApplyMode(window, mode);
                    if (mode == "SideBySideSplit")
                    {
                        InvokePrivate(window, "SetDebugPanelVisible", true);
                    }

                    DrainLayout(window);
                    AssertPaneInsideWorkspace(window, "EditorPaneBorder", mode);
                    AssertPaneInsideWorkspace(window, "ConsolePaneBorder", mode);

                    if (mode == "SideBySideSplit")
                    {
                        AssertPaneInsideWorkspace(window, "SideBySideDebugSplitter", mode);
                        AssertPaneInsideWorkspace(window, "DebugPanelBorder", mode);
                        Assert.InRange(GetActualWidth(window, "SideBySideEditorColumnDefinition"), 319.0, double.PositiveInfinity);
                        Assert.InRange(GetActualWidth(window, "SideBySideConsoleColumnDefinition"), 259.0, double.PositiveInfinity);
                        Assert.InRange(GetActualWidth(window, "SideBySideDebugColumnDefinition"), 159.0, double.PositiveInfinity);
                        Assert.InRange(window.LayoutState.MainWindow.AvailableWorkspaceWidth, 1.0, double.PositiveInfinity);
                        Assert.True(window.LastLayoutInvariantResult?.Budget.UnusedHorizontalWidth < 1.5);
                    }
                }
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        });
    }

    private static ApplicationSettings CreateSettings()
        => new()
        {
            IsExplorerVisible = true,
            IsDebugPanelVisible = false,
            IsBottomToolWindowVisible = false,
            IsBottomToolWindowFloating = false,
            WorkspaceLayoutMode = "HorizontalSplit",
            IsDeveloperDiagnosticsEnabled = false
        };

    private static void ApplyMode(MainWindow window, string modeName)
    {
        var modeType = typeof(MainWindow).GetNestedType("WorkspaceLayoutMode", BindingFlags.NonPublic);
        Assert.NotNull(modeType);
        var mode = Enum.Parse(modeType!, modeName);
        InvokePrivate(window, "ApplyWorkspaceLayoutMode", mode, "RuntimeHarness");
    }

    private static double GetActualWidth(MainWindow window, string name)
        => Assert.IsType<ColumnDefinition>(window.FindName(name)).ActualWidth;

    private static void AssertPaneInsideWorkspace(MainWindow window, string name, string mode)
    {
        var workspace = Assert.IsType<Grid>(window.FindName("WorkspaceGrid"));
        var pane = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
        if (pane.Visibility != Visibility.Visible)
        {
            Assert.InRange(pane.ActualWidth, 0, 0.5);
            Assert.InRange(pane.ActualHeight, 0, 0.5);
            return;
        }

        var paneOrigin = pane.TranslatePoint(new Point(0, 0), workspace);
        var workspaceBounds = new Rect(new Point(0, 0), new Size(workspace.ActualWidth, workspace.ActualHeight));
        var paneBounds = new Rect(paneOrigin, new Size(pane.ActualWidth, pane.ActualHeight));

        Assert.True(paneBounds.Left >= workspaceBounds.Left - 1, $"{name} extends left of WorkspaceGrid.");
        Assert.True(paneBounds.Top >= workspaceBounds.Top - 1, $"{name} extends above WorkspaceGrid.");
        Assert.True(paneBounds.Right <= workspaceBounds.Right + 1, $"{name} extends right of WorkspaceGrid in {mode}; workspace={workspaceBounds}, pane={paneBounds}.");
        Assert.True(paneBounds.Bottom <= workspaceBounds.Bottom + 1, $"{name} extends below WorkspaceGrid in {mode}; workspace={workspaceBounds}, pane={paneBounds}.");
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, arguments);
    }

    private static void DrainLayout(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(window.UpdateLayout));
        window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(window.UpdateLayout));
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    }

    private static void RunOnStaThread(Action action)
        => WpfTestDispatcher.Run(action);

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

    private sealed class InMemorySettingsService : IApplicationSettingsService
    {
        public string SettingsFilePath => "runtime-harness.settings.json";

        public ApplicationSettings LoadSettings() => CreateSettings();

        public void SaveSettings(ApplicationSettings settings)
        {
        }
    }
}
