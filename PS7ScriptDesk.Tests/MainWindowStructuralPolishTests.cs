namespace PS7ScriptDesk.Tests;

public sealed class MainWindowStructuralPolishTests
{
    [Fact]
    public void MainWindow_UsesSharedIdeStructuralStylesForPrimaryPanes()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("x:Key=\"IdePaneBorderStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeSectionHeaderTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeSectionHeaderBorderStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeHeaderPanelStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorHeaderPanelStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeStatusStripBorderStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorStatusStripBorderStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeSecondaryButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeStatusBarSeparatorStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeColumnSplitterStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeRowSplitterStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowPrimaryTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowLabelTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowMetaTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowDataGridStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowListBoxItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeEmptyPaneTextStyle\"", appXaml, StringComparison.Ordinal);

        Assert.Contains("Style=\"{StaticResource IdePaneBorderStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeCompactEditorStatusStripBorderStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource EditorSurfaceBorderStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource SubtlePanelBorderStyle}\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_TabsAndSecondaryButtonsUseSharedIdeStyles()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("x:Key=\"IdeTabItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorTabItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeBottomPaneTabToggleButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"BottomPaneTabToggleButtonStyle\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle=\"{StaticResource IdeTabItemStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle=\"{StaticResource IdeCompactEditorTabItemStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeBottomPaneTabToggleButtonStyle}\""));
        Assert.Contains("Content=\"Reset Console\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Pop Out\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Open Folder\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Show in Explorer\"", mainXaml, StringComparison.Ordinal);
        Assert.True(CountOccurrences(mainXaml, "Style=\"{StaticResource IdeSecondaryButtonStyle}\"") >= 6);
    }

    [Fact]
    public void MainWindow_DefaultPaneSizingFavorsEditorAndPreservesSplitters()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Width=\"1360\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExplorerColumnDefinition\" Width=\"220\" MinWidth=\"190\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorColumnDefinition\" Width=\"*\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugPanelColumn\" Width=\"0\" MinWidth=\"0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("private const double MinimumExplorerWidth = 190;", mainCode, StringComparison.Ordinal);
        Assert.Contains("private double _lastKnownExplorerWidth = 220;", mainCode, StringComparison.Ordinal);
        Assert.Contains("private const double DebugPanelWidth = 220;", mainCode, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(mainXaml, "GridSplitter"));
        Assert.Contains("<Setter Property=\"ResizeDirection\" Value=\"Columns\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ResizeDirection\" Value=\"Rows\" />", appXaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeColumnSplitterStyle}\""));
        Assert.Equal(2, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeRowSplitterStyle}\""));
        Assert.Contains("ExplorerColumnDefinition.Width = new GridLength(Math.Max(_lastKnownExplorerWidth, MinimumExplorerWidth), GridUnitType.Pixel)", mainCode, StringComparison.Ordinal);
        Assert.Contains("if (IsUsableLength(_loadedSettings.ExplorerWidth, MinimumExplorerWidth))", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SidePaneVisibilityReleasesColumnsAndRestoresUsableWidths()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"ExplorerColumnDefinition\" Width=\"220\" MinWidth=\"190\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExplorerSplitterColumnDefinition\" Width=\"6\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorColumnDefinition\" Width=\"*\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsoleSideSplitterColumnDefinition\" Width=\"0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsoleSideColumnDefinition\" Width=\"0\" MinWidth=\"0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugPanelSplitterColumn\" Width=\"0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugPanelColumn\" Width=\"0\" MinWidth=\"0\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("private double _lastKnownExplorerWidth = 220;", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerColumnDefinition.Width = new GridLength(Math.Max(_lastKnownExplorerWidth, MinimumExplorerWidth), GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerColumnDefinition.MinWidth = MinimumExplorerWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerSplitterColumnDefinition.Width = new GridLength(6, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("if (ExplorerColumnDefinition.ActualWidth >= MinimumExplorerWidth)", mainCode, StringComparison.Ordinal);
        Assert.Contains("_lastKnownExplorerWidth = ExplorerColumnDefinition.ActualWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerColumnDefinition.MinWidth = 0;", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerSplitterColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);

        Assert.Contains("private const double MinimumDebugPanelWidth = 160;", mainCode, StringComparison.Ordinal);
        Assert.Contains("private double _lastKnownDebugPanelWidth = DebugPanelWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("CaptureDockedDebugPanelWidth();", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelColumn.Width         = new GridLength(Math.Max(_lastKnownDebugPanelWidth, MinimumDebugPanelWidth), GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelColumn.MinWidth      = MinimumDebugPanelWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelSplitterColumn.Width = new GridLength(6, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelBorder.Visibility == Visibility.Visible", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelColumn.ActualWidth >= MinimumDebugPanelWidth", mainCode, StringComparison.Ordinal);
        Assert.Contains("_lastKnownDebugPanelWidth = DebugPanelColumn.ActualWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelColumn.Width         = new GridLength(0, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelColumn.MinWidth      = 0;", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);

        Assert.Contains("EditorColumnDefinition.Width = new GridLength(1, GridUnitType.Star);", mainCode, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkspaceLayoutMode(WorkspaceLayoutMode.SideBySideSplit, \"ViewMenu\");", mainCode, StringComparison.Ordinal);
        Assert.Contains("ConsoleSideColumnDefinition.MinWidth = MinimumConsoleSideWidth;", mainCode, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(ConsolePaneBorder, 4);", mainCode, StringComparison.Ordinal);
        Assert.Contains("nameof(MainWindowViewModel.IsExplorerVisible)", mainCode, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(new Action(ApplyExplorerVisibilityLayout));", mainCode, StringComparison.Ordinal);
        Assert.Contains("ShowDebugPanel_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("CloseDebugPanelButton_Click", mainCode, StringComparison.Ordinal);
        Assert.Contains("PopOutDebugPane(\"HeaderButton\")", mainCode, StringComparison.Ordinal);
        Assert.Contains("DockDebugPane(\"PlaceholderButton\")", mainCode, StringComparison.Ordinal);
        Assert.Contains("SetDebugPanelVisible(true);", mainCode, StringComparison.Ordinal);
        Assert.Contains("Explorer side pane layout applied.", mainCode, StringComparison.Ordinal);
        Assert.Contains("Docked Debug pane layout applied.", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PhaseOnePreservesFunctionalSurfaceAndCommandHooks()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Command=\"{Binding RefreshRuntimesCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RefreshWorkspaceCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenWorkspaceFolderCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowWorkspaceFolderInExplorerCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestartConsoleCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ConsoleBottomPaneTab_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DiagnosticsBottomPaneTab_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DebugOutputBottomPaneTab_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ActivityBottomPaneTab_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PopOutDebugPaneButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CloseDebugPanelButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DebugBreakpointRemove_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SetDebugPanelVisible(bool visible)", mainCode, StringComparison.Ordinal);
        Assert.Contains("ApplyShellLayoutFromSettings()", mainCode, StringComparison.Ordinal);
        Assert.Contains("SaveApplicationSettings()", mainCode, StringComparison.Ordinal);
        Assert.Contains("Click=\"WorkspaceEditorMaximized_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"WorkspaceConsoleMaximized_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"WorkspaceHorizontalSplit_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"WorkspaceSideBySideSplit_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"WorkspaceRestoreDefault_Click\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PhaseTwoKeepsEditorChromeCompactAndInformative()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var editorPaneXaml = ExtractEditorPaneXaml(mainXaml);

        Assert.Contains("x:Key=\"IdeCompactEditorHeaderPanelStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"8,3\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorStatusStripBorderStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorTabItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"28\" />", appXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("help:ContextHelp.Key=\"Editor.ActiveDocument\"", editorPaneXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource IdeCompactEditorHeaderPanelStyle}\"", editorPaneXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource IdeHeaderPanelStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StringFormat=Path: {0}", editorPaneXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding SelectedTab.SelectionDisplayText, TargetNullValue=Selection: None}\"", editorPaneXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding SelectedTab.BreakpointDisplayText, TargetNullValue=Breakpoints: None}\"", editorPaneXaml, StringComparison.Ordinal);

        Assert.Contains("ItemContainerStyle=\"{StaticResource IdeCompactEditorTabItemStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,0,0,4\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"22\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"22\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DataContext.NewScriptCommand, RelativeSource={RelativeSource TemplatedParent}}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"16\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"16\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding FilePath, TargetNullValue=Unsaved document}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Close tab\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close tab\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DataContext.CloseTabCommand, ElementName=RootWindow}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("help:ContextHelp.Key=\"Editor.Footer\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeCompactEditorStatusStripBorderStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CaretDisplayText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding EditorMetricsText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectionDisplayText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectionDisplayText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding BreakpointDisplayText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding BreakpointDisplayText}\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PhaseThreeToolbarUsesCompactGroupsWithoutChangingCommands()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var toolbarXaml = ExtractMainToolbarXaml(mainXaml);

        Assert.Contains("x:Key=\"IdeToolbarButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarPrimaryButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarIconButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarSeparatorStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"6,2\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"26\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ToolTipService.InitialShowDelay\" Value=\"700\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ToolTipService.ShowDuration\" Value=\"12000\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"13\" />", mainXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"13\" />", mainXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsMouseOver\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsPressed\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsEnabled\" Value=\"False\">", appXaml, StringComparison.Ordinal);

        Assert.Equal(4, CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarSeparatorStyle}\""));
        Assert.True(CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarButtonStyle}\"") >= 8);
        Assert.True(CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarPrimaryButtonStyle}\"") >= 4);
        Assert.Equal(0, CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarIconButtonStyle}\""));

        Assert.Contains("Command=\"{Binding NewScriptCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenFile_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenWorkspaceFolderCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveFile_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseTabCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseAllTabsCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RunScript_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RunSelection_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding StopCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DebugToggle_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ContinueDebug_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"StepOver_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"StepInto_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"StepOut_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ClearConsoleCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"HelpOverview_Click\"", toolbarXaml, StringComparison.Ordinal);

        Assert.Equal(2, CountOccurrences(toolbarXaml, "IsEnabled=\"{Binding IsRunAvailable}\""));
        Assert.Equal(5, CountOccurrences(toolbarXaml, "IsEnabled=\"False\""));

        Assert.Contains("ToolTip=\"New Script (Ctrl+N)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Open File (Ctrl+O)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Open Folder (Ctrl+Shift+O)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Save (Ctrl+S)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Close Tab (Ctrl+W)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Close All Tabs (Ctrl+Shift+W)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Run Script (Ctrl+F5)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Run Selection (F8).", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Start Debug (F5). Stop Debug (Shift+F5).\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Continue (F5)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step Over (F10)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step Into (F11)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Step Out (Shift+F11)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"PS7 ScriptDesk Help\"", toolbarXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("Text=\"New (Ctrl+N)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Open (Ctrl+O)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Folder (Ctrl+Shift+O)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Save (Ctrl+S)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Run (Ctrl+F5)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Run Selection (F8)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Debug (F5)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Help (F1)\"", toolbarXaml, StringComparison.Ordinal);

        Assert.Contains("Text=\"New\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Open\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Folder\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Save\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Close\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Close All\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Run\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Run Selection\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Selection\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Interrupt\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Debug\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Stop\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Continue\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Step Over\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Step Into\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Step Out\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Clear\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Help\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close Tab\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close All Tabs\"", toolbarXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PhaseFourRefinesSidePanesWithoutChangingBehavior()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("x:Key=\"IdeToolWindowPrimaryTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowLabelTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowMetaTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowDataGridStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowListBoxItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"RowHeight\" Value=\"24\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ColumnHeaderHeight\" Value=\"26\" />", appXaml, StringComparison.Ordinal);

        Assert.Contains("Text=\"Explorer\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"PowerShell Runtime\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Workspace Filter\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding WorkspaceGroupHeaderText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Open Tabs\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeToolWindowPrimaryTextStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.True(CountOccurrences(mainXaml, "Style=\"{StaticResource IdeToolWindowMetaTextStyle}\"") >= 7);
        Assert.Equal(2, CountOccurrences(mainXaml, "ItemContainerStyle=\"{StaticResource IdeToolWindowListBoxItemStyle}\""));
        Assert.Contains("Command=\"{Binding RefreshRuntimesCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RefreshWorkspaceCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenWorkspaceFolderCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowWorkspaceFolderInExplorerCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItemChanged=\"WorkspaceTree_SelectedItemChanged\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MouseDoubleClick=\"WorkspaceTree_MouseDoubleClick\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("Visibility=\"{Binding IsExplorerVisible, Converter={StaticResource BooleanToVisibilityConverter}}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugPanelSplitter\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"IdeEmptyPaneTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Debug\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Pop out debug pane\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Pop out debug pane\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle=\"{StaticResource IdeTabItemStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeToolWindowDataGridStyle}\""));
        Assert.Contains("Text=\"No variables available.\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"No active call stack.\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"No breakpoints configured.\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasItems, ElementName=DebugVariablesGrid}\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasItems, ElementName=DebugCallStackGrid}\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasItems, ElementName=DebugBreakpointsGrid}\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(mainXaml, "BasedOn=\"{StaticResource IdeEmptyPaneTextStyle}\""));
        Assert.Contains("x:Name=\"DebugVariablesGrid\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugCallStackGrid\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugBreakpointsGrid\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PopOutDebugPaneButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CloseDebugPanelButton_Click\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DebugBreakpointRemove_Click\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PhaseFiveCompactsExplorerRuntimeAndWorkspaceMetadata()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Header=\"PowerShell Runtime\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedRuntimeCompactText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectedRuntimeCompactText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RunningRuntimeCompactText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding RunningRuntimeCompactText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding HasRunningRuntimeCompactText, Converter={StaticResource BooleanToVisibilityConverter}}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreferredRuntimeText", mainXaml, StringComparison.Ordinal);
        Assert.Contains("help:ContextHelp.Key=\"Runtime.Refresh\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RefreshRuntimesCommand}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("Text=\"Path\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedRuntimePathOnlyText, Mode=OneWay}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectedRuntimePathOnlyText}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBox Text=\"{Binding SelectedRuntimePathOnlyText, Mode=OneWay}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("Text=\"{Binding RuntimeListHeaderText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RuntimeSelectionStatusText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding RuntimeSelectionStatusText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"64\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"112\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DetectedRuntimes}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedRuntimeItem}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("<WrapPanel Grid.Row=\"0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding OpenTabCountText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding WorkspaceFileCountText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding WorkspaceFolderCountText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CurrentWorkspaceText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding CurrentWorkspaceText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedWorkspacePathText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectedWorkspacePathText}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"WorkspaceFilterBox\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RefreshWorkspaceCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenWorkspaceFolderCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ShowWorkspaceFolderInExplorerCommand}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WorkspaceTree\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItemChanged=\"WorkspaceTree_SelectedItemChanged\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MouseDoubleClick=\"WorkspaceTree_MouseDoubleClick\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding OpenTabs}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedTab}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"ExplorerColumnDefinition\" Width=\"220\" MinWidth=\"190\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ExplorerColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerColumnDefinition.Width = new GridLength(Math.Max(_lastKnownExplorerWidth, MinimumExplorerWidth), GridUnitType.Pixel);", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PhaseSixPolishesToolbarTabsStatusAndPreservesFields()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var toolbarXaml = ExtractMainToolbarXaml(mainXaml);
        var statusBarXaml = ExtractStatusBarXaml(mainXaml);

        Assert.Contains("ToolTip=\"Close Tab (Ctrl+W)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close Tab\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseTabCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Close\"", toolbarXaml, StringComparison.Ordinal);

        Assert.Contains("ToolTip=\"Close All Tabs (Ctrl+Shift+W)\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close All Tabs\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseAllTabsCommand}\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Close All\"", toolbarXaml, StringComparison.Ordinal);

        Assert.Contains("Click=\"RunSelection_Click\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Run Selection (F8).", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Run Selection\"", toolbarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Selection\"", toolbarXaml, StringComparison.Ordinal);

        Assert.Contains("<Setter TargetName=\"RootBorder\" Property=\"Opacity\" Value=\"0.72\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter TargetName=\"RootBorder\" Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Accent.Primary}\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeStatusBarSeparatorStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"6,2\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"14\" />", appXaml, StringComparison.Ordinal);

        Assert.True(CountOccurrences(statusBarXaml, "Style=\"{StaticResource IdeStatusBarSeparatorStyle}\"") >= 12);
        Assert.Contains("Text=\"{Binding VersionText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StatusText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SessionRestoreNoticeText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.CaretDisplayText, TargetNullValue='Ln 1, Col 1'}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.EditorMetricsText, TargetNullValue=Lines: 1}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.SelectionDisplayText, TargetNullValue=Selection: None}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.BreakpointDisplayText, TargetNullValue=Breakpoints: None}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RuntimeText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding WorkspaceText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ZoomLevelText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ExecutionProgressText}\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Editor metadata loading...\"", statusBarXaml, StringComparison.Ordinal);
        Assert.Contains("StatusBarHelpToggleButtonStyle", statusBarXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("Background=\"#", statusBarXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", statusBarXaml, StringComparison.Ordinal);

        Assert.Contains("Style=\"{StaticResource IdeColumnSplitterStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeRowSplitterStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("<Grid Margin=\"4\">", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorConsoleRowSplitterDefinition\" Height=\"6\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorConsoleRowSplitter\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorConsoleColumnSplitter\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConsolePaneBorder\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"6\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsMouseOver\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsKeyboardFocusWithin\" Value=\"True\">", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_FinalConsistencyRemovesLegacyToastAndBadgeChrome()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Background=\"{DynamicResource Theme.Status.Error.Background}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Theme.Status.Error.Border}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource Theme.Status.Error.Foreground}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDialogPanelStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MetadataToastCard.SetResourceReference(Border.BackgroundProperty", mainCode, StringComparison.Ordinal);
        Assert.Contains("MetadataToastTitleTextBlock.SetResourceReference(TextBlock.ForegroundProperty", mainCode, StringComparison.Ordinal);
        Assert.Contains("ThemeStatusWarningBackgroundResourceKey", mainCode, StringComparison.Ordinal);
        Assert.Contains("ThemeStatusErrorBackgroundResourceKey", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"18\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#FFE8F2FF", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#FF4A90E2", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateFrozenBrush(0xE8, 0xF2, 0xFF)", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateFrozenBrush(0xFD, 0xE8, 0xE8)", mainCode, StringComparison.Ordinal);
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

    private static string ExtractMainToolbarXaml(string mainXaml)
    {
        const string startMarker = "<ToolBarTray DockPanel.Dock=\"Top\"";
        const string endMarker = "</ToolBarTray>";
        var start = mainXaml.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Main toolbar tray was not found.");
        var end = mainXaml.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, "Main toolbar tray closing tag was not found.");
        return mainXaml.Substring(start, end + endMarker.Length - start);
    }

    private static string ExtractStatusBarXaml(string mainXaml)
    {
        const string startMarker = "<StatusBar DockPanel.Dock=\"Bottom\"";
        const string endMarker = "</StatusBar>";
        var start = mainXaml.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Status bar was not found.");
        var end = mainXaml.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, "Status bar closing tag was not found.");
        return mainXaml.Substring(start, end + endMarker.Length - start);
    }

    private static string ExtractEditorPaneXaml(string mainXaml)
    {
        const string startMarker = "<Border x:Name=\"EditorPaneBorder\"";
        const string endMarker = "<GridSplitter x:Name=\"EditorConsoleRowSplitter\"";
        var start = mainXaml.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Editor pane was not found.");
        var end = mainXaml.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, "Editor/console row splitter was not found.");
        return mainXaml.Substring(start, end - start);
    }

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
