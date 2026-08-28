using System.Windows.Controls;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class StoreUpdateDetectionTests
{
    [Theory]
    [InlineData("Store")]
    [InlineData("3")]
    [InlineData("Windows.ApplicationModel.PackageSignatureKind.Store")]
    public async Task StoreSignedProductionPackageUsesStoreUpdatePath(string signatureKind)
    {
        var service = CreateService(
            StorePackage(signatureKind, isDevelopmentMode: false),
            FakeStoreQuery.Returning(StoreUpdateQueryResult.UpdatesReturned(
                new object(),
                Array.Empty<object>(),
                new List<StoreUpdatePackageInfo>())));

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(result.IsPackaged);
        Assert.True(result.IsStoreManaged);
        Assert.False(result.IsDevelopmentMode);
        Assert.Equal("Store", result.PackageSignatureKind);
        Assert.Equal(StoreUpdatePackagingKind.StoreInstalledManaged, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.NoUpdateAvailable, result.AvailabilityState);
        Assert.True(result.StoreUpdateCheckAvailable);
        Assert.DoesNotContain("sideload", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test package", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeveloperOrTestPackageIsNotTreatedAsStoreManaged()
    {
        var query = FakeStoreQuery.ThrowingIfCalled();
        var service = CreateService(StorePackage("Developer", isDevelopmentMode: true), query);

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(result.IsPackaged);
        Assert.False(result.IsStoreManaged);
        Assert.True(result.IsDevelopmentMode);
        Assert.Equal(StoreUpdatePackagingKind.PackagedDeveloperOrTest, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.ManualCheckRequired, result.AvailabilityState);
        Assert.False(result.StoreUpdateCheckAvailable);
        Assert.True(result.ShouldShowManualInstructions);
        Assert.Contains("developer or test package", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(query.WasCalled);
    }

    [Fact]
    public async Task SideloadedPackageIsDistinguishedWhenSignatureIsNotStoreOrDeveloper()
    {
        var query = FakeStoreQuery.ThrowingIfCalled();
        var service = CreateService(StorePackage("Enterprise", isDevelopmentMode: false), query);

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(result.IsPackaged);
        Assert.False(result.IsStoreManaged);
        Assert.False(result.IsDevelopmentMode);
        Assert.Equal(StoreUpdatePackagingKind.PackagedSideloaded, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.ManualCheckRequired, result.AvailabilityState);
        Assert.False(result.StoreUpdateCheckAvailable);
        Assert.Contains("sideloaded package", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(query.WasCalled);
    }

    [Fact]
    public async Task UnpackagedDevelopmentExecutionSkipsStoreApis()
    {
        var query = FakeStoreQuery.ThrowingIfCalled();
        var service = CreateService(new StorePackageEnvironmentInfo(), query);

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.False(result.IsPackaged);
        Assert.False(result.IsStoreManaged);
        Assert.Equal(StoreUpdatePackagingKind.UnpackagedLocalBuild, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.UpdateCheckUnavailable, result.AvailabilityState);
        Assert.False(result.StoreUpdateCheckAvailable);
        Assert.Contains("unpackaged or local build", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(query.WasCalled);
    }

    [Fact]
    public async Task StorePackageWithUpdateAvailableExposesConfirmedInstallableUpdate()
    {
        var rawContext = new object();
        var rawUpdates = new[] { new object() };
        var service = CreateService(
            StorePackage("Store", isDevelopmentMode: false),
            FakeStoreQuery.Returning(StoreUpdateQueryResult.UpdatesReturned(
                rawContext,
                rawUpdates,
                new List<StoreUpdatePackageInfo> { new("31735RonBarnes.PowerShell7.xScriptDesk_wbw8xvvd4njnt", isMandatory: false) })));

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(result.IsStoreManaged);
        Assert.Equal(StoreUpdateAvailabilityState.ConfirmedUpdateAvailable, result.AvailabilityState);
        Assert.True(result.StoreUpdateCheckAvailable);
        Assert.True(result.HasConfirmedInstallableUpdate);
        Assert.False(result.HasMandatoryUpdate);
        Assert.Equal(1, result.UpdateCount);
        Assert.Same(rawContext, result.RawStoreContext);
        Assert.Same(rawUpdates, result.RawUpdatesCollection);
    }

    [Fact]
    public async Task StoreApiUnavailableKeepsStorePackageClassificationButDisablesInstall()
    {
        var service = CreateService(
            StorePackage("Store", isDevelopmentMode: false),
            FakeStoreQuery.Returning(StoreUpdateQueryResult.Unavailable(
                StoreUpdateAvailabilityState.UpdateCheckUnavailable,
                "Store update APIs were not available at runtime.",
                "StoreContext type was not available.")));

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(result.IsStoreManaged);
        Assert.Equal(StoreUpdatePackagingKind.StoreInstalledManaged, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.UpdateCheckUnavailable, result.AvailabilityState);
        Assert.False(result.StoreUpdateCheckAvailable);
        Assert.False(result.HasConfirmedInstallableUpdate);
        Assert.True(result.ShouldShowManualInstructions);
        Assert.DoesNotContain("sideload", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreUpdateQueryExceptionsReturnUnavailableResultForManualRecovery()
    {
        var service = CreateService(
            StorePackage("Store", isDevelopmentMode: false),
            FakeStoreQuery.Throwing(new InvalidOperationException("Store API failed.")));

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.Equal(StoreUpdatePackagingKind.StoreInstalledManaged, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.UpdateCheckUnavailable, result.AvailabilityState);
        Assert.False(result.StoreUpdateCheckAvailable);
        Assert.True(result.ShouldShowManualInstructions);
        Assert.Contains("InvalidOperationException", result.ExceptionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreUpdateQueryCancellationReturnsUnavailableResultWithoutChangingClassification()
    {
        var service = CreateService(
            StorePackage("Store", isDevelopmentMode: false),
            FakeStoreQuery.Throwing(new OperationCanceledException("Update check canceled.")));

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.True(result.IsStoreManaged);
        Assert.Equal(StoreUpdatePackagingKind.StoreInstalledManaged, result.PackagingKind);
        Assert.Equal(StoreUpdateAvailabilityState.UpdateCheckUnavailable, result.AvailabilityState);
        Assert.False(result.StoreUpdateCheckAvailable);
        Assert.Contains("OperationCanceledException", result.ExceptionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void NonStorePackageDialogDoesNotOfferImpossibleInstallAction()
    {
        RunOnStaThread(() =>
        {
            var service = CreateService(
                StorePackage("Developer", isDevelopmentMode: true),
                FakeStoreQuery.ThrowingIfCalled());

            var window = new StoreUpdateWindow(
                service,
                new StoreUpdateCheckResult
                {
                    PackagingKind = StoreUpdatePackagingKind.PackagedDeveloperOrTest,
                    AvailabilityState = StoreUpdateAvailabilityState.ManualCheckRequired,
                    IsPackaged = true,
                    IsDevelopmentMode = true,
                    PackageSignatureKind = "Developer",
                    StatusMessage = "This appears to be a developer or test package. Microsoft Store automatic update checks are not available for this build."
                },
                isMandatory: false);

            Assert.False(((Button)window.FindName("InstallNowButton")).IsEnabled);
            Assert.Contains("developer or test package", ((TextBlock)window.FindName("MessageTextBlock")).Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static StoreUpdateService CreateService(StorePackageEnvironmentInfo packageEnvironment, IStoreUpdateQuery storeUpdateQuery)
        => new(new FakePackageEnvironmentProvider(packageEnvironment), storeUpdateQuery);

    private static StorePackageEnvironmentInfo StorePackage(string signatureKind, bool isDevelopmentMode)
    {
        return new StorePackageEnvironmentInfo
        {
            HasPackageIdentity = true,
            IsInferredPackagedFallback = true,
            IsDevelopmentMode = isDevelopmentMode,
            SignatureKind = signatureKind,
            PackageFullName = "31735RonBarnes.PowerShell7.xScriptDesk_1.0.71.0_x64__wbw8xvvd4njnt",
            PackageFamilyName = "31735RonBarnes.PowerShell7.xScriptDesk_wbw8xvvd4njnt",
            PackageVersion = "1.0.71.0",
            ProcessPath = @"C:\Program Files\WindowsApps\31735RonBarnes.PowerShell7.xScriptDesk_1.0.71.0_x64__wbw8xvvd4njnt\PS7ScriptDesk.Shell.exe",
            BaseDirectory = @"C:\Program Files\WindowsApps\31735RonBarnes.PowerShell7.xScriptDesk_1.0.71.0_x64__wbw8xvvd4njnt\"
        };
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class FakePackageEnvironmentProvider(StorePackageEnvironmentInfo packageEnvironment) : IStorePackageEnvironmentProvider
    {
        public StorePackageEnvironmentInfo ReadPackageEnvironment() => packageEnvironment;
    }

    private sealed class FakeStoreQuery(Func<CancellationToken, Task<StoreUpdateQueryResult>> handler) : IStoreUpdateQuery
    {
        public bool WasCalled { get; private set; }

        public static FakeStoreQuery Returning(StoreUpdateQueryResult result)
            => new(_ => Task.FromResult(result));

        public static FakeStoreQuery Throwing(Exception exception)
            => new(_ => Task.FromException<StoreUpdateQueryResult>(exception));

        public static FakeStoreQuery ThrowingIfCalled()
            => new(_ => Task.FromException<StoreUpdateQueryResult>(new InvalidOperationException("Store query should not be called.")));

        public Task<StoreUpdateQueryResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return handler(cancellationToken);
        }
    }
}
