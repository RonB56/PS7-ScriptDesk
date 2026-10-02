using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase5SplitterModernizationContractTests
{
    [Fact]
    public void SharedSplitterStylesProvideThemeAwareIdleHoverFocusAndDragStates()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");

        Assert.Contains("x:Key=\"IdeSplitterGripStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeColumnSplitterStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeRowSplitterStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("DynamicResource Theme.Splitter.Idle", app, StringComparison.Ordinal);
        Assert.Contains("DynamicResource Theme.Splitter.Hover", app, StringComparison.Ordinal);
        Assert.Contains("DynamicResource Theme.Splitter.Focus", app, StringComparison.Ordinal);
        Assert.Contains("DynamicResource Theme.Splitter.Active", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsDragging\"", app, StringComparison.Ordinal);
        Assert.Contains("Value=\"SizeWE\"", app, StringComparison.Ordinal);
        Assert.Contains("Value=\"SizeNS\"", app, StringComparison.Ordinal);

        foreach (var theme in new[] { "LightTheme.xaml", "DarkTheme.xaml", "IseBlueTheme.xaml" })
        {
            var source = Read("PS7ScriptDesk.Shell", "Themes", theme);
            foreach (var key in new[] { "Theme.Splitter.Idle", "Theme.Splitter.Hover", "Theme.Splitter.Active", "Theme.Splitter.Focus" })
            {
                Assert.Contains($"x:Key=\"{key}\"", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MainWindowSplitterOwnershipAndDragContractsRemainUnchanged()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var code = Read("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        AssertSplitter(main, "SideBySideDebugSplitter", "Grid.Row=\"0\"", "Grid.Column=\"3\"", "ResizeDirection=\"Columns\"", "ResizeBehavior=\"PreviousAndNext\"", "ShowsPreview=\"False\"");
        AssertSplitter(main, "ExplorerPaneSplitter", "Grid.Row=\"0\"", "Grid.RowSpan=\"3\"", "Grid.Column=\"1\"");
        AssertSplitter(main, "EditorConsoleRowSplitter", "Grid.Row=\"1\"", "Grid.Column=\"2\"");
        AssertSplitter(main, "EditorConsoleColumnSplitter", "Grid.Row=\"0\"", "Grid.RowSpan=\"3\"", "Grid.Column=\"3\"");
        AssertSplitter(main, "BottomToolWindowSplitter", "Grid.Row=\"2\"");
        AssertSplitter(main, "DebugPanelSplitter", "Grid.Row=\"0\"", "Grid.RowSpan=\"3\"", "Grid.Column=\"5\"");

        Assert.Contains("<GridSplitter Grid.Row=\"5\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeRowSplitterStyle}\"", ExtractBetween(main, "<GridSplitter Grid.Row=\"5\"", " />"), StringComparison.Ordinal);

        foreach (var handler in new[]
        {
            "ExplorerPaneSplitter_DragStarted", "ExplorerPaneSplitter_DragCompleted",
            "HorizontalSplitter_DragStarted", "HorizontalSplitter_DragDelta", "HorizontalSplitter_DragCompleted",
            "DebugPanelSplitter_DragStarted", "DebugPanelSplitter_DragDelta", "DebugPanelSplitter_DragCompleted",
            "SideBySideDebugSplitter_DragStarted", "SideBySideDebugSplitter_DragDelta", "SideBySideDebugSplitter_DragCompleted"
        })
        {
            Assert.Contains(handler, main, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("new GridSplitter", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(DebugPanelSplitter, splitterColumn);", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(SideBySideDebugSplitter, 3);", code, StringComparison.Ordinal);
        Assert.Contains("BottomToolWindowSplitterRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RelatedToolWindowsRetainNativeSplitterPresentationAndOwnership()
    {
        var git = Read("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var api = Read("PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");

        Assert.Equal(3, Count(git, "<GridSplitter"));
        Assert.Equal(3, Count(git, "ResizeDirection=\"Columns\""));
        Assert.Contains("x:Name=\"LocalTestResultsSplitter\"", api, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeRowSplitterStyle}\"", api, StringComparison.Ordinal);
        Assert.DoesNotContain("GridSplitter", debug, StringComparison.Ordinal);
        Assert.DoesNotContain("GridSplitter", bottom, StringComparison.Ordinal);
    }

    private static void AssertSplitter(string xaml, string name, params string[] expected)
    {
        var start = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Splitter '{name}' is missing.");
        var end = xaml.IndexOf(" />", start, StringComparison.Ordinal);
        var element = end >= 0 ? xaml[start..end] : xaml[start..];
        foreach (var marker in expected)
        {
            Assert.Contains(marker, element, StringComparison.Ordinal);
        }
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker '{startMarker}'.");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing end marker '{endMarker}'.");
        return source[start..end];
    }

    private static int Count(string text, string value)
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
