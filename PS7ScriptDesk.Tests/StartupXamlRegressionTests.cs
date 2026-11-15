using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class StartupXamlRegressionTests
{
    [Fact]
    public void MainWindowInitializesWithNativeToolbarOverflowAndBorderlessEditorConsolePanes()
    {
        var mainWindowXaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        Assert.DoesNotContain("ToolBar.OverflowButtonStyleKey", mainWindowXaml, StringComparison.Ordinal);

        WpfTestDispatcher.Run(() =>
        {
            var application = EnsureShellApplication();
            var settings = new ApplicationSettings { IsDeveloperDiagnosticsEnabled = false };
            var window = new MainWindow(new InMemorySettingsService(settings), settings);

            try
            {
                var toolbar = Assert.IsType<ToolBar>(window.FindName("MainToolbar"));
                Assert.NotEmpty(toolbar.Items);

                var editorPane = Assert.IsType<Border>(window.FindName("EditorPaneBorder"));
                var consolePane = Assert.IsType<Border>(window.FindName("ConsolePaneBorder"));
                Assert.Equal(new Thickness(0), editorPane.BorderThickness);
                Assert.Equal(new Thickness(0), consolePane.BorderThickness);

                var workspaceGrid = Assert.IsType<Grid>(window.FindName("WorkspaceGrid"));
                Assert.Equal(new Thickness(4), workspaceGrid.Margin);
                var workspaceParent = Assert.IsType<DockPanel>(workspaceGrid.Parent);
                var appBackground = Assert.IsType<SolidColorBrush>(application.FindResource("Theme.App.Background"));
                Assert.Equal(appBackground.Color, Assert.IsType<SolidColorBrush>(workspaceParent.Background).Color);

                var subtleBorder = Assert.IsType<SolidColorBrush>(application.FindResource("Theme.Border.Subtle"));
                Assert.Equal(subtleBorder.Color, Assert.IsType<SolidColorBrush>(editorPane.BorderBrush).Color);
                Assert.Equal(subtleBorder.Color, Assert.IsType<SolidColorBrush>(consolePane.BorderBrush).Color);

                var consoleHeader = Assert.IsType<Border>(window.FindName("ConsoleHeaderBorder"));
                Assert.Equal(new Thickness(0, 0, 0, 1), consoleHeader.BorderThickness);
                Assert.Equal(subtleBorder.Color, Assert.IsType<SolidColorBrush>(consoleHeader.BorderBrush).Color);

                var editorConsoleSplitter = Assert.IsType<GridSplitter>(window.FindName("EditorConsoleRowSplitter"));
                Assert.Equal(6, editorConsoleSplitter.Height);
                Assert.Equal(GridResizeDirection.Rows, editorConsoleSplitter.ResizeDirection);
            }
            finally
            {
                window.Close();
            }
        });
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
        public string SettingsFilePath => "startup-xaml-regression.settings.json";

        public ApplicationSettings LoadSettings() => settings;

        public void SaveSettings(ApplicationSettings settings) { }
    }
}
