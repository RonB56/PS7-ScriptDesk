using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class StoreUpdateManualCheckTests
{
    [Fact]
    public async Task StartupNoUpdateThenManualUpdateUsesNewQueryAndCurrentResultWindow()
    {
        var calls = 0;
        var service = CreateService(_ =>
        {
            calls++;
            return Task.FromResult(calls == 2 ? Available() : Empty());
        });

        StoreUpdateStartupState.Begin(service);
        var startup = await service.CheckForUpdatesAsync(CancellationToken.None);
        StoreUpdateStartupState.Complete(service, startup);
        Assert.Equal(StoreUpdateAvailabilityState.NoUpdateAvailable, startup.AvailabilityState);

        Assert.True(StoreUpdateStartupState.TryStartManualCheck(CancellationToken.None, out var firstTask));
        var first = await firstTask!;
        Assert.Same(service, first.Service);
        Assert.Equal(StoreUpdateAvailabilityState.ConfirmedUpdateAvailable, first.Result.AvailabilityState);
        Assert.Equal(2, calls);
        Assert.False(StoreUpdateStartupState.ShouldShowStartupNotification);

        WpfTestDispatcher.Run(() =>
        {
            var window = new StoreUpdateWindow(first.Service, first.Result, isMandatory: false);
            Assert.Contains("update is available", ((TextBlock)window.FindName("MessageTextBlock")).Text,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(((Button)window.FindName("InstallNowButton")).IsEnabled);
        });

        Assert.True(StoreUpdateStartupState.TryStartManualCheck(CancellationToken.None, out var secondTask));
        var second = await secondTask!;
        Assert.Equal(StoreUpdateAvailabilityState.NoUpdateAvailable, second.Result.AvailabilityState);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ManualCheckWaitsForStartupAndRejectsRepeatedClick()
    {
        var startupQuery = new TaskCompletionSource<StoreUpdateQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualQuery = new TaskCompletionSource<StoreUpdateQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = CreateService(_ => ++calls == 1 ? startupQuery.Task : manualQuery.Task);

        StoreUpdateStartupState.Begin(service);
        var startupTask = service.CheckForUpdatesAsync(CancellationToken.None);
        Assert.True(StoreUpdateStartupState.TryStartManualCheck(CancellationToken.None, out var manualTask));
        Assert.False(StoreUpdateStartupState.TryStartManualCheck(CancellationToken.None, out _));
        Assert.Equal(1, calls);

        startupQuery.SetResult(Empty());
        StoreUpdateStartupState.Complete(service, await startupTask);
        await WaitForCallCountAsync(() => calls, 2);
        Assert.False(StoreUpdateStartupState.TryStartManualCheck(CancellationToken.None, out _));

        manualQuery.SetResult(Available());
        var manual = await manualTask!;
        Assert.Equal(StoreUpdateAvailabilityState.ConfirmedUpdateAvailable, manual.Result.AvailabilityState);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationAndQueryFailureNeverBecomeNoUpdate()
    {
        var cancelledService = CreateService(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Empty();
        });
        StoreUpdateStartupState.Begin(cancelledService);
        StoreUpdateStartupState.Complete(cancelledService, await CreateService(_ => Task.FromResult(Empty()))
            .CheckForUpdatesAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.True(StoreUpdateStartupState.TryStartManualCheck(cancellation.Token, out var cancelledTask));
        var cancelled = await cancelledTask!;
        Assert.Equal(StoreUpdateAvailabilityState.UpdateCheckUnavailable, cancelled.Result.AvailabilityState);
        Assert.False(cancelled.Result.StoreUpdateCheckAvailable);

        var failingService = CreateService(_ => Task.FromException<StoreUpdateQueryResult>(
            new InvalidOperationException("Store query failed.")));
        StoreUpdateStartupState.Begin(failingService);
        StoreUpdateStartupState.Complete(failingService, await CreateService(_ => Task.FromResult(Empty()))
            .CheckForUpdatesAsync(CancellationToken.None));
        Assert.True(StoreUpdateStartupState.TryStartManualCheck(CancellationToken.None, out var failedTask));
        var failed = await failedTask!;
        Assert.Equal(StoreUpdateAvailabilityState.UpdateCheckUnavailable, failed.Result.AvailabilityState);
        Assert.False(failed.Result.StoreUpdateCheckAvailable);
    }

    private static StoreUpdateService CreateService(Func<CancellationToken, Task<StoreUpdateQueryResult>> query)
        => new(new StorePackageProvider(), new StoreQuery(query));

    private static StoreUpdateQueryResult Empty()
        => StoreUpdateQueryResult.UpdatesReturned(new object(), Array.Empty<object>(), new List<StoreUpdatePackageInfo>());

    private static StoreUpdateQueryResult Available()
        => StoreUpdateQueryResult.UpdatesReturned(new object(), new[] { new object() },
            new List<StoreUpdatePackageInfo>
            {
                new("31735RonBarnes.PowerShell7.xScriptDesk_wbw8xvvd4njnt", isMandatory: false, version: "1.0.121.0")
            });

    private static async Task WaitForCallCountAsync(Func<int> count, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (count() < expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class StorePackageProvider : IStorePackageEnvironmentProvider
    {
        public StorePackageEnvironmentInfo ReadPackageEnvironment() => new()
        {
            HasPackageIdentity = true,
            PackageIdentityReadSucceeded = true,
            PackageIdentityApi = "Package.Current",
            SignatureKind = "Store",
            PackageName = "31735RonBarnes.PowerShell7.xScriptDesk",
            PackageFamilyName = "31735RonBarnes.PowerShell7.xScriptDesk_wbw8xvvd4njnt",
            PackagePublisherId = "wbw8xvvd4njnt",
            PackageVersion = "1.0.120.0"
        };
    }

    private sealed class StoreQuery(Func<CancellationToken, Task<StoreUpdateQueryResult>> query) : IStoreUpdateQuery
    {
        public Task<StoreUpdateQueryResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
            => query(cancellationToken);
    }
}
