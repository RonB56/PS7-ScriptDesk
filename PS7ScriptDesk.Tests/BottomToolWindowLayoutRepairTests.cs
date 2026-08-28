namespace PS7ScriptDesk.Tests;

public sealed class BottomToolWindowLayoutRepairTests
{
    [Fact]
    public void MainWindow_DockedToolWindowIsOwnedByConsoleRegionNotWorkspaceRoot()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var workspaceGridRows = ExtractBetween(mainXaml, "<Grid Margin=\"4\">", "</Grid.RowDefinitions>");
        var consolePaneXaml = ExtractBetween(mainXaml, "<Border x:Name=\"ConsolePaneBorder\"", "<!-- Debug splitter");
        var toolGroupXaml = ExtractBetween(mainXaml, "<Border x:Name=\"BottomToolWindowBorder\"", "<!-- Debug splitter");
        var toolBorderDeclaration = ExtractBetween(mainXaml, "<Border x:Name=\"BottomToolWindowBorder\"", "Visibility=\"Collapsed\">");
        var sideBySideCase = ExtractBetween(mainCode, "case WorkspaceLayoutMode.SideBySideSplit:", "break;");
        var presenter = ExtractBetween(mainCode, "private void ApplyBottomToolWindowPresentationState", "private void EnsureBottomToolWindowContentDocked");

