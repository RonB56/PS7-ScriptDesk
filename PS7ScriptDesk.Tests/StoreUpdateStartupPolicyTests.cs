namespace PS7ScriptDesk.Tests;

public sealed class StoreUpdateStartupPolicyTests
{
    [Fact]
    public void ProductionUpdater_UsesDirectWinRtStoreApiAndSingleStartupCheck()
    {
        var serviceSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Services",
            "StoreUpdateService.cs");
        var appSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "App.xaml.cs");
        var mainWindowSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");
        var mainWindowXaml = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml");

        Assert.Contains("using Windows.Services.Store;", serviceSource, StringComparison.Ordinal);
        Assert.Contains("StoreContext.GetDefault()", serviceSource, StringComparison.Ordinal);
        Assert.Contains("GetAppAndOptionalStorePackageUpdatesAsync", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionStoreUpdateQuery", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreContextTypeName", serviceSource, StringComparison.Ordinal);

        Assert.Contains("_ = CheckForStoreUpdatesAfterStartupAsync(shellWindow);", appSource, StringComparison.Ordinal);
        Assert.Contains("StoreUpdateStartupState.Begin", appSource, StringComparison.Ordinal);
        Assert.Contains("StoreUpdateStartupState.Complete", appSource, StringComparison.Ordinal);

        Assert.Contains("StoreUpdateStartupState.Read()", mainWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual Store/MSIX update check requested from Help", mainWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("await storeUpdateService.CheckForUpdatesAsync", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("Store Update _Status", mainWindowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreProjects_TargetWindowsSdkNeededForWinRtProjection()
    {
        var shellProject = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "PS7ScriptDesk.Shell.csproj");
        var testsProject = ReadRepositoryFile(
            "PS7ScriptDesk.Tests",
            "PS7ScriptDesk.Tests.csproj");

        Assert.Contains("net10.0-windows10.0.26100.0", shellProject, StringComparison.Ordinal);
        Assert.Contains("<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>", shellProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-windows10.0.26100.0", testsProject, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", Path.Combine(segments)));
        return File.ReadAllText(path);
    }
}
