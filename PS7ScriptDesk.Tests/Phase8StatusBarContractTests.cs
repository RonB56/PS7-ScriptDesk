using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase8StatusBarContractTests
{
    [Fact]
    public void StatusBarUsesFixedSingleRowAndThemeResources()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var statusBar = ExtractStatusBar(Read("PS7ScriptDesk.Shell", "MainWindow.xaml"));
        Assert.Contains("x:Key=\"IdeModernStatusBarStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeModernStatusBarStyle}\"", statusBar, StringComparison.Ordinal);
        Assert.Contains("Height=\"28\"", statusBar, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" MinWidth=\"0\" />", statusBar, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", statusBar, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", statusBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#", statusBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", statusBar, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBarShowsApplicationStatusAndPreservesActiveNotices()
    {
        var statusBar = ExtractStatusBar(Read("PS7ScriptDesk.Shell", "MainWindow.xaml"));
        foreach (var field in new[] { "ApplicationStatusText", "ConsoleInputStatusText", "SessionRestoreNoticeText", "ExecutionProgressText", "EditorMetadataStatusItem", "StatusBarHelpToggleButtonStyle" })
            Assert.Contains(field, statusBar, StringComparison.Ordinal);
        Assert.Contains("Width=\"150\"", statusBar, StringComparison.Ordinal);
        foreach (var removed in new[] { "VersionText", "SelectedTab.CaretDisplayText", "SelectedTab.EditorMetricsText", "SelectedTab.SelectionDisplayText", "SelectedTab.BreakpointDisplayText", "RuntimeText", "WorkspaceText", "GitStatusText", "ZoomLevelText", "UiScaleText" })
            Assert.DoesNotContain(removed, statusBar, StringComparison.Ordinal);
    }

    private static string ExtractStatusBar(string source)
    {
        const string startMarker = "<StatusBar DockPanel.Dock=\"Bottom\"";
        const string endMarker = "</StatusBar>";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "StatusBar is missing.");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, "StatusBar closing element is missing.");
        return source[start..(end + endMarker.Length)];
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
