using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase4PaneSystemContractTests
{
    [Fact]
    public void DockedPaneHeadersReuseSharedPresentationResources()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("Style=\"{StaticResource IdeHeaderPanelStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeToolWindowHeaderTextStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Content=\"Console\"", main, StringComparison.Ordinal);
        Assert.Contains("Text=\"Problems / Debug Output / Activity\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void PaneCommandsAndBindingsRemainWired()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("Command=\"{Binding RestartConsoleCommand}\"", main, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"ResetConsoleButton_PreviewMouseLeftButtonDown\"", main, StringComparison.Ordinal);
        Assert.Contains("Click=\"PopOutBottomToolWindowButton_Click\"", main, StringComparison.Ordinal);
        Assert.Contains("Click=\"DockBottomToolWindowButton_Click\"", main, StringComparison.Ordinal);
        Assert.Contains("Click=\"HideBottomToolWindowButton_Click\"", main, StringComparison.Ordinal);
        Assert.Contains("Click=\"BottomProblemsToolTab_Click\"", main, StringComparison.Ordinal);
        Assert.Contains("Click=\"BottomDebugOutputToolTab_Click\"", main, StringComparison.Ordinal);
        Assert.Contains("Click=\"BottomActivityToolTab_Click\"", main, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ConsoleSessionText, NotifyOnTargetUpdated=True}\"", main, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedTab.SyntaxErrorSummaryText, TargetNullValue=No active document}\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void DockingAndSplitterContractsRemainPresent()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");

        foreach (var name in new[]
        {
            "ConsolePaneBorder", "BottomToolWindowBorder", "BottomToolWindowSplitter",
            "DebugPanelBorder", "BottomToolWindowPopOutButton", "BottomToolWindowDockBackButton"
        })
        {
            Assert.Contains($"x:Name=\"{name}\"", main, StringComparison.Ordinal);
        }

        Assert.Contains("Click=\"DockBackButton_Click\"", debug, StringComparison.Ordinal);
        var dockableCode = Read("PS7ScriptDesk.Shell", "DockableToolWindow.cs");
        Assert.Contains("DockBackRequested?.Invoke(this, EventArgs.Empty);", dockableCode, StringComparison.Ordinal);
        Assert.Contains("ToolContentHost", bottom, StringComparison.Ordinal);
    }

    [Fact]
    public void PaneResourcesUseThemeAwarePresentationValues()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");

        Assert.Contains("x:Key=\"IdeHeaderPanelStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowHeaderStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolWindowContentBorderStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Surface.Secondary", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Subtle", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Focus", app, StringComparison.Ordinal);

        foreach (var theme in new[] { "LightTheme.xaml", "DarkTheme.xaml", "IseBlueTheme.xaml" })
        {
            var source = Read("PS7ScriptDesk.Shell", "Themes", theme);
            Assert.Contains("Theme.Surface.Elevated", source, StringComparison.Ordinal);
            Assert.Contains("Theme.Border.Focus", source, StringComparison.Ordinal);
            Assert.Contains("Theme.Text.Disabled", source, StringComparison.Ordinal);
        }
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
