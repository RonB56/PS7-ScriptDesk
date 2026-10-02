using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase8StatusBarContractTests
{
    [Fact]
    public void StatusBarUsesSharedCompactPresentationWithoutHardCodedColors()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var statusBar = ExtractStatusBar(Read("PS7ScriptDesk.Shell", "MainWindow.xaml"));

        foreach (var style in new[]
        {
            "IdeModernStatusBarStyle",
            "IdeStatusBarTextStyle",
            "IdeStatusBarLongTextStyle",
            "IdeStatusBarGroupStartItemStyle",
            "IdeStatusBarSeparatorStyle"
        })
        {
            Assert.Contains($"x:Key=\"{style}\"", app, StringComparison.Ordinal);
        }

        Assert.Contains("Style=\"{StaticResource IdeModernStatusBarStyle}\"", statusBar, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeStatusBarTextStyle}\"", statusBar, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeStatusBarGroupStartItemStyle}\"", statusBar, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeStatusBarLongTextStyle}\"", statusBar, StringComparison.Ordinal);
        Assert.True(Count(statusBar, "Style=\"{StaticResource IdeStatusBarSeparatorStyle}\"") >= 12);
        Assert.DoesNotContain("Background=\"#", statusBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", statusBar, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBarPreservesFieldOrderBindingsAndInteractiveMetadata()
    {
        var statusBar = ExtractStatusBar(Read("PS7ScriptDesk.Shell", "MainWindow.xaml"));
        var fields = new[]
        {
            "Text=\"{Binding VersionText}\"",
            "Text=\"{Binding StatusText}\"",
            "Text=\"{Binding ConsoleInputStatusText}\"",
            "Text=\"{Binding SessionRestoreNoticeText}\"",
            "Text=\"{Binding SelectedTab.CaretDisplayText, TargetNullValue='Ln 1, Col 1'}\"",
            "Text=\"{Binding SelectedTab.EditorMetricsText, TargetNullValue=Lines: 1}\"",
            "Text=\"{Binding SelectedTab.SelectionDisplayText, TargetNullValue=Selection: None}\"",
            "Text=\"{Binding SelectedTab.BreakpointDisplayText, TargetNullValue=Breakpoints: None}\"",
            "Text=\"{Binding RuntimeText}\"",
            "Text=\"{Binding WorkspaceText}\"",
            "Text=\"{Binding GitStatusText, NotifyOnTargetUpdated=True}\"",
            "Text=\"{Binding ZoomLevelText}\"",
            "Text=\"{Binding UiScaleText}\"",
            "Text=\"{Binding ExecutionProgressText}\"",
            "Text=\"Editor metadata loading...\""
        };

        var previous = -1;
        foreach (var field in fields)
        {
            var current = statusBar.IndexOf(field, StringComparison.Ordinal);
            Assert.True(current > previous, $"Status field ordering or binding changed: {field}");
            previous = current;
        }

        foreach (var preserved in new[]
        {
            "help:ContextHelp.Key=\"Status.Version\"",
            "help:ContextHelp.Key=\"Status.Caret\"",
            "help:ContextHelp.Key=\"Status.Lines\"",
            "help:ContextHelp.Key=\"Status.Selection\"",
            "help:ContextHelp.Key=\"Status.Breakpoints\"",
            "help:ContextHelp.Key=\"Status.Runtime\"",
            "help:ContextHelp.Key=\"Status.Workspace\"",
            "help:ContextHelp.Key=\"Status.Git\"",
            "help:ContextHelp.Key=\"Status.UiScale\"",
            "help:ContextHelp.Key=\"Help.Context\"",
            "TargetUpdated=\"GitStatusTextBlock_TargetUpdated\"",
            "ToolTip=\"{Binding GitRepositoryRootText}\"",
            "ToolTip=\"Application interface scale. Editor Zoom and terminal font size are separate settings.\"",
            "ToolTip=\"Turn advanced context help on or off\"",
            "IsChecked=\"{Binding IsContextHelpEnabled, RelativeSource={RelativeSource AncestorType=Window}, Mode=TwoWay}\"",
            "StatusBarHelpToggleButtonStyle"
        })
        {
            Assert.Contains(preserved, statusBar, StringComparison.Ordinal);
        }
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
