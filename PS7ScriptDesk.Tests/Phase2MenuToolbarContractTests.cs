using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase2MenuToolbarContractTests
{
    [Fact]
    public void MainWindow_PreservesTopLevelMenuAndToolbarCommandContracts()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var menu = document.Descendants(presentation + "Menu").Single(element => element.Attribute(XName.Get("DockPanel.Dock"))?.Value == "Top");
        Assert.Equal(8, menu.Elements(presentation + "MenuItem").Count());

        var toolbar = document.Descendants(presentation + "ToolBar").Single(element => element.Attribute(x + "Name")?.Value == "MainToolbar");
        Assert.Equal(16, toolbar.Descendants(presentation + "Button").Count());

        foreach (var contract in new[]
        {
            "Command=\"{Binding NewScriptCommand}\"",
            "Click=\"OpenFile_Click\"",
            "Command=\"{Binding OpenWorkspaceFolderCommand}\"",
            "Click=\"SaveFile_Click\"",
            "Command=\"{Binding CloseTabCommand}\"",
            "Command=\"{Binding CloseAllTabsCommand}\"",
            "Click=\"RunScript_Click\"",
            "Click=\"RunSelection_Click\"",
            "Command=\"{Binding StopCommand}\"",
            "Click=\"DebugToggle_Click\"",
            "Command=\"{x:Static local:MainWindow.ContinueDebugCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepOverCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepIntoCommand}\"",
            "Command=\"{x:Static local:MainWindow.StepOutCommand}\"",
            "Command=\"{Binding ClearConsoleCommand}\"",
            "Click=\"HelpOverview_Click\"",
            "InputGestureText=\"Ctrl+F5\"",
            "InputGestureText=\"F8\"",
            "InputGestureText=\"F5\"",
            "InputGestureText=\"F10\"",
            "InputGestureText=\"F11\"",
            "InputGestureText=\"Shift+F11\"",
            "InputGestureText=\"Shift+F5\""
        })
        {
            Assert.Contains(contract, xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedMenuAndToolbarStylesUseThePhase2VisualFoundation()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");

        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"36\" />", app, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Padding.Toolbar}\" />", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Separator", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Focus", app, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"{StaticResource Radius.Popup}\"", app, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"0\" />", app, StringComparison.Ordinal);
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
