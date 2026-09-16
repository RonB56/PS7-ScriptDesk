namespace PS7ScriptDesk.Tests;

public sealed class GitFeatureDisablementTests
{
    [Fact]
    public void ProductionGitFeatureIsDisabledAtTheShellBoundaryAndLaunchRoutesFailClosed()
    {
        var availability = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "GitFeatureAvailability.cs");
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var code = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("internal static bool IsEnabled => false", availability, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GitMenuItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GitMenuItem.Visibility = GitFeatureAvailability.IsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("GitWorkspaceRejected", code, StringComparison.Ordinal);
        Assert.Contains("commands.RemoveAll(command => command.Id.StartsWith(\"git.\"", code, StringComparison.Ordinal);
    }
}
