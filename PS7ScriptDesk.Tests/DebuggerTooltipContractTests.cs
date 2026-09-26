using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class DebuggerTooltipContractTests
{
    [Fact]
    public void DebuggerToolbarTooltipsDescribeActionsAndShortcuts()
    {
        var xaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("Continue running until the next breakpoint or until the script finishes. (F5)", xaml, StringComparison.Ordinal);
        Assert.Contains("Execute the current statement without entering called functions, then pause at the next statement in the current scope. (F10)", xaml, StringComparison.Ordinal);
        Assert.Contains("Enter a called PowerShell function or script when it can be debugged. (F11)", xaml, StringComparison.Ordinal);
        Assert.Contains("Continue until the current function or scope returns to its caller. (Shift+F11)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=\"Continue (F5)\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=\"Step Over (F10)\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=\"Step Into (F11)\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=\"Step Out (Shift+F11)\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DebuggerInspectionTooltipsStateCurrentScopeAndNavigationTruthfully()
    {
        var mainWindow = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var floatingPane = ReadRepositoryFile("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");

        Assert.Contains("Show read-only variables for the current paused execution scope.", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Show the active call stack and navigate to available source frames.", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Show read-only variables for the current paused execution scope.", floatingPane, StringComparison.Ordinal);
        Assert.Contains("Show the active call stack and navigate to available source frames.", floatingPane, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine([directory.FullName, .. relativeSegments]);
        Assert.True(File.Exists(path), $"Expected repository file was not found: {path}");
        return File.ReadAllText(path);
    }
}
