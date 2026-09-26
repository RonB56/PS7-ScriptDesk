using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Layout;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class MainWindowLayoutStateIntegrationTests
{
    [Fact]
    public void RealMainWindowHarnessRestoresConceptualStateAcrossModesAndVisibilityCombinations()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var settings = new ApplicationSettings
            {
                WindowWidth = 1200,
                WindowHeight = 760,
                IsExplorerVisible = true,
                IsDebugPanelVisible = false,
                IsBottomToolWindowVisible = false,
                WorkspaceLayoutMode = "SideBySideSplit",
                ConsoleSideWidth = 340,
                IsDeveloperDiagnosticsEnabled = false
            };
            var window = new MainWindow(new InMemorySettingsService(settings), settings);
            try
            {
                window.Show();
                DrainLayout(window);

                Assert.Equal(WorkspaceMode.SideBySideSplit, window.LayoutState.WorkspaceMode);
                Assert.True(window.LayoutState.Explorer.IsVisible);
                Assert.Equal(340, window.LayoutState.Console.RequestedSideBySideWidth);
                Assert.Equal(LayoutDockState.Hidden, window.LayoutState.Debug.DockState);
                Assert.Equal(LayoutDockState.Hidden, window.LayoutState.LowerTools.DockState);
                AssertNoInvalidDimensions(window.LayoutState);

                ApplyMode(window, "ConsoleMaximized");
                DrainLayout(window);
                Assert.Equal(WorkspaceMode.ConsoleMaximized, window.LayoutState.WorkspaceMode);

                ApplyMode(window, "EditorMaximized");
                DrainLayout(window);
                Assert.Equal(WorkspaceMode.EditorMaximized, window.LayoutState.WorkspaceMode);

                ApplyMode(window, "SideBySideSplit");
                InvokePrivate(window, "SetDebugPanelVisible", true);
                DrainLayout(window);
                Assert.Equal(WorkspaceMode.SideBySideSplit, window.LayoutState.WorkspaceMode);
                Assert.Equal(LayoutDockState.Docked, window.LayoutState.Debug.DockState);
                Assert.True(window.LastLayoutInvariantResult is not null);
                AssertNoInvalidDimensions(window.LayoutState);
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

    private static void AssertNoInvalidDimensions(WorkspaceLayoutState state)
    {
        Assert.True(double.IsFinite(state.Explorer.RequestedWidth));
        Assert.True(double.IsFinite(state.Console.RequestedVerticalHeight));
        Assert.True(double.IsFinite(state.Console.RequestedSideBySideWidth));
        Assert.True(double.IsFinite(state.Debug.RequestedDockedWidth));
        Assert.True(double.IsFinite(state.LowerTools.RequestedDockedHeight));
    }

    private static void ApplyMode(MainWindow window, string modeName)
    {
        var modeType = typeof(MainWindow).GetNestedType("WorkspaceLayoutMode", BindingFlags.NonPublic);
        Assert.NotNull(modeType);
        InvokePrivate(window, "ApplyWorkspaceLayoutMode", Enum.Parse(modeType!, modeName), "LayoutStateIntegration");
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, arguments);
    }

    private static void DrainLayout(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(window.UpdateLayout));
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

    private sealed class InMemorySettingsService(ApplicationSettings settings) : IApplicationSettingsService
    {
        public string SettingsFilePath => "layout-state-integration.settings.json";

        public ApplicationSettings LoadSettings() => settings;

        public void SaveSettings(ApplicationSettings settings)
        {
        }
    }
}
