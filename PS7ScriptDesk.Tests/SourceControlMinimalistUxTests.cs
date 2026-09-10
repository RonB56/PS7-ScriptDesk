namespace PS7ScriptDesk.Tests;

public sealed class SourceControlMinimalistUxTests
{
    [Fact]
    public void SourceControlUsesCompactHeaderAndPrimaryRowActions()
    {
        var view = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml");
        var model = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "SourceControlViewModel.cs");

        Assert.DoesNotContain("Text=" + "\"Source Control\"", view, StringComparison.Ordinal);
        Assert.Contains("ChangeSummaryText", view, StringComparison.Ordinal);
        Assert.Contains("HeaderText", view, StringComparison.Ordinal);
        Assert.Contains("Content=\"+\"", view, StringComparison.Ordinal);
        Assert.Contains("Content=\"-\"", view, StringComparison.Ordinal);
        Assert.Contains("Stage this change", view, StringComparison.Ordinal);
        Assert.Contains("Unstage this change", view, StringComparison.Ordinal);
        Assert.Contains("ChangeSummaryText", model, StringComparison.Ordinal);
        Assert.Contains("HeaderText", model, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceControlKeepsContextualDiscardDiffPlaceholderAndVirtualization()
    {
        var view = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "SourceControlView.xaml");

        Assert.Contains("Header=\"Open Diff\"", view, StringComparison.Ordinal);
        Assert.Contains("Header=\"Discard\"", view, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", view, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", view, StringComparison.Ordinal);
        Assert.Contains("Stage All", view, StringComparison.Ordinal);
        Assert.Contains("Unstage All", view, StringComparison.Ordinal);
    }

    [Fact]
    public void GitMenuUsesDedicatedWorkspaceAsThePrimaryGitEntryPoint()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("Open Git _Workspace", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"_Source Control\"", shell, StringComparison.Ordinal);
        Assert.Contains("Refresh Git _Status", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Open Repository _Folder", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Git _Diagnostics", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"_Stage Selected File\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Stage _All\"", shell, StringComparison.Ordinal);
    }
}
