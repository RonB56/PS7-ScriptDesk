using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase9EmptyStatesCommandBarHostContractTests
{
    [Fact]
    public void ExistingEmptyStatesUseSharedPresentationWithoutChangingTriggersOrMessages()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");

        Assert.Contains("x:Key=\"IdeEmptyPaneTextStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeEmptyPaneContextTextStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"Opacity\" Value=\"0.82\"", app, StringComparison.Ordinal);

        foreach (var message in new[]
        {
            "Variables: no active frame",
            "No variables available.",
            "No active call stack.",
            "No breakpoints configured.",
            "No problems for the active document.",
            "No debugger output yet."
        })
        {
            Assert.Contains($"Text=\"{message}\"", main + debug, StringComparison.Ordinal);
        }

        foreach (var trigger in new[]
        {
            "Binding=\"{Binding HasItems, ElementName=DebugVariablesGrid}\"",
            "Binding=\"{Binding HasItems, ElementName=DebugCallStackGrid}\"",
            "Binding=\"{Binding HasItems, ElementName=DebugBreakpointsGrid}\"",
            "Binding=\"{Binding SelectedTab.HasDiagnostics}\"",
            "Binding=\"{Binding IsChecked, ElementName=BottomDebugOutputToolTab}\""
        })
        {
            Assert.Contains(trigger, main + debug, StringComparison.Ordinal);
        }

        Assert.Contains("Style=\"{StaticResource IdeEmptyPaneTextStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource IdeEmptyPaneContextTextStyle}\"", debug, StringComparison.Ordinal);
        Assert.Contains("help:ContextHelp.Key=\"Debug.Variables\"", debug, StringComparison.Ordinal);
        Assert.Contains("help:ContextHelp.Key=\"Debug.CallStack\"", main, StringComparison.Ordinal);
        Assert.Contains("help:ContextHelp.Key=\"Debug.Breakpoints\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandBarHostFillsWidthWithoutAddingControlsOrChangingCommandOrder()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var toolbar = Extract(main, "<ToolBarTray DockPanel.Dock=\"Top\"", "</ToolBarTray>");

        Assert.Contains("Background\" Value=\"{DynamicResource Theme.Surface.Secondary}\"", app, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", toolbar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainToolbar\"", toolbar, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Left\"", toolbar, StringComparison.Ordinal);

        var commands = new[]
        {
            "Text=\"New\"", "Text=\"Open\"", "Text=\"Folder\"", "Text=\"Save\"",
            "Text=\"Close\"", "Text=\"Close All\"", "Text=\"Run\"", "Text=\"Run Selection\"",
            "Text=\"Interrupt\"", "Text=\"Debug\"", "Text=\"Continue\"", "Text=\"Step Over\"",
            "Text=\"Step Into\"", "Text=\"Step Out\"", "Text=\"Clear\"", "Text=\"Help\""
        };
        var previous = -1;
        foreach (var command in commands)
        {
            var current = toolbar.IndexOf(command, StringComparison.Ordinal);
            Assert.True(current > previous, $"Command order changed: {command}");
            previous = current;
        }

        Assert.DoesNotContain("ToolbarRight", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolbarFiller", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolbarStatus", toolbar, StringComparison.Ordinal);
        Assert.Contains("Theme.Surface.Secondary", app, StringComparison.Ordinal);
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker '{startMarker}'.");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker '{endMarker}'.");
        return source[start..(end + endMarker.Length)];
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
