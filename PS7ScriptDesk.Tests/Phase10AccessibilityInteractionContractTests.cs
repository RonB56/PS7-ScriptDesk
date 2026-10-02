using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase10AccessibilityInteractionContractTests
{
    [Fact]
    public void SharedInteractionResourcesAndFocusStatesRemainThemeAware()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");

        foreach (var resource in new[]
        {
            "IdeButtonFocusVisualStyle",
            "Theme.Button.HoverBackground",
            "Theme.Button.PressedBackground",
            "Theme.Button.DisabledForeground",
            "Theme.Button.SelectedBackground",
            "Theme.Border.Focus",
            "Theme.Selection.Background",
            "Theme.Text.Disabled"
        })
        {
            Assert.Contains(resource, app, StringComparison.Ordinal);
        }

        Assert.Contains("Property=\"IsMouseOver\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsPressed\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocused\"", app, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocusWithin\"", app, StringComparison.Ordinal);
        Assert.Contains("FocusVisualStyle\" Value=\"{StaticResource IdeButtonFocusVisualStyle}\"", app, StringComparison.Ordinal);
        Assert.Contains("FocusVisualStyle\" Value=\"{StaticResource IdeButtonFocusVisualStyle}\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<ScaleTransform ScaleX=\"1\" ScaleY=\"1\" />", app, StringComparison.Ordinal);
        Assert.DoesNotContain("<EventTrigger RoutedEvent=\"MouseEnter\">", app, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractionFamiliesPreserveSemanticBindingsAndMetadata()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");

        foreach (var preserved in new[]
        {
            "Command=\"{Binding NewScriptCommand}\"",
            "Command=\"{Binding StopCommand}\"",
            "Command=\"{x:Static local:MainWindow.ContinueDebugCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepOverCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepIntoCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepOutCommand}\"",
            "IsChecked=\"{Binding IsContextHelpEnabled, RelativeSource={RelativeSource AncestorType=Window}, Mode=TwoWay}\"",
            "help:ContextHelp.Key=\"Editor.TabStrip\"",
            "help:ContextHelp.Key=\"Debug.Variables\"",
            "help:ContextHelp.Key=\"Debug.CallStack\"",
            "help:ContextHelp.Key=\"Debug.Breakpoints\"",
            "AutomationProperties.Name=\"Close tab\"",
            "ToolTip=\"Close tab\""
        })
        {
            Assert.True(main.Contains(preserved, StringComparison.Ordinal)
                        || bottom.Contains(preserved, StringComparison.Ordinal)
                        || debug.Contains(preserved, StringComparison.Ordinal),
                $"Preserved interaction metadata is missing: {preserved}");
        }

        Assert.DoesNotContain("KeyboardNavigation.TabIndex=\"0\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyboardNavigation.TabIndex=\"0\"", bottom, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyboardNavigation.TabIndex=\"0\"", debug, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemesContinueToProvideTheSemanticInteractionPalette()
    {
        foreach (var theme in new[] { "DarkTheme.xaml", "LightTheme.xaml", "IseBlueTheme.xaml" })
        {
            var xaml = Read("PS7ScriptDesk.Shell", "Themes", theme);
            foreach (var key in new[]
            {
                "Theme.Surface.Hover",
                "Theme.Surface.Pressed",
                "Theme.Border.Focus",
                "Theme.Text.Disabled",
                "Theme.Button.HoverBackground",
                "Theme.Button.PressedBackground",
                "Theme.Button.DisabledForeground",
                "Theme.Button.SelectedBackground",
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
