namespace PS7ScriptDesk.Tests;

public sealed class DebugOutputTrueBottomStructuralTests
{
    [Fact]
    public void DebugOutput_UsesFilteredViewAndBoundedTrueBottomSettle()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("_debugOutputPresentation.VisibleItems).CollectionChanged", source, StringComparison.Ordinal);
        Assert.Contains("ScrollIntoView(newestVisibleItem)", source, StringComparison.Ordinal);
        Assert.Contains("settledScrollViewer?.ScrollToEnd()", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ContextIdle", source, StringComparison.Ordinal);
        Assert.Contains("_debugOutputScrollOperationPending", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugOutput_FollowGateRejectsOnlyWhenTheLifecyclePredicateIsFalse()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains(
            "if (!_debugOutputFollowing ||\n                !DebugOutputAutoScrollLifecycle.ShouldRequestScroll(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DebugOutput_OrdinaryRowsAreCompactAndSessionBoundariesRemainDistinct()
    {
        var xaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var debugPane = xaml[(xaml.IndexOf("x:Name=\"DebugOutputBottomPane\"", StringComparison.Ordinal))..xaml.IndexOf("x:Name=\"ActivityBottomPane\"", StringComparison.Ordinal)];

        Assert.Contains("<Setter Property=\"Padding\" Value=\"2,0\" />", debugPane, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"0\" />", debugPane, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"1,1\" />", debugPane, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"3,3\" />", debugPane, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness=\"0,0,0,1\"", debugPane, StringComparison.Ordinal);
        Assert.Contains("Binding IsSessionBoundary", debugPane, StringComparison.Ordinal);
        Assert.Contains("Binding IsExpandable", debugPane, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
