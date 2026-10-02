using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Phase6DebuggerSurfaceContractTests
{
    [Fact]
    public void DebuggerSurfacesUseSharedPresentationStylesWithoutChangingBindingsOrHandlers()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var debug = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");
        var debugCode = Read("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml.cs");
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("x:Key=\"IdeDebuggerTabItemStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDebuggerColumnHeaderStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDebuggerDataGridRowStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDebuggerDataGridStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Selection.Background", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Surface.Hover", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Focus", app, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"DebugTabControl\"", debug, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle=\"{DynamicResource IdeDebuggerTabItemStyle}\"", debug, StringComparison.Ordinal);
        foreach (var grid in new[] { "DebugVariablesGrid", "DebugCallStackGrid", "DebugBreakpointsGrid" })
        {
            var start = debug.IndexOf($"x:Name=\"{grid}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"Debugger grid '{grid}' is missing.");
            var end = debug.IndexOf('>', start);
            Assert.True(end > start, $"Debugger grid '{grid}' declaration is incomplete.");
            Assert.Contains("IdeDebuggerDataGridStyle", debug[start..end], StringComparison.Ordinal);
        }

        foreach (var preserved in new[]
        {
            "SelectionChanged=\"DebugTabControl_SelectionChanged\"",
            "SelectionChanged=\"DebugCallStackGrid_SelectionChanged\"",
            "Click=\"RemoveSelectedBreakpointButton_Click\"",
            "Binding=\"{Binding Name}\"",
            "Binding=\"{Binding IsSelectedInspectionFrame}\"",
            "Binding=\"{Binding IsEnabled, UpdateSourceTrigger=PropertyChanged}\"",
            "AutomationProperties.Name=\"Debugger tool content\"",
            "AutomationProperties.Name=\"Dock debug pane back\""
        })
        {
            Assert.Contains(preserved, debug, StringComparison.Ordinal);
        }
        Assert.Contains("DockBackRequested", debugCode, StringComparison.Ordinal);

        Assert.Contains("Text=\"Debug Output\"", main, StringComparison.Ordinal);
        Assert.Contains("DebugOutputClear_Click", main, StringComparison.Ordinal);
        Assert.Contains("DebugOutputCopyAll_Click", main, StringComparison.Ordinal);
        Assert.Contains("DebugOutputSaveLog_Click", main, StringComparison.Ordinal);
        Assert.Contains("DebugOutputAutoScroll_Checked", main, StringComparison.Ordinal);
        Assert.Contains("DebugOutputFilters_Click", main, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeTitleBarThemeKeepsDefaultFrameAndHasDefensiveDwmFallback()
    {
        var main = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var code = Read("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var helper = Read("PS7ScriptDesk.Shell", "Native", "NativeTitleBarTheme.cs");
        var rootWindowTag = main[..main.IndexOf('>', StringComparison.Ordinal)];

        Assert.DoesNotContain("WindowStyle=\"None\"", rootWindowTag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AllowsTransparency=\"True\"", rootWindowTag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WindowChrome", main, StringComparison.Ordinal);
        Assert.DoesNotContain("HwndSource", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnMouseLeftButtonDown", code, StringComparison.Ordinal);
        Assert.Contains("SourceInitialized=\"Window_SourceInitialized\"", main, StringComparison.Ordinal);
        Assert.Contains("NativeTitleBarTheme.TryApply", code, StringComparison.Ordinal);
        Assert.Contains("DwmSetWindowAttribute", helper, StringComparison.Ordinal);
        Assert.Contains("DwmwaUseImmersiveDarkModeLegacy", helper, StringComparison.Ordinal);
        Assert.Contains("DllNotFoundException", helper, StringComparison.Ordinal);
        Assert.Contains("EntryPointNotFoundException", helper, StringComparison.Ordinal);
        Assert.Contains("default native frame remains active", helper, StringComparison.Ordinal);
        Assert.Contains("ApplyNativeTitleBarTheme(_themeService.CurrentTheme)", code, StringComparison.Ordinal);
        Assert.True(code.Split("ApplyNativeTitleBarTheme(_themeService.CurrentTheme)", StringSplitOptions.None).Length >= 3);
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
