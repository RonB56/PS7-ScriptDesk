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
        Assert.Contains("x:Key=\"IdeColumnSplitterStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeRowSplitterStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeEmptyPaneTextStyle\"", appXaml, StringComparison.Ordinal);

        Assert.Contains("Style=\"{StaticResource IdePaneBorderStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeCompactEditorHeaderPanelStyle}\"", mainXaml, StringComparison.Ordinal);
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
        Assert.Equal(4, CountOccurrences(mainXaml, "GridSplitter"));
        Assert.Contains("<Setter Property=\"ResizeDirection\" Value=\"Columns\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ResizeDirection\" Value=\"Rows\" />", appXaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeColumnSplitterStyle}\""));
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
    }

    [Fact]
    public void MainWindow_PhaseTwoKeepsEditorChromeCompactAndInformative()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("x:Key=\"IdeCompactEditorHeaderPanelStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"8,3\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorStatusStripBorderStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeCompactEditorTabItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"28\" />", appXaml, StringComparison.Ordinal);

        Assert.Contains("<Grid Grid.Row=\"0\" Margin=\"0,0,0,4\">", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeCompactEditorHeaderPanelStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource IdeHeaderPanelStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ActiveDocumentText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding ActiveDocumentText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.FilePath, StringFormat=Path: {0}, TargetNullValue=Path: Unsaved document}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectedTab.FilePath, TargetNullValue=Unsaved document}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.SelectionDisplayText, TargetNullValue=Selection: None}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectedTab.SelectionDisplayText, TargetNullValue=Selection: None}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.BreakpointDisplayText, TargetNullValue=Breakpoints: None}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SelectedTab.BreakpointDisplayText, TargetNullValue=Breakpoints: None}\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("ItemContainerStyle=\"{StaticResource IdeCompactEditorTabItemStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,0,0,4\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"22\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"22\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DataContext.NewScriptCommand, RelativeSource={RelativeSource TemplatedParent}}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"16\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"16\"", mainXaml, StringComparison.Ordinal);
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
        Assert.Contains("<Trigger Property=\"IsMouseOver\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsPressed\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsEnabled\" Value=\"False\">", appXaml, StringComparison.Ordinal);

        Assert.Equal(4, CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarSeparatorStyle}\""));
        Assert.True(CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarButtonStyle}\"") >= 8);
        Assert.True(CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarPrimaryButtonStyle}\"") >= 4);
        Assert.Equal(2, CountOccurrences(toolbarXaml, "Style=\"{StaticResource IdeToolbarIconButtonStyle}\""));

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
        Assert.Contains("Text=\"Run\"", toolbarXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Selection\"", toolbarXaml, StringComparison.Ordinal);
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
    public void MainWindow_FinalPolishKeepsSplittersAndDebugEmptyStatesIntentional()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("x:Key=\"IdeSplitterGripStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeColumnSplitterStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeRowSplitterStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"6\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"6\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Cursor\" Value=\"SizeWE\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Cursor\" Value=\"SizeNS\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ShowsPreview\" Value=\"True\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsMouseOver\" Value=\"True\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsKeyboardFocusWithin\" Value=\"True\">", appXaml, StringComparison.Ordinal);

        Assert.Equal(2, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeColumnSplitterStyle}\""));
        Assert.Equal(2, CountOccurrences(mainXaml, "Style=\"{StaticResource IdeRowSplitterStyle}\""));
        Assert.Contains("Visibility=\"{Binding IsExplorerVisible, Converter={StaticResource BooleanToVisibilityConverter}}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugPanelSplitter\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", mainXaml, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"IdeEmptyPaneTextStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"No variables to show.\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"No call stack to show.\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"No breakpoints to show.\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasItems, ElementName=DebugVariablesGrid}\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasItems, ElementName=DebugCallStackGrid}\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasItems, ElementName=DebugBreakpointsGrid}\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(mainXaml, "BasedOn=\"{StaticResource IdeEmptyPaneTextStyle}\""));
        Assert.Contains("x:Name=\"DebugVariablesGrid\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugCallStackGrid\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugBreakpointsGrid\"", mainXaml, StringComparison.Ordinal);
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
