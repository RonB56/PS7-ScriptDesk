using System.Windows.Threading;
using System.Runtime.InteropServices;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class StoreUpdateInstallTests
{
    [Fact]
    public void ZeroOwnerHwndIsRejectedBeforeInteractiveAdapterRuns()
    {
        RecordingInstallAdapter? recordingAdapter = null;
        RunOnStaThread(() =>
        {
            recordingAdapter = new RecordingInstallAdapter
            {
                Attempt = new StoreUpdateService.StoreUpdateInstallAttempt
                {
                    RequestInvoked = true,
                    StoreContextInitialized = true
                }
            };
            var service = CreateService(recordingAdapter);
            var result = service.RequestInstallAsync(CreateInstallableCheckResult(), IntPtr.Zero, null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.False(result.RequestStarted);
            Assert.False(recordingAdapter.WasCalled);
            Assert.Contains("valid HWND", result.ExceptionSummary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ContextInitializationAndRequestOrderingIsRepresentedByInstallAdapter()
    {
        RecordingInstallAdapter? adapter = null;
        RunOnStaThread(() =>
        {
            _ = Dispatcher.CurrentDispatcher;
            adapter = new RecordingInstallAdapter
            {
                Attempt = new StoreUpdateService.StoreUpdateInstallAttempt
                {
                    RequestInvoked = true,
                    StoreContextInitialized = true,
                    OverallState = "Started"
                }
            };
            var service = CreateService(adapter);
            var result = service.RequestInstallAsync(CreateInstallableCheckResult(), new IntPtr(42), null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.RequestStarted);
            Assert.True(result.StoreContextInitialized);
            Assert.Equal(new IntPtr(42), adapter.OwnerHwnd);
            Assert.Equal(new[] { "initialize", "request" }, adapter.Events);
        });
    }

    [Fact]
    public void ExceptionWithObservedPausedStateIsReturnedAsPartialStartAndIsNotRetriedByService()
    {
        RecordingInstallAdapter? adapter = null;
        RunOnStaThread(() =>
        {
            _ = Dispatcher.CurrentDispatcher;
            adapter = new RecordingInstallAdapter
            {
                Attempt = new StoreUpdateService.StoreUpdateInstallAttempt
                {
                    RequestInvoked = true,
                    StoreContextInitialized = true,
                    PackageStatuses = new[]
                    {
                        new StoreUpdateInstallStatusInfo("family", "Paused", "10%", "Total=10%", string.Empty, "Progress", string.Empty, string.Empty)
                    },
                    Exception = new COMException("Invalid window handle.", unchecked((int)0x80070578))
                }
            };
            var service = CreateService(adapter);
            var result = service.RequestInstallAsync(CreateInstallableCheckResult(), new IntPtr(42), null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.RequestStarted);
            Assert.True(result.PartialStartDetected);
            Assert.Contains(result.PackageStatuses, status => status.PackageUpdateState == "Paused");
            Assert.Contains("0x80070578", result.ExceptionSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, adapter.CallCount);
        });
    }

    [Fact]
    public void RestartRegistrationFailurePreventsStoreInstall()
    {
        RunOnStaThread(() =>
        {
            var adapter = new RecordingInstallAdapter();
            var restart = new RecordingRestartRegistration { RegisterResult = false, ErrorCode = unchecked((int)0x80004005) };
            var service = CreateService(adapter, restart);
            var result = service.RequestInstallAsync(CreateInstallableCheckResult(), new IntPtr(42), null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.False(result.RequestStarted);
            Assert.False(adapter.WasCalled);
            Assert.True(restart.RegisterCalled);
            Assert.Equal(0, restart.UnregisterCount);
            Assert.Contains("automatic restart", result.ExceptionSummary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void SuccessfulInstallKeepsRestartRegistrationForWindowsToRelaunchUpdatedPackage()
    {
        RunOnStaThread(() =>
        {
            var restart = new RecordingRestartRegistration();
            var adapter = new RecordingInstallAdapter
            {
                Attempt = new StoreUpdateService.StoreUpdateInstallAttempt
                {
                    RequestInvoked = true,
                    StoreContextInitialized = true,
                    OverallState = "Completed"
                }
            };
            var service = CreateService(adapter, restart);
            var result = service.RequestInstallAsync(CreateInstallableCheckResult(), new IntPtr(42), null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.IsSuccessfulInstallation);
            Assert.True(result.RestartRegistrationSucceeded);
            Assert.Equal(0, restart.UnregisterCount);
        });
    }

    [Fact]
    public void CanceledInstallUnregistersRestartIntentAndDoesNotRequestRelaunch()
    {
        RunOnStaThread(() =>
        {
            var restart = new RecordingRestartRegistration();
            var adapter = new RecordingInstallAdapter
            {
                Attempt = new StoreUpdateService.StoreUpdateInstallAttempt
                {
                    RequestInvoked = true,
                    StoreContextInitialized = true,
                    OverallState = "Canceled"
                }
            };
            var service = CreateService(adapter, restart);
            var result = service.RequestInstallAsync(CreateInstallableCheckResult(), new IntPtr(42), null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.False(result.IsSuccessfulInstallation);
            Assert.Equal(1, restart.UnregisterCount);
        });
    }

    private static StoreUpdateService CreateService(
        StoreUpdateService.IStoreUpdateInstallAdapter adapter,
        RecordingRestartRegistration? restart = null)
        => new(new FakePackageEnvironmentProvider(), new FakeStoreQuery(), adapter, restart ?? new RecordingRestartRegistration());

    private static StoreUpdateCheckResult CreateInstallableCheckResult()
        => new()
        {
            PackagingKind = StoreUpdatePackagingKind.StoreInstalledManaged,
            AvailabilityState = StoreUpdateAvailabilityState.ConfirmedUpdateAvailable,
            RawStoreContext = new object(),
            RawUpdatesCollection = new object(),
            UpdateCount = 1,
            Updates = new List<StoreUpdatePackageInfo> { new("family", isMandatory: true) }
        };

    private static void RunOnStaThread(Action action)
        => WpfTestDispatcher.Run(action);

    private sealed class FakePackageEnvironmentProvider : IStorePackageEnvironmentProvider
    {
        public StorePackageEnvironmentInfo ReadPackageEnvironment() => new();
    }

    private sealed class FakeStoreQuery : IStoreUpdateQuery
    {
        public Task<StoreUpdateQueryResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
            => Task.FromResult(StoreUpdateQueryResult.Unavailable(StoreUpdateAvailabilityState.UpdateCheckUnavailable, string.Empty, string.Empty));
    }

    private sealed class RecordingInstallAdapter : StoreUpdateService.IStoreUpdateInstallAdapter
    {
        public StoreUpdateService.StoreUpdateInstallAttempt Attempt { get; init; } = new();
        public List<string> Events { get; } = new();
        public IntPtr OwnerHwnd { get; private set; }
        public int CallCount { get; private set; }
        public bool WasCalled => CallCount > 0;

        public Task<StoreUpdateService.StoreUpdateInstallAttempt> RequestAsync(
            object rawStoreContext,
            object rawUpdatesCollection,
            IntPtr ownerHwnd,
            IProgress<StoreUpdateInstallProgressInfo>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OwnerHwnd = ownerHwnd;
            Events.Add("initialize");
            Events.Add("request");
            return Task.FromResult(Attempt);
        }
    }

    private sealed class RecordingRestartRegistration : StoreUpdateService.IStoreUpdateRestartRegistration
    {
        public bool RegisterResult { get; init; } = true;
        public int ErrorCode { get; init; }
        public bool RegisterCalled { get; private set; }
        public int UnregisterCount { get; private set; }

        public bool TryRegister(out int errorCode)
        {
            RegisterCalled = true;
            errorCode = ErrorCode;
            return RegisterResult;
        }

        public void Unregister() => UnregisterCount++;
    }
}
