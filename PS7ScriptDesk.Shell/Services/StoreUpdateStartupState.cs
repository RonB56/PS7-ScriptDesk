using System;
using System.Threading;
using System.Threading.Tasks;

namespace PS7ScriptDesk.Shell.Services
{
    /// <summary>
    /// Coordinates the startup check with fresh, user-initiated Store queries.
    /// </summary>
    internal static class StoreUpdateStartupState
    {
        private static readonly object Sync = new();
        private static StoreUpdateService? _service;
        private static StoreUpdateCheckResult? _result;
        private static bool _checkInProgress;
        private static DateTimeOffset? _checkedAt;
        private static TaskCompletionSource<bool>? _startupCompletion;
        private static bool _manualCheckInProgress;
        private static bool _manualCheckStarted;

        public static void Begin(StoreUpdateService service)
        {
            ArgumentNullException.ThrowIfNull(service);

            lock (Sync)
            {
                _service = service;
                _result = null;
                _checkInProgress = true;
                _checkedAt = null;
                _startupCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public static void Complete(StoreUpdateService service, StoreUpdateCheckResult result)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(result);

            lock (Sync)
            {
                _service = service;
                _result = result;
                _checkInProgress = false;
                _checkedAt = DateTimeOffset.Now;
                _startupCompletion?.TrySetResult(true);
            }
        }

        public static void Fail(StoreUpdateService service, StoreUpdateCheckResult result)
        {
            Complete(service, result);
        }

        public static StoreUpdateStartupSnapshot Read()
        {
            lock (Sync)
            {
                return new StoreUpdateStartupSnapshot(
                    _service,
                    _result,
                    _checkInProgress,
                    _checkedAt);
            }
        }

        public static bool ShouldShowStartupNotification
        {
            get
            {
                lock (Sync)
                {
                    return !_manualCheckStarted;
                }
            }
        }

        public static bool TryStartManualCheck(
            CancellationToken cancellationToken,
            out Task<StoreUpdateManualCheckResult>? checkTask)
        {
            lock (Sync)
            {
                if (_manualCheckInProgress)
                {
                    checkTask = null;
                    return false;
                }

                _manualCheckInProgress = true;
                _manualCheckStarted = true;
                var service = _service ?? new StoreUpdateService();
                var startupCompletion = _startupCompletion?.Task ?? Task.CompletedTask;
                checkTask = RunManualCheckAsync(service, startupCompletion, cancellationToken);
                return true;
            }
        }

        private static async Task<StoreUpdateManualCheckResult> RunManualCheckAsync(
            StoreUpdateService service,
            Task startupCompletion,
            CancellationToken cancellationToken)
        {
            try
            {
                await startupCompletion.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await service.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                return new StoreUpdateManualCheckResult(service, result);
            }
            finally
            {
                lock (Sync)
                {
                    _manualCheckInProgress = false;
                }
            }
        }
    }

    internal sealed record StoreUpdateStartupSnapshot(
        StoreUpdateService? Service,
        StoreUpdateCheckResult? Result,
        bool CheckInProgress,
        DateTimeOffset? CheckedAt);

    internal sealed record StoreUpdateManualCheckResult(StoreUpdateService Service, StoreUpdateCheckResult Result);
}
