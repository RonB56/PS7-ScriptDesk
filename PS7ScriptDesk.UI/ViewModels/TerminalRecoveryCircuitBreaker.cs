using System;
using System.Collections.Generic;

namespace PS7ScriptDesk.UI.ViewModels
{
    public sealed class TerminalRecoveryCircuitBreaker
    {
        private readonly object _syncRoot = new();
        private readonly Queue<DateTimeOffset> _attempts = new();
        private readonly int _maxAttempts;
        private readonly TimeSpan _window;
        private readonly TimeSpan _baseBackoff;
        private readonly TimeSpan _maxBackoff;
        private readonly Func<DateTimeOffset> _clock;
        private bool _automaticRecoveryPaused;
        private bool _unavailableNotificationIssued;

        public TerminalRecoveryCircuitBreaker()
            : this(
                maxAttempts: 3,
                window: TimeSpan.FromSeconds(30),
                baseBackoff: TimeSpan.FromMilliseconds(250),
                maxBackoff: TimeSpan.FromSeconds(2),
                clock: () => DateTimeOffset.UtcNow)
        {
        }

        public TerminalRecoveryCircuitBreaker(
            int maxAttempts,
            TimeSpan window,
            TimeSpan baseBackoff,
            TimeSpan maxBackoff,
            Func<DateTimeOffset> clock)
        {
            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }

            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            if (baseBackoff < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(baseBackoff));
            }

            if (maxBackoff < baseBackoff)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBackoff));
            }

            _maxAttempts = maxAttempts;
            _window = window;
            _baseBackoff = baseBackoff;
            _maxBackoff = maxBackoff;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public TerminalRecoveryDecision TryBeginAutomaticRestart()
        {
            var now = _clock();
            lock (_syncRoot)
            {
                PruneExpiredAttempts(now);
                if (_automaticRecoveryPaused || _attempts.Count >= _maxAttempts)
                {
                    _automaticRecoveryPaused = true;
                    var shouldNotify = !_unavailableNotificationIssued;
                    _unavailableNotificationIssued = true;
                    return TerminalRecoveryDecision.Blocked(
                        _attempts.Count,
                        _maxAttempts,
                        shouldNotify,
                        "Automatic terminal recovery reached the bounded retry limit.");
                }

                _attempts.Enqueue(now);
                var attemptNumber = _attempts.Count;
                return TerminalRecoveryDecision.Allowed(
                    attemptNumber,
                    _maxAttempts,
                    CalculateBackoff(attemptNumber));
            }
        }

        public void ResetForManualRetry()
        {
            lock (_syncRoot)
            {
                _attempts.Clear();
                _automaticRecoveryPaused = false;
                _unavailableNotificationIssued = false;
            }
        }

        private void PruneExpiredAttempts(DateTimeOffset now)
        {
            while (_attempts.Count > 0 && now - _attempts.Peek() > _window)
            {
                _attempts.Dequeue();
            }
        }

        private TimeSpan CalculateBackoff(int attemptNumber)
        {
            if (attemptNumber <= 1 || _baseBackoff == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var multiplier = Math.Min(attemptNumber - 1, 6);
            var candidate = TimeSpan.FromMilliseconds(_baseBackoff.TotalMilliseconds * multiplier);
            return candidate <= _maxBackoff ? candidate : _maxBackoff;
        }
    }

    public readonly record struct TerminalRecoveryDecision(
        bool IsAllowed,
        int AttemptCount,
        int MaxAttempts,
        TimeSpan Backoff,
        bool ShouldNotifyUnavailable,
        string? BlockReason)
    {
        public static TerminalRecoveryDecision Allowed(
            int attemptCount,
            int maxAttempts,
            TimeSpan backoff)
            => new(true, attemptCount, maxAttempts, backoff, false, null);

        public static TerminalRecoveryDecision Blocked(
            int attemptCount,
            int maxAttempts,
            bool shouldNotifyUnavailable,
            string blockReason)
            => new(false, attemptCount, maxAttempts, TimeSpan.Zero, shouldNotifyUnavailable, blockReason);
    }
}
