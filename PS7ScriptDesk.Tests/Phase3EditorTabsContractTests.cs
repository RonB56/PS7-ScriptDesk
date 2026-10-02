using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase3EditorTabsContractTests
{
    [Fact]
    public void EditorTabs_PreserveDocumentSelectionAndCommandContracts()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var tabControl = document.Descendants(presentation + "TabControl")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding OpenTabs}");

        Assert.Equal("{Binding SelectedTab}", tabControl.Attribute("SelectedItem")?.Value);
        Assert.Equal("{StaticResource IdeCompactEditorTabItemStyle}", tabControl.Attribute("ItemContainerStyle")?.Value);
        Assert.Contains(tabControl.Descendants(presentation + "TextBlock"), element =>
            element.Attribute("Text")?.Value == "{Binding DisplayTitle}" &&
            element.Attribute("ToolTip")?.Value == "{Binding EditorTabToolTip}");

        var closeButton = tabControl.Descendants(presentation + "Button")
            .Single(element => element.Attribute(x + "Name") is null &&
                               element.Attribute("AutomationProperties.Name")?.Value == "Close tab");
        Assert.Equal("{Binding DataContext.CloseTabCommand, ElementName=RootWindow}", closeButton.Attribute("Command")?.Value);
        Assert.Equal("{Binding}", closeButton.Attribute("CommandParameter")?.Value);

        var newTabButton = tabControl.Descendants(presentation + "Button")
            .Single(element => element.Attribute("Command")?.Value.Contains("NewScriptCommand", StringComparison.Ordinal) == true);
        Assert.Equal("{Binding DataContext.NewScriptCommand, RelativeSource={RelativeSource TemplatedParent}}", newTabButton.Attribute("Command")?.Value);
        Assert.Equal("{StaticResource IdeEditorNewTabButtonStyle}", newTabButton.Attribute("Style")?.Value);
    }

    [Fact]
    public void EditorChrome_PreservesDirtyRecoveryStatusAndEditorSurfaceContracts()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");

        Assert.Contains("Style=\"{StaticResource IdeEditorTabTitleStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding IsDirty", app, StringComparison.Ordinal);
        Assert.Contains("Binding IsRecoveredContent", app, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CaretDisplayText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding BreakpointDisplayText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Consolas\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeEditorStatusTextStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Theme.FontSize.StatusText", app, StringComparison.Ordinal);
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
