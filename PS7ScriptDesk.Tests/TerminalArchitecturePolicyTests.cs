namespace PS7ScriptDesk.Tests;

public sealed class TerminalArchitecturePolicyTests
{
    [Fact]
    public void TerminalWiring_HasNoGenericApplicationTextSinkIntoXterm()
    {
        var viewModelSource = ReadRepositoryFile(
            "PS7ScriptDesk.UI",
            "ViewModels",
            "MainWindowViewModel.cs");
        var shellSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");

        Assert.DoesNotContain("SetTerminalSinks", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_writeTextSink", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("SetTerminalSessionControls", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.WriteRaw(raw)", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.WriteDebuggerOutput(text)", shellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalHotPaths_DoNotContainSynchronousTranscriptAppendCode()
    {
        var liveConsoleSource = ReadRepositoryFile(
            "PS7ScriptDesk.PowerShell",
            "Services",
            "LiveConsoleService.cs");
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.DoesNotContain("File.AppendAllText", liveConsoleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.AppendAllText", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalCaptureState", liveConsoleSource, StringComparison.Ordinal);
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
