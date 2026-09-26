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
                window.Show();
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

        var workspaceBounds = workspace.TransformToAncestor(window).TransformBounds(new Rect(0, 0, workspace.ActualWidth, workspace.ActualHeight));
        var paneBounds = pane.TransformToAncestor(window).TransformBounds(new Rect(0, 0, pane.ActualWidth, pane.ActualHeight));

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

    private sealed class InMemorySettingsService : IApplicationSettingsService
    {
        public string SettingsFilePath => "runtime-harness.settings.json";

        public ApplicationSettings LoadSettings() => CreateSettings();

        public void SaveSettings(ApplicationSettings settings)
        {
        }
    }
}
