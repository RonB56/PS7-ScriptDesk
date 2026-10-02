using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class TopChromeCommandBarConsoleHeaderContractTests
{
    [Fact]
    public void MainWindowRetainsNativeWindowOwnership()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var code = Read("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var windowHeader = main[..main.IndexOf('>')];

        Assert.Contains("Title=\"{Binding Title}\"", windowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowChrome", windowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowsTransparency=\"True\"", windowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowStyle=\"None\"", windowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("DwmSetWindowAttribute", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HwndSource", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandBarPreservesOrderBindingsAndDebugShortcuts()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var commandOrder = new[] { "TextBlock Text=\"New\"", "TextBlock Text=\"Open\"", "TextBlock Text=\"Folder\"", "TextBlock Text=\"Save\"", "TextBlock Text=\"Close\"", "TextBlock Text=\"Close All\"", "TextBlock Text=\"Run\"", "TextBlock Text=\"Run Selection\"", "TextBlock Text=\"Interrupt\"", "TextBlock Text=\"Debug\"", "TextBlock Text=\"Continue\"", "TextBlock Text=\"Step Over\"", "TextBlock Text=\"Step Into\"", "TextBlock Text=\"Step Out\"", "TextBlock Text=\"Clear\"", "TextBlock Text=\"Help\"" };

        var previous = -1;
        foreach (var marker in commandOrder)
        {
            var current = main.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"Command marker '{marker}' is missing or out of order.");
            previous = current;
        }

        foreach (var binding in new[]
        {
            "Command=\"{Binding NewScriptCommand}\"",
            "Command=\"{Binding CloseTabCommand}\"",
            "Command=\"{Binding CloseAllTabsCommand}\"",
            "Command=\"{Binding StopCommand}\"",
            "Command=\"{Binding ClearConsoleCommand}\"",
            "Command=\"{x:Static local:MainWindow.ContinueDebugCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepOverCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepIntoCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepOutCommand}\""
        })
        {
            Assert.Contains(binding, main, StringComparison.Ordinal);
        }

        Assert.Contains("ToolTip=\"Execute the current statement without entering called functions, then pause at the next statement in the current scope. (F10)\"", main, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Enter a called PowerShell function or script when it can be debugged. (F11)\"", main, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Continue until the current function or scope returns to its caller. (Shift+F11)\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolbarUsesSharedHierarchyAndAccessibleStates()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");

        Assert.Contains("x:Key=\"IdeToolbarPrimaryButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarSeparatorStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsMouseOver\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsPressed\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocused\"", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Focus", app, StringComparison.Ordinal);
        Assert.Contains("Value=\"2,0\"", app, StringComparison.Ordinal);
        Assert.Contains("Value=\"10,4\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleUsesOneVisibleHeaderAndPreservesResetAndRuntimeContracts()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Equal(1, Count(main, "x:Name=\"ResetConsoleButton\""));
        Assert.Equal(1, Count(main, "x:Name=\"ConsoleRuntimeVersionTextBlock\""));
        Assert.Contains("Text=\"{Binding EffectiveRuntimeInfo.VersionText, TargetNullValue=PowerShell 7}\"", main, StringComparison.Ordinal);
        Assert.Contains("Content=\"Console\"", main, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestartConsoleCommand}\"", main, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"ResetConsoleButton_PreviewMouseLeftButtonDown\"", main, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ConsoleSessionText, NotifyOnTargetUpdated=True}\"", main, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", main.Substring(main.IndexOf("x:Name=\"ConsoleSessionStatusTextBlock\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("LastRun", main, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Last run", main, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ScriptPath", main, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Console header: session status", main, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

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
