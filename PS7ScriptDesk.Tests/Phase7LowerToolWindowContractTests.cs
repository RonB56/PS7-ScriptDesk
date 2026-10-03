using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase7LowerToolWindowContractTests
{
    [Fact]
    public void LowerToolPresentationUsesSharedStylesAndPreservesTabOrderAndActions()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");

        foreach (var style in new[]
        {
            "IdeLowerToolWindowHeaderStyle",
            "IdeLowerToolListBoxItemStyle",
            "IdeLowerToolProblemItemButtonStyle",
            "IdeLowerToolActivityTextBoxStyle"
        })
        {
            Assert.Contains($"x:Key=\"{style}\"", app, StringComparison.Ordinal);
        }

        var problems = main.IndexOf("x:Name=\"BottomProblemsToolTab\"", StringComparison.Ordinal);
        var debugOutput = main.IndexOf("x:Name=\"BottomDebugOutputToolTab\"", StringComparison.Ordinal);
        var activity = main.IndexOf("x:Name=\"BottomActivityToolTab\"", StringComparison.Ordinal);
        Assert.True(problems >= 0 && debugOutput > problems && activity > debugOutput, "Lower tool tab order changed.");

        Assert.Contains("Style=\"{StaticResource IdeLowerToolWindowHeaderStyle}\"", main, StringComparison.Ordinal);
        foreach (var tabName in new[] { "BottomProblemsToolTab", "BottomDebugOutputToolTab", "BottomActivityToolTab" })
        {
            Assert.Contains("Style=\"{StaticResource IdeBottomPaneTabToggleButtonStyle}\"", ExtractElement(main, $"x:Name=\"{tabName}\""), StringComparison.Ordinal);
        }
        Assert.Contains("Style=\"{StaticResource IdeLowerToolProblemItemButtonStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle=\"{StaticResource IdeLowerToolListBoxItemStyle}\"", main, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeLowerToolActivityTextBoxStyle}\"", main, StringComparison.Ordinal);

        foreach (var preserved in new[]
        {
            "Click=\"BottomProblemsToolTab_Click\"",
            "Click=\"BottomDebugOutputToolTab_Click\"",
            "Click=\"BottomActivityToolTab_Click\"",
            "Click=\"PopOutBottomToolWindowButton_Click\"",
            "Click=\"DockBottomToolWindowButton_Click\"",
            "Click=\"HideBottomToolWindowButton_Click\"",
            "Click=\"SyntaxDiagnosticItem_Click\"",
            "Click=\"DebugOutputClear_Click\"",
            "Click=\"DebugOutputCopyAll_Click\"",
            "Click=\"DebugOutputSaveLog_Click\"",
            "Checked=\"DebugOutputAutoScroll_Checked\"",
            "Unchecked=\"DebugOutputAutoScroll_Unchecked\"",
            "Click=\"DebugOutputFilters_Click\"",
            "ItemsSource=\"{Binding SelectedTab.SyntaxErrors}\"",
            "Text=\"{Binding ApplicationActivityText, Mode=OneWay}\"",
            "AutomationProperties.Name=\"Pop out Problems, Debug Output, and Activity\"",
            "AutomationProperties.Name=\"Dock back Problems, Debug Output, and Activity\""
        })
        {
            Assert.Contains(preserved, main, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FloatingBottomToolWindowRetainsSharedChromeAndDockBackContract()
    {
        var bottom = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml");
        var code = Read("PS7ScriptDesk.Shell", "BottomToolWindow.xaml.cs");
        var dockableCode = Read("PS7ScriptDesk.Shell", "DockableToolWindow.cs");

        Assert.Contains("Style=\"{DynamicResource IdeToolWindowStyle}\"", bottom, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource IdeLowerToolWindowHeaderStyle}\"", bottom, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Problems, Debug Output, and Activity\"", bottom, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Floating tool window content\"", bottom, StringComparison.Ordinal);
        Assert.Contains("SetToolContent", code, StringComparison.Ordinal);
        Assert.Contains(": DockableToolWindow", code, StringComparison.Ordinal);
        Assert.Contains("CloseForDockBack", dockableCode, StringComparison.Ordinal);
        Assert.Contains("DockBackRequested?.Invoke(this, EventArgs.Empty);", dockableCode, StringComparison.Ordinal);
        Assert.Contains("CloseForOwnerShutdown", dockableCode, StringComparison.Ordinal);
    }

    private static string ExtractElement(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing element marker '{marker}'.");
        var end = source.IndexOf(" />", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Incomplete element for '{marker}'.");
        return source[start..end];
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
