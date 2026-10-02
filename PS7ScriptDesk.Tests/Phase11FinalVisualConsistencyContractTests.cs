using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase11FinalVisualConsistencyContractTests
{
    [Fact]
    public void AcceptedVisualBaselineKeepsSharedSurfacesAndIntentionalRoleDifferences()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");

        foreach (var resource in new[]
        {
            "IdePaneBorderStyle",
            "IdeHeaderPanelStyle",
            "IdeToolWindowHeaderStyle",
            "IdeEditorTabStripBorderStyle",
            "IdeTabItemStyle",
            "IdeToolbarButtonStyle",
            "IdeBottomPaneTabToggleButtonStyle",
            "IdeModernStatusBarStyle",
            "IdeEmptyPaneTextStyle",
            "IdeButtonFocusVisualStyle",
            "IdeSplitterGripStyle",
            "Theme.Surface.Primary",
            "Theme.Surface.Secondary",
            "Theme.Border.Subtle",
            "Theme.Border.Focus",
            "Theme.Button.HoverBackground",
            "Theme.Button.PressedBackground"
        })
        {
            Assert.Contains(resource, app, StringComparison.Ordinal);
        }

        Assert.Contains("ToolBarTray DockPanel.Dock=\"Top\"", main, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", main, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainToolbar\"", main, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Left\"", main, StringComparison.Ordinal);
        Assert.Contains("Content=\"Console\"", main, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestartConsoleCommand}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeEditorTabStripBorderStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeModernStatusBarStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeBottomPaneTabToggleButtonStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDebuggerTabItemStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDebuggerDataGridStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeRowSplitterStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeColumnSplitterStyle}\"", main, StringComparison.Ordinal);

        // These are intentional role differences, not accidental visual drift.
        Assert.Contains("Width=\"16\"", main, StringComparison.Ordinal); // compact tab close action
        Assert.Contains("Width=\"22\"", main, StringComparison.Ordinal); // compact new-tab action
        Assert.Contains("Width=\"6\"", app, StringComparison.Ordinal); // splitter hit target
        Assert.Contains("Theme.FontSize.StatusText", app, StringComparison.Ordinal); // compact status typography
    }

    [Fact]
    public void FinalBaselinePreservesInteractionMetadataAndAvoidsNewVisualRemnants()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");
        var mainHeader = main[..main.IndexOf('>')];

        foreach (var metadata in new[]
        {
            "help:ContextHelp.Key=\"App.Overview\"",
            "help:ContextHelp.Key=\"Editor.TabStrip\"",
            "help:ContextHelp.Key=\"Console.Area\"",
            "help:ContextHelp.Key=\"Debug.Area\"",
            "help:ContextHelp.Key=\"Debug.Variables\"",
            "help:ContextHelp.Key=\"Debug.CallStack\"",
            "help:ContextHelp.Key=\"Debug.Breakpoints\"",
            "AutomationProperties.Name=\"Close tab\"",
            "ToolTip=\"Close tab\"",
            "ToolTip=\"Turn advanced context help on or off\""
        })
        {
            Assert.True(main.Contains(metadata, StringComparison.Ordinal)
                        || bottom.Contains(metadata, StringComparison.Ordinal)
                        || debug.Contains(metadata, StringComparison.Ordinal),
                $"Accepted interaction metadata is missing: {metadata}");
        }

        foreach (var prohibited in new[]
        {
            "WindowChrome",
            "AllowsTransparency=\"True\"",
            "WindowStyle=\"None\"",
            "DwmSetWindowAttribute",
            "ToolbarRight",
            "ToolbarFiller",
            "LastRun",
            "Last run",
            "ScriptPath"
        })
        {
            Assert.DoesNotContain(prohibited, mainHeader, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Background=\"#", app + main + bottom + debug, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Foreground=\"#", app + main + bottom + debug, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<EventTrigger RoutedEvent=\"MouseEnter\">", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThemesRetainTheSharedSurfaceAndInteractionPalette()
    {
        foreach (var theme in new[] { "DarkTheme.xaml", "LightTheme.xaml", "IseBlueTheme.xaml" })
        {
            var xaml = Read("PS7ScriptDesk.Shell", "Themes", theme);
            foreach (var key in new[]
            {
                "Theme.Surface.Primary",
                "Theme.Surface.Secondary",
                "Theme.Surface.Tertiary",
                "Theme.Surface.Hover",
                "Theme.Surface.Pressed",
                "Theme.Border.Subtle",
                "Theme.Border.Focus",
                "Theme.Text.Primary",
                "Theme.Text.Secondary",
                "Theme.Text.Disabled",
                "Theme.Button.HoverBackground",
                "Theme.Button.PressedBackground",
                "Theme.Button.DisabledForeground",
                "Theme.Selection.Background"
            })
            {
                Assert.Contains($"x:Key=\"{key}\"", xaml, StringComparison.Ordinal);
            }
        }
    }

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(new[] { directory?.FullName ?? throw new DirectoryNotFoundException() }.Concat(parts).ToArray()));
    }
}
