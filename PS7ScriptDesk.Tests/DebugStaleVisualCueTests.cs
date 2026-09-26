using System.IO;

namespace PS7ScriptDesk.Tests;

public sealed class DebugStaleVisualCueTests
{
    [Fact]
    public void TabAndDockedPaneUseAccessibleWarningCueAndThemeResources()
    {
        var app = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var main = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("Binding=\"{Binding IsDebugSourceStale}\"", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Status.Warning.Background", app, StringComparison.Ordinal);
        Assert.Contains("Text=\"⚠ Debug Stale\"", main, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugStaleIndicator\"", main, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Debug Stale\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingPaneContainsTheSameSessionLevelCue()
    {
        var floating = ReadRepositoryFile("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");
        var code = ReadRepositoryFile("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml.cs");

        Assert.Contains("Text=\"⚠ Debug Stale\"", floating, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DebugStaleIndicator\"", floating, StringComparison.Ordinal);
        Assert.Contains("SetDebugStaleIndicator", code, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(segments)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