        Assert.Contains("x:Name=\"EditorRowDefinition\" Height=\"*\"", workspaceGridRows, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorConsoleRowSplitterDefinition\" Height=\"6\"", workspaceGridRows, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsoleRowDefinition\" Height=\"180\"", workspaceGridRows, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomToolWindowRowSplitterDefinition", workspaceGridRows, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomToolWindowSplitterRowDefinition", workspaceGridRows, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomToolWindowRowDefinition", workspaceGridRows, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"ConsoleContentRowDefinition\" Height=\"*\" MinHeight=\"160\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomToolWindowSplitterRowDefinition\" Height=\"0\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomToolWindowRowDefinition\" Height=\"0\" MinHeight=\"0\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomToolWindowSplitter\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomToolWindowBorder\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"3\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=", toolBorderDeclaration, StringComparison.Ordinal);

        Assert.Contains("Grid.SetRow(ConsolePaneBorder, 0);", sideBySideCase, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRowSpan(ConsolePaneBorder, 3);", sideBySideCase, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(ConsolePaneBorder, 4);", sideBySideCase, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.SetColumn(BottomToolWindow", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("targetColumn", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_DockedToolWindowUsesRealConsoleLocalVerticalSplitter()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var consolePaneXaml = ExtractBetween(mainXaml, "<Border x:Name=\"ConsolePaneBorder\"", "<!-- Debug splitter");
        var splitterIndex = consolePaneXaml.IndexOf("x:Name=\"BottomToolWindowSplitter\"", StringComparison.Ordinal);
        var toolIndex = consolePaneXaml.IndexOf("x:Name=\"BottomToolWindowBorder\"", StringComparison.Ordinal);

        Assert.True(splitterIndex >= 0, "The Console region should contain the docked tool splitter.");
        Assert.True(toolIndex > splitterIndex, "The docked tool group should appear below the Console-local splitter.");
        Assert.Contains("Style=\"{StaticResource IdeRowSplitterStyle}\"", ExtractBetween(consolePaneXaml, "<GridSplitter x:Name=\"BottomToolWindowSplitter\"", "/>"), StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ResizeDirection\" Value=\"Rows\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsoleContentRowDefinition\" Height=\"*\" MinHeight=\"160\"", consolePaneXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomToolWindowRowDefinition\" Height=\"0\" MinHeight=\"0\"", consolePaneXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_DockedToolHeightIsIndependentFromEditorConsoleSplitterState()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var presenter = ExtractBetween(mainCode, "private void ApplyBottomToolWindowPresentationState", "private void EnsureBottomToolWindowContentDocked");
        var horizontalHeightHelper = ExtractBetween(mainCode, "private void ApplyHorizontalConsoleRegionHeight", "private void CaptureBottomToolWindowBounds");
        var captureMethod = ExtractBetween(mainCode, "private void CaptureWorkspaceLayoutSizes", "private static WorkspaceLayoutMode RestoreWorkspaceLayoutMode");
        var saveBeforeConsoleAssignment = ExtractBetween(mainCode, "private void SaveApplicationSettings", "settings.ConsoleHeight = _lastKnownConsoleHeight;");

        Assert.Contains("private const double BottomToolWindowSplitterThickness = 6;", mainCode, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowRowDefinition.Height = new GridLength(Math.Max(_lastKnownBottomToolWindowHeight, MinimumBottomToolWindowHeight), GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
        Assert.Contains("ApplyHorizontalConsoleRegionHeight(dockedVisible);", presenter, StringComparison.Ordinal);
        Assert.Contains("if (_workspaceLayoutMode is not (WorkspaceLayoutMode.Default or WorkspaceLayoutMode.HorizontalSplit))", horizontalHeightHelper, StringComparison.Ordinal);
        Assert.Contains("consoleHeight += BottomToolWindowSplitterThickness + Math.Max(_lastKnownBottomToolWindowHeight, MinimumBottomToolWindowHeight);", horizontalHeightHelper, StringComparison.Ordinal);
        Assert.Contains("consoleHeight -= BottomToolWindowSplitterRowDefinition.ActualHeight;", captureMethod, StringComparison.Ordinal);
        Assert.Contains("consoleHeight -= BottomToolWindowRowDefinition.ActualHeight;", captureMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastKnownConsoleHeight = ConsoleRowDefinition.ActualHeight;", saveBeforeConsoleAssignment, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_LayoutTransitionsPreserveDockedToolStateAcrossWorkspaceModes()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var workspaceMethod = ExtractBetween(mainCode, "private void ApplyWorkspaceLayoutMode", "private void CaptureWorkspaceLayoutSizes");
        var presenter = ExtractBetween(mainCode, "private void ApplyBottomToolWindowPresentationState", "private void EnsureBottomToolWindowContentDocked");

        Assert.Contains("CaptureDockedBottomToolWindowHeight();", workspaceMethod, StringComparison.Ordinal);
        Assert.Contains("CaptureWorkspaceLayoutSizes();", workspaceMethod, StringComparison.Ordinal);
        Assert.Contains("ApplyBottomToolWindowPresentationState(source);", workspaceMethod, StringComparison.Ordinal);
        Assert.Contains("_workspaceLayoutMode != WorkspaceLayoutMode.EditorMaximized", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("_isBottomToolWindowVisible = false", workspaceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_isBottomToolWindowFloating = false", workspaceMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastKnownBottomToolWindowHeight = DefaultBottomToolWindowHeight", workspaceMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_FloatingModeDoesNotUseDockedSplitterGeometry()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var popOutMethod = ExtractBetween(mainCode, "private void PopOutBottomToolWindow(string reason)", "private void DockBottomToolWindow");
        var presenter = ExtractBetween(mainCode, "private void ApplyBottomToolWindowPresentationState", "private void EnsureBottomToolWindowContentDocked");

        Assert.Contains("_isBottomToolWindowFloating = true;", popOutMethod, StringComparison.Ordinal);
        Assert.Contains("EnsureBottomToolWindowContentFloating(bottomToolWindow);", popOutMethod, StringComparison.Ordinal);
        Assert.Contains("!_isBottomToolWindowFloating", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowSplitter.Visibility = Visibility.Collapsed;", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowSplitterRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ConsoleRemainsOutsideThreeToolGroupAfterLayoutRepair()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var consoleRuntimeXaml = ExtractBetween(mainXaml, "<Border x:Name=\"ConsolePaneBorder\"", "<GridSplitter x:Name=\"BottomToolWindowSplitter\"");
        var toolGroupXaml = ExtractBetween(mainXaml, "<Border x:Name=\"BottomToolWindowBorder\"", "<!-- Debug splitter");

        Assert.Contains("x:Name=\"ConsoleBottomPaneTab\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TerminalConsole\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestartConsoleCommand}\"", consoleRuntimeXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalConsole", toolGroupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartConsoleCommand", toolGroupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomProblemsToolTab\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomDebugOutputToolTab\"", toolGroupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BottomActivityToolTab\"", toolGroupXaml, StringComparison.Ordinal);
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
