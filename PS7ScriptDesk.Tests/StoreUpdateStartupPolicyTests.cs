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
        var bannerSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "StoreUpdateBanner.xaml.cs");
        var updateWindowSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "StoreUpdateWindow.xaml.cs");

        Assert.Contains("using Windows.Services.Store;", serviceSource, StringComparison.Ordinal);
        Assert.Contains("StoreContext.GetDefault()", serviceSource, StringComparison.Ordinal);
        Assert.Contains("GetAppAndOptionalStorePackageUpdatesAsync", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionStoreUpdateQuery", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreContextTypeName", serviceSource, StringComparison.Ordinal);

        Assert.Contains("_ = CheckForStoreUpdatesAfterStartupAsync(shellWindow);", appSource, StringComparison.Ordinal);
        Assert.Contains("StoreUpdateStartupState.Begin", appSource, StringComparison.Ordinal);
        Assert.Contains("StoreUpdateStartupState.Complete", appSource, StringComparison.Ordinal);
        Assert.Contains("result.HasMandatoryUpdate = false", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("result.HasMandatoryUpdate = queryResult.Updates.Any(update => update.IsMandatory)", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current?.Shutdown(0)", appSource, StringComparison.Ordinal);
        Assert.Contains("RegisterApplicationRestart", serviceSource, StringComparison.Ordinal);
        Assert.Contains("RestartFlags.None", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartFlags.NoPatch", serviceSource, StringComparison.Ordinal);
        Assert.Contains("MinimumRestartLifetime", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Shutdown(0)", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryShutdownAfterSuccessfulInstall", appSource, StringComparison.Ordinal);

        var restartSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Services",
            "StoreUpdateRestartCoordinator.cs");
        var shellProject = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "PS7ScriptDesk.Shell.csproj");
        Assert.Contains("Microsoft.WindowsAppSDK.Foundation", shellProject, StringComparison.Ordinal);
        Assert.Contains("AppInstance.Restart", restartSource, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange", restartSource, StringComparison.Ordinal);
        Assert.Contains("Package.Current", restartSource, StringComparison.Ordinal);
        Assert.Contains("PackageVersionAdvanced", restartSource, StringComparison.Ordinal);

        Assert.Contains("may close PS7 ScriptDesk and automatically restart it", bannerSource, StringComparison.Ordinal);
        Assert.Contains("may close PS7 ScriptDesk and automatically restart it", updateWindowSource, StringComparison.Ordinal);
        Assert.Contains("You can postpone this update", bannerSource, StringComparison.Ordinal);
        Assert.Contains("You can postpone the update", updateWindowSource, StringComparison.Ordinal);
        Assert.Contains("Update installed successfully. PS7 ScriptDesk will restart automatically.", bannerSource, StringComparison.Ordinal);
        Assert.Contains("Update installed successfully. PS7 ScriptDesk will restart automatically.", updateWindowSource, StringComparison.Ordinal);
        Assert.Contains("Please reopen PS7 ScriptDesk", bannerSource, StringComparison.Ordinal);
        Assert.Contains("Please reopen PS7 ScriptDesk", updateWindowSource, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(StoreUpdateRestartCoordinator.RestartNotificationDisplayDuration)", bannerSource, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(StoreUpdateRestartCoordinator.RestartNotificationDisplayDuration)", updateWindowSource, StringComparison.Ordinal);
        Assert.Contains("ShowStoreUpdateSuccessStatus", appSource, StringComparison.Ordinal);
        Assert.Contains("PS7 ScriptDesk has been updated successfully", mainWindowSource, StringComparison.Ordinal);

        var bannerNoticeIndex = bannerSource.IndexOf("Update installed successfully. PS7 ScriptDesk will restart automatically.", StringComparison.Ordinal);
        var bannerRestartIndex = bannerSource.IndexOf("TryRestartAfterSuccessfulInstall", StringComparison.Ordinal);
        Assert.True(bannerNoticeIndex >= 0 && bannerNoticeIndex < bannerRestartIndex);
        var windowNoticeIndex = updateWindowSource.IndexOf("Update installed successfully. PS7 ScriptDesk will restart automatically.", StringComparison.Ordinal);
        var windowRestartIndex = updateWindowSource.IndexOf("TryRestartAfterSuccessfulInstall", StringComparison.Ordinal);
        Assert.True(windowNoticeIndex >= 0 && windowNoticeIndex < windowRestartIndex);

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
        => TestRepositoryPaths.ReadFile(segments);
}
