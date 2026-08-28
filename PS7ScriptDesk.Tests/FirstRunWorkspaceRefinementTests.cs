using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class FirstRunWorkspaceRefinementTests
{
    [Fact]
    public void ApplicationSettings_FirstRunDefaultsUseCleanEditorConsoleWorkspace()
    {
        var settings = new ApplicationSettings();

        Assert.Equal("Dark", settings.Theme);
        Assert.False(settings.IsContextHelpEnabled);
        Assert.False(settings.IsExplorerVisible);
        Assert.False(settings.IsDebugPanelVisible);
        Assert.Equal("HorizontalSplit", settings.WorkspaceLayoutMode);
        Assert.Null(settings.ConsoleHeight);
        Assert.Null(settings.ConsoleSideWidth);
    }

    [Fact]
    public void ApplicationSettings_ExplicitSavedPreferencesOverrideFirstRunDefaults()
    {
        var settings = new ApplicationSettings
        {
            Theme = "IseBlue",
            IsContextHelpEnabled = true,
            IsExplorerVisible = true,
            IsDebugPanelVisible = true,
            WorkspaceLayoutMode = "SideBySideSplit",
            ExplorerWidth = 255,
            ConsoleHeight = 300,
            ConsoleSideWidth = 460,
            DockedDebugPanelWidth = 260
        };

        Assert.Equal("IseBlue", settings.Theme);
        Assert.True(settings.IsContextHelpEnabled);
        Assert.True(settings.IsExplorerVisible);
        Assert.True(settings.IsDebugPanelVisible);
        Assert.Equal("SideBySideSplit", settings.WorkspaceLayoutMode);
        Assert.Equal(255, settings.ExplorerWidth);
        Assert.Equal(300, settings.ConsoleHeight);
        Assert.Equal(460, settings.ConsoleSideWidth);
        Assert.Equal(260, settings.DockedDebugPanelWidth);
    }

    [Fact]
    public void MainWindow_RestoresAndSavesWorkspaceLayoutStateThroughExistingSettings()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("ApplyWorkspaceLayoutMode(RestoreWorkspaceLayoutMode(_loadedSettings.WorkspaceLayoutMode), \"SettingsRestore\");", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.WorkspaceLayoutMode = _workspaceLayoutMode.ToString();", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.ConsoleSideWidth = _lastKnownConsoleSideWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.IsDebugPanelVisible = DebugPanelBorder.Visibility == Visibility.Visible;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.DockedDebugPanelWidth = _lastKnownDebugPanelWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("return WorkspaceLayoutMode.HorizontalSplit;", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_FirstRunWorkspaceKeepsOnlyConsoleBottomPaneVisible()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var consolePaneXaml = ExtractBetween(mainXaml, "<Border x:Name=\"ConsolePaneBorder\"", "<!-- Debug splitter");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"ConsoleRowDefinition\" Height=\"180\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("private const double DefaultConsoleHeight = 180;", mainCode, StringComparison.Ordinal);
        Assert.Contains("private WorkspaceLayoutMode _workspaceLayoutMode = WorkspaceLayoutMode.HorizontalSplit;", mainCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsoleBottomPaneTab\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"True\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DiagnosticsBottomPaneTab\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugOutputBottomPaneTab\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ActivityBottomPaneTab\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\" />", ExtractBetween(consolePaneXaml, "<Grid x:Name=\"DiagnosticsBottomPane\"", "<Grid x:Name=\"DebugOutputBottomPane\""), StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\" />", ExtractBetween(consolePaneXaml, "<Grid x:Name=\"DebugOutputBottomPane\"", "<Grid x:Name=\"ActivityBottomPane\""), StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\" />", ExtractBetween(consolePaneXaml, "<Grid x:Name=\"ActivityBottomPane\"", "</Border>"), StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ActiveDocumentEditorHeaderIsRemovedButStateRemains()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var editorPaneXaml = ExtractBetween(mainXaml, "<Border x:Name=\"EditorPaneBorder\"", "<GridSplitter x:Name=\"EditorConsoleRowSplitter\"");
        var viewModelCode = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.DoesNotContain("help:ContextHelp.Key=\"Editor.ActiveDocument\"", editorPaneXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Active Document:", editorPaneXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StringFormat=Path: {0}", editorPaneXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding FilePath, TargetNullValue=Unsaved document}\"", editorPaneXaml, StringComparison.Ordinal);

        Assert.Contains("public string ActiveDocumentText =>", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(ActiveDocumentText));", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("SelectedTabFilePath = SelectedTab?.FilePath", viewModelCode, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker '{startMarker}' was not found.");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"End marker '{endMarker}' was not found.");
        return text.Substring(start, end - start);
    }

    private static string ReadRepositoryFile(params string[] segments)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(segments)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
