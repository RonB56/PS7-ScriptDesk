using System.IO;

namespace PS7ScriptDesk.Tests;

public sealed class StartupLifecycleTraceTests
{
    [Fact]
    public void StartupTrace_IsBoundedAndWiredAcrossProductionStartupBoundaries()
    {
        var root = FindRepositoryRoot();
        var trace = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Application", "Diagnostics", "StartupLifecycleTrace.cs"));
        var app = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "App.xaml.cs"));
        var bootstrapper = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "Composition", "AppBootstrapper.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs"));
        var terminal = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.PowerShell", "Services", "LiveConsoleService.cs"));
        var terminalControl = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "MainWindow.xaml"));

        Assert.Contains("MaximumFileBytes = 512 * 1024", trace, StringComparison.Ordinal);
        Assert.Contains("MaximumArchives = 2", trace, StringComparison.Ordinal);
        Assert.Contains("RotateIfNeeded", trace, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.ConfigureUiThreadSnapshotProvider", app, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"MainWindow.Window_Loaded\", \"ENTER\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"MainWindow.DeferredStartup\", \"SCHEDULED\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"MainWindowViewModel.InitializeAsync\", \"ENTER\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"MainWindowViewModel.RefreshGitRepositoryAsync\", \"StateReturned\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"LiveConsoleService.StartSessionAsync\", \"ENTER\"", terminal, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"LiveConsoleService.ConPTYProcess\", \"STARTED\"", terminal, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"TerminalControl.WebView2\", \"CORE_READY\"", terminalControl, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"TerminalControl.Xterm\", \"READY\"", terminalControl, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"AppBootstrapper.CreateMainWindow\", \"ENTER\"", bootstrapper, StringComparison.Ordinal);
        Assert.Contains("var args = new PropertyChangedEventArgs(propertyName)", viewModel, StringComparison.Ordinal);
        Assert.Contains("TargetUpdated=\"GitStatusTextBlock_TargetUpdated\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TargetUpdated=\"ConsoleSessionStatusTextBlock_TargetUpdated\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"MainWindow.GitStatusTextBlock\", \"TARGET_UPDATED\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("StartupLifecycleTrace.Write(\"MainWindow.ConsoleSessionStatusTextBlock\", \"TARGET_UPDATED\"", mainWindow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
