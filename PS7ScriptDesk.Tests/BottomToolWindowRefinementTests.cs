using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class BottomToolWindowRefinementTests
{
    [Fact]
    public void ApplicationSettings_DefaultAndMissingBottomToolFieldsAreSafe()
    {
        var defaults = new ApplicationSettings();

        Assert.False(defaults.IsBottomToolWindowVisible);
        Assert.False(defaults.IsBottomToolWindowFloating);
        Assert.Equal("Problems", defaults.SelectedBottomToolTab);
        Assert.Null(defaults.DockedBottomToolWindowHeight);
        Assert.Null(defaults.BottomToolWindowWidth);
        Assert.Null(defaults.BottomToolWindowHeight);
        Assert.Null(defaults.BottomToolWindowLeft);
        Assert.Null(defaults.BottomToolWindowTop);

        var restored = JsonSerializer.Deserialize<ApplicationSettings>(
            """
            {
              "Theme": "Light",
              "WorkspaceLayoutMode": "ConsoleMaximized"
            }
            """)!;

        Assert.Equal("Light", restored.Theme);
        Assert.Equal("ConsoleMaximized", restored.WorkspaceLayoutMode);
        Assert.False(restored.IsBottomToolWindowVisible);
        Assert.False(restored.IsBottomToolWindowFloating);
        Assert.Equal("Problems", restored.SelectedBottomToolTab);
    }

    [Fact]
    public void MainWindow_ConsoleRemainsSeparateFromBottomToolGroup()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var consolePaneXaml = ExtractBetween(mainXaml, "<Border x:Name=\"ConsolePaneBorder\"", "<!-- Debug splitter");
        var consoleRuntimeXaml = ExtractBetween(mainXaml, "<Border x:Name=\"ConsolePaneBorder\"", "<GridSplitter x:Name=\"BottomToolWindowSplitter\"");
        var toolGroupXaml = ExtractBetween(mainXaml, "<Border x:Name=\"BottomToolWindowBorder\"", "<!-- Debug splitter");
        var toolTabStripXaml = ExtractBetween(toolGroupXaml, "<Grid Grid.Row=\"1\" Margin=\"0,0,0,4\">", "<Grid Grid.Row=\"2\">");

        Assert.Contains("x:Name=\"TerminalConsole\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestartConsoleCommand}\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsoleBottomPaneTab\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DiagnosticsBottomPane\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DebugOutputBottomPane\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ActivityBottomPane\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomProblemsToolTab", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomDebugOutputToolTab", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomActivityToolTab", consoleRuntimeXaml, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"BottomToolWindowSplitter\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomToolWindowContent\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DiagnosticsBottomPane\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugOutputBottomPane\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ActivityBottomPane\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(toolGroupXaml, "x:Name=\"BottomProblemsToolTab\""));
        Assert.Equal(1, CountOccurrences(toolGroupXaml, "x:Name=\"BottomDebugOutputToolTab\""));
        Assert.Equal(1, CountOccurrences(toolGroupXaml, "x:Name=\"BottomActivityToolTab\""));
        Assert.Equal(3, CountOccurrences(toolGroupXaml, "Style=\"{StaticResource IdeBottomPaneTabToggleButtonStyle}\""));
        Assert.Equal(3, CountOccurrences(toolTabStripXaml, "<ColumnDefinition Width=\"*\" />"));
    }

    [Fact]
    public void MainWindow_BottomToolGroupHasShowHideAndSinglePopOutDockCommands()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var floatingWindowXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");
        var floatingWindowCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "BottomToolWindow.xaml.cs");

        Assert.Contains("x:Name=\"ShowBottomToolWindowMenuItem\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ShowBottomToolWindow_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PopOutBottomToolWindowMenuItem\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PopOutBottomToolWindowMenuItem_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DockBottomToolWindowMenuItem\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DockBottomToolWindowMenuItem_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PopOutBottomToolWindowButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DockBottomToolWindowButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"HideBottomToolWindowButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Toolbar", ExtractBetween(mainXaml, "<Border x:Name=\"BottomToolWindowBorder\"", "<!-- Debug splitter"), StringComparison.Ordinal);

        Assert.Contains("private void ShowBottomToolWindow(BottomToolTab selectedTab, string reason)", mainCode, StringComparison.Ordinal);
        Assert.Contains("private void HideBottomToolWindow(string reason)", mainCode, StringComparison.Ordinal);
        Assert.Contains("private void PopOutBottomToolWindow(string reason)", mainCode, StringComparison.Ordinal);
        Assert.Contains("private void DockBottomToolWindow(string reason)", mainCode, StringComparison.Ordinal);
        Assert.Contains("CloseForDockBack()", floatingWindowCode, StringComparison.Ordinal);
        Assert.Contains("DockBackRequested?.Invoke(this, EventArgs.Empty);", floatingWindowCode, StringComparison.Ordinal);
        Assert.Contains("<ContentControl x:Name=\"ToolContentHost\"", floatingWindowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PersistsBottomToolStateIndependentlyFromWorkspaceLayout()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("_selectedBottomToolTab = RestoreBottomToolTab(_loadedSettings.SelectedBottomToolTab);", mainCode, StringComparison.Ordinal);
        Assert.Contains("_isBottomToolWindowVisible = _loadedSettings.IsBottomToolWindowVisible;", mainCode, StringComparison.Ordinal);
        Assert.Contains("_isBottomToolWindowFloating = _loadedSettings.IsBottomToolWindowFloating;", mainCode, StringComparison.Ordinal);
        Assert.Contains("if (IsUsableLength(_loadedSettings.DockedBottomToolWindowHeight, MinimumBottomToolWindowHeight))", mainCode, StringComparison.Ordinal);
        Assert.Contains("RestoreBottomToolWindowFromSettings();", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.IsBottomToolWindowVisible = _isBottomToolWindowVisible;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.IsBottomToolWindowFloating = _isBottomToolWindowFloating;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.SelectedBottomToolTab = _selectedBottomToolTab.ToString();", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.DockedBottomToolWindowHeight = _lastKnownBottomToolWindowHeight;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.BottomToolWindowWidth = bottomToolWindowBounds.Width;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.BottomToolWindowHeight = bottomToolWindowBounds.Height;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.BottomToolWindowLeft = bottomToolWindowBounds.Left;", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.BottomToolWindowTop = bottomToolWindowBounds.Top;", mainCode, StringComparison.Ordinal);

        var workspaceMethod = ExtractBetween(mainCode, "private void ApplyWorkspaceLayoutMode", "private void CaptureWorkspaceLayoutSizes");
        Assert.Contains("CaptureDockedBottomToolWindowHeight();", workspaceMethod, StringComparison.Ordinal);
        Assert.Contains("ApplyBottomToolWindowPresentationState(source);", workspaceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_isBottomToolWindowVisible = false", workspaceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_isBottomToolWindowFloating = false", workspaceMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_LayoutTransitionsPreserveBottomToolState()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var presenter = ExtractBetween(mainCode, "private void ApplyBottomToolWindowPresentationState", "private void EnsureBottomToolWindowContentDocked");

        Assert.Contains("_workspaceLayoutMode != WorkspaceLayoutMode.EditorMaximized", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.SetColumn(BottomToolWindow", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("targetColumn", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowSplitterRowDefinition.Height = new GridLength(BottomToolWindowSplitterThickness, GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowRowDefinition.Height = new GridLength(Math.Max(_lastKnownBottomToolWindowHeight, MinimumBottomToolWindowHeight), GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowRowDefinition.MinHeight = MinimumBottomToolWindowHeight;", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowSplitterRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowRowDefinition.MinHeight = 0;", presenter, StringComparison.Ordinal);
        Assert.Contains("ApplyHorizontalConsoleRegionHeight(dockedVisible);", presenter, StringComparison.Ordinal);
        Assert.Contains("WorkspaceEditorMaximized_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("WorkspaceConsoleMaximized_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("WorkspaceHorizontalSplit_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("WorkspaceSideBySideSplit_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("WorkspaceRestoreDefault_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("SetDebugPanelVisible(ShowDebugPanelMenuItem.IsChecked);", mainCode, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(new Action(ApplyExplorerVisibilityLayout));", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_FloatingBottomToolWindowReparentsSingleContentAndRecoversOffScreenBounds()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("EnsureBottomToolWindowContentFloating(bottomToolWindow);", mainCode, StringComparison.Ordinal);
        Assert.Contains("bottomToolWindow.SetToolContent(BottomToolWindowContent);", mainCode, StringComparison.Ordinal);
        Assert.Contains("EnsureBottomToolWindowContentDocked();", mainCode, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowDockHost.Children.Add(BottomToolWindowContent);", mainCode, StringComparison.Ordinal);
        Assert.Contains("bottomToolWindow.ClearToolContent();", mainCode, StringComparison.Ordinal);
        Assert.Contains("bottomToolWindow.LocationChanged += BottomToolWindow_LocationChanged;", mainCode, StringComparison.Ordinal);
        Assert.Contains("bottomToolWindow.SizeChanged += BottomToolWindow_SizeChanged;", mainCode, StringComparison.Ordinal);
        Assert.Contains("private static bool IsWindowBoundsVisible(Rect bounds)", mainCode, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.VirtualScreenLeft", mainCode, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.VirtualScreenWidth", mainCode, StringComparison.Ordinal);
        Assert.Contains("var restoredBounds = hasVisibleSavedBounds ? savedBounds : fallbackBounds;", mainCode, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker '{startMarker}' was not found.");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"End marker '{endMarker}' was not found.");
        return text.Substring(start, end - start);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
