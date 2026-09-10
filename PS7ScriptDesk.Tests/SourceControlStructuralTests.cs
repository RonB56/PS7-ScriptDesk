namespace PS7ScriptDesk.Tests;

public sealed class SourceControlStructuralTests
{
    [Fact]
    public void LegacySourceControlViewRemainsSafeWhileMainWindowUsesDedicatedWorkspace()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "MainWindow.xaml"));
        var sourceControlView = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "SourceControlView.xaml"));
        var sourceControlWindow = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "SourceControlToolWindow.xaml"));
        var service = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Infrastructure", "Services", "GitService.cs"));
        var runner = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Infrastructure", "Services", "GitCommandRunner.cs"));

        Assert.Contains("Open Git _Workspace", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"SourceControlBottomPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", sourceControlView, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", sourceControlView, StringComparison.Ordinal);
        Assert.Contains("SourceControl.FilterText", sourceControlView, StringComparison.Ordinal);
        Assert.Contains("Theme.Text.Primary", sourceControlView + sourceControlWindow, StringComparison.Ordinal);
        Assert.Contains("--porcelain=v2", service, StringComparison.Ordinal);
        Assert.Contains("-z", service, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorTabViewModel", service, StringComparison.Ordinal);
        Assert.Contains("add", service, StringComparison.Ordinal);
        Assert.Contains("restore", service, StringComparison.Ordinal);
        Assert.Contains("Stage", sourceControlView, StringComparison.Ordinal);
        Assert.Contains("Unstage", sourceControlView, StringComparison.Ordinal);
        Assert.Contains("Discard", sourceControlView, StringComparison.Ordinal);
        Assert.DoesNotContain("reset --hard", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clean -fd", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git commit", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git checkout", service, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ArgumentList.Add", runner, StringComparison.Ordinal);

        Assert.Contains("Value=\"CONFLICTS\"", sourceControlView, StringComparison.Ordinal);
        var sourceControlCode = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "SourceControlView.xaml.cs"));
        Assert.Contains("status.IsConflicted", sourceControlCode, StringComparison.Ordinal);
        Assert.Contains("TryOpenFileFromPath", sourceControlCode, StringComparison.Ordinal);
        Assert.Contains("HasUnresolvedConflicts", File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs")), StringComparison.Ordinal);
    }

    private static string GetRepositoryPath(params string[] pathParts) => Path.Combine(FindRepositoryRoot(), Path.Combine(pathParts));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
