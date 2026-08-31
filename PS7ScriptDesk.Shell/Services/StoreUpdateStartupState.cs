using System;

namespace PS7ScriptDesk.Shell.Services
{
    /// <summary>
    /// Process-local snapshot of the single Microsoft Store update check performed after startup.
    /// Help > Update Status reads this snapshot and never initiates a second Store query.
    /// </summary>
    internal static class StoreUpdateStartupState
    {
        private static readonly object Sync = new();
        private static StoreUpdateService? _service;
        private static StoreUpdateCheckResult? _result;
        private static bool _checkInProgress;
        private static DateTimeOffset? _checkedAt;

        public static void Begin(StoreUpdateService service)
        {
            ArgumentNullException.ThrowIfNull(service);

            lock (Sync)
            {
                _service = service;
                _result = null;
                _checkInProgress = true;
                _checkedAt = null;
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
    }

    internal sealed record StoreUpdateStartupSnapshot(
        StoreUpdateService? Service,
        StoreUpdateCheckResult? Result,
        bool CheckInProgress,
        DateTimeOffset? CheckedAt);
}
