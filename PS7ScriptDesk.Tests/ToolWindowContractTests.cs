using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class ToolWindowContractTests
{
    [Fact]
    public void SharedToolWindowResourcesExistAndUseSemanticThemeKeys()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        foreach (var key in new[] { "IdeToolWindowStyle", "IdeToolWindowHeaderStyle", "IdeToolWindowHeaderTextStyle", "IdeToolWindowContentBorderStyle" })
        {
            Assert.Equal(1, Count(app, $"x:Key=\"{key}\""));
        }

        Assert.Contains("Theme.App.Background", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Surface.Primary", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Subtle", app, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingToolWindowsRetainFrameSizingContentAndKeyboardContracts()
    {
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");
            Assert.Contains("Style=\"{DynamicResource IdeToolWindowStyle}\"", bottom, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"360\"", bottom, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"220\"", bottom, StringComparison.Ordinal);
        Assert.Contains("<ContentControl x:Name=\"ToolContentHost\"", bottom, StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown=\"Window_PreviewKeyDown\"", bottom, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Floating tool window content\"", bottom, StringComparison.Ordinal);

        var bottomCode = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml.cs");
        Assert.Contains("CloseForDockBack", bottomCode, StringComparison.Ordinal);
        Assert.Contains("CloseForOwnerShutdown", bottomCode, StringComparison.Ordinal);
        Assert.Contains("DockBackRequested?.Invoke(this, EventArgs.Empty);", bottomCode, StringComparison.Ordinal);
        Assert.Contains("SetToolContent(UIElement content)", bottomCode, StringComparison.Ordinal);
        Assert.Contains("ClearToolContent()", bottomCode, StringComparison.Ordinal);

        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");
        Assert.Contains("Style=\"{DynamicResource IdeToolWindowStyle}\"", debug, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"320\"", debug, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"260\"", debug, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Floating Debug Pane\"", debug, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Debugger tool content\"", debug, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Dock debug pane back\"", debug, StringComparison.Ordinal);

        var debugCode = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml.cs");
        Assert.Contains("CloseForDockBack", debugCode, StringComparison.Ordinal);
        Assert.Contains("CloseForOwnerShutdown", debugCode, StringComparison.Ordinal);
        Assert.Contains("DockBackRequested?.Invoke(this, EventArgs.Empty);", debugCode, StringComparison.Ordinal);
        Assert.Contains("SelectedTabIndexChanged?.Invoke", debugCode, StringComparison.Ordinal);
        Assert.Contains("RemoveSelectedBreakpointRequested?.Invoke", debugCode, StringComparison.Ordinal);
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
