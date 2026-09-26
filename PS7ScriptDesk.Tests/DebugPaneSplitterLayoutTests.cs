namespace PS7ScriptDesk.Tests;

public sealed class DebugPaneSplitterLayoutTests
{
    [Fact]
    public void DebugSplitter_UsesNativeGridSplitterAsItsOnlyResizeOwner()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"DebugPanelSplitter\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DragStarted=\"DebugPanelSplitter_DragStarted\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DragDelta=\"DebugPanelSplitter_DragDelta\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DragCompleted=\"DebugPanelSplitter_DragCompleted\"", mainXaml, StringComparison.Ordinal);
        var splitterXaml = ExtractBetween(mainXaml, "<GridSplitter x:Name=\"DebugPanelSplitter\"", "<!-- Debug panels");
        var sideBySideSplitterXaml = ExtractBetween(mainXaml, "<GridSplitter x:Name=\"SideBySideDebugSplitter\"", "</Grid>");
        Assert.DoesNotContain("ShowsPreview=\"False\"", splitterXaml, StringComparison.Ordinal);
        Assert.Contains("ShowsPreview=\"False\"", sideBySideSplitterXaml, StringComparison.Ordinal);
        Assert.Contains("ResizeDirection=\"Columns\"", sideBySideSplitterXaml, StringComparison.Ordinal);
        Assert.Contains("ResizeBehavior=\"PreviousAndNext\"", sideBySideSplitterXaml, StringComparison.Ordinal);
        Assert.Contains("DragStarted=\"SideBySideDebugSplitter_DragStarted\"", sideBySideSplitterXaml, StringComparison.Ordinal);
        Assert.Contains("DragDelta=\"SideBySideDebugSplitter_DragDelta\"", sideBySideSplitterXaml, StringComparison.Ordinal);
        Assert.Contains("DragCompleted=\"SideBySideDebugSplitter_DragCompleted\"", sideBySideSplitterXaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(mainXaml, "x:Name=\"DebugPanelBorder\""));
        Assert.DoesNotContain("WorkspaceGrid.Children.Remove(DebugPanelSplitter)", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SideBySideGrid.Children.Add(DebugPanelSplitter)", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SideBySideGrid.Children.Remove(SideBySideDebugSplitter)", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceGrid.Children.Add(SideBySideDebugSplitter)", mainCode, StringComparison.Ordinal);
        Assert.Contains("DebugPanelSplitter.Visibility = Visibility.Collapsed;", mainCode, StringComparison.Ordinal);
        Assert.Contains("SideBySideDebugSplitter.Visibility = Visibility.Collapsed;", mainCode, StringComparison.Ordinal);
        Assert.Contains("ResizeBehavior\" Value=\"PreviousAndNext\"", ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml"), StringComparison.Ordinal);
        Assert.Contains("ApplySideBySideContentStarSizing", mainCode, StringComparison.Ordinal);
        Assert.Contains("new GridLength(requestedConsoleWidth, GridUnitType.Star)", mainCode, StringComparison.Ordinal);
        Assert.Contains("new GridLength(requestedDebugWidth, GridUnitType.Star)", mainCode, StringComparison.Ordinal);
        Assert.Contains("nativeTargetPairIsStarStar", mainCode, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(DebugPanelSplitter, splitterColumn);", mainCode, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(DebugPanelBorder, panelColumn);", mainCode, StringComparison.Ordinal);
        Assert.Contains("AttachSplitterInputForensics(EditorConsoleColumnSplitter);", mainCode, StringComparison.Ordinal);
        Assert.Contains("AttachSplitterInputForensics(DebugPanelSplitter);", mainCode, StringComparison.Ordinal);
        Assert.Contains("AttachSplitterInputForensics(SideBySideDebugSplitter);", mainCode, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(SideBySideDebugSplitter, 3);", mainCode, StringComparison.Ordinal);
        Assert.Contains("AddSideBySideColumnDiagnostics", mainCode, StringComparison.Ordinal);
        Assert.Contains("Splitter_GotMouseCaptureForensics", mainCode, StringComparison.Ordinal);
        Assert.Contains("Splitter_LostMouseCaptureForensics", mainCode, StringComparison.Ordinal);
        Assert.Contains("minimumHorizontalDragDistance", mainCode, StringComparison.Ordinal);
        Assert.Contains("native GridSplitter owns the resize", mainCode, StringComparison.Ordinal);
        Assert.Contains("BuildDebugSplitterTargetColumnDiagnostics", mainCode, StringComparison.Ordinal);
        Assert.Contains("Debug splitter DragDelta entered before application handling.", mainCode, StringComparison.Ordinal);
        Assert.Contains("Debug splitter DragDelta observed after dispatcher processing.", mainCode, StringComparison.Ordinal);
        Assert.Contains("LogDebugSplitterWriterSnapshot", mainCode, StringComparison.Ordinal);
        Assert.Contains("LogCriticalForensic", mainCode, StringComparison.Ordinal);
        Assert.Contains("critical-ui-forensics.ndjson", ReadRepositoryFile("PS7ScriptDesk.Application", "Diagnostics", "DeveloperDiagnostics.cs"), StringComparison.Ordinal);
        Assert.Contains("requestedDebugDockedWidth", mainCode, StringComparison.Ordinal);
        Assert.Contains("targetPreviousIdentity", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DebugSplitterResizePolicy.Apply", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerVisibilityDoesNotRewriteEditorSizingOwnership()
    {
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var explorerMethod = ExtractBetween(mainCode, "private void ApplyExplorerVisibilityLayout()", "private void NormalizeLayoutBudget");

        Assert.DoesNotContain("EditorColumnDefinition.Width = new GridLength(1, GridUnitType.Star)", explorerMethod, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] pathParts)
    {
        var repositoryRoot = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { repositoryRoot }.Concat(pathParts).ToArray()));
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker: {start}");
        startIndex += start.Length;
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Missing end marker: {end}");
        return text[startIndex..endIndex];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
