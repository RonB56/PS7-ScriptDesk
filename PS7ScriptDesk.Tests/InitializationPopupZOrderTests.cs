namespace PS7ScriptDesk.Tests;

public sealed class InitializationPopupZOrderTests
{
    [Fact]
    public void InitializationProgressUsesOwnedWindowAcrossWebView2AirspaceBoundary()
    {
        var mainWindowXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainWindowSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var popupSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "MetadataInitializationWindow.xaml.cs");
        var popupXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MetadataInitializationWindow.xaml");
        var terminalSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");

        Assert.DoesNotContain("MetadataToastHost", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("MetadataInitializationWindow", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("ShowForOwner(this)", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("Owner = owner", popupSource, StringComparison.Ordinal);
        Assert.Contains("PointToScreen", popupSource, StringComparison.Ordinal);
        Assert.Contains("TransformFromDevice", popupSource, StringComparison.Ordinal);
        Assert.Contains("WindowStyle=\"None\"", popupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Topmost", popupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BringWindowToTop", popupSource, StringComparison.Ordinal);
        Assert.Contains("new WebView2", terminalSource, StringComparison.Ordinal);
        Assert.Contains("WebViewHost.Children.Insert", terminalSource, StringComparison.Ordinal);
    }

    [Fact]
    public void InitializationPopupDoesNotUseZOrderPollingOrGlobalTopmost()
    {
        var popupSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "MetadataInitializationWindow.xaml.cs");
        var mainWindowSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.DoesNotContain("DispatcherTimer", popupSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowPos", popupSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BringWindowToTop", mainWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Topmost = true", mainWindowSource, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
        => TestRepositoryPaths.ReadFile(segments);
}
