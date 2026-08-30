using System;
using System.Linq;

namespace PS7ScriptDesk.Shell.Services
{
    internal sealed class UnhandledExceptionStormGate
    {
        private readonly object _syncRoot = new();
        private readonly TimeSpan _repeatWindow;
        private readonly Func<DateTimeOffset> _clock;
        private ActiveFault? _activeFault;
        private bool _presentationInProgress;

        public UnhandledExceptionStormGate()
            : this(TimeSpan.FromSeconds(30), () => DateTimeOffset.Now)
        {
        }

        public UnhandledExceptionStormGate(TimeSpan repeatWindow, Func<DateTimeOffset> clock)
        {
            if (repeatWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(repeatWindow));
            }

            _repeatWindow = repeatWindow;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public UnhandledExceptionPresentationDecision TryBeginPresentation(string source, Exception exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            var now = _clock();
            var signature = CreateSignature(source, exception);
            lock (_syncRoot)
            {
                if (_presentationInProgress)
                {
                    var reentrantCount = RecordSuppressedOccurrence(signature, now);
                    return UnhandledExceptionPresentationDecision.Suppress(
                        signature,
                        now,
                        reentrantCount,
                        "A runtime exception notification is already being presented.");
                }

                if (_activeFault is not null &&
                    string.Equals(_activeFault.Signature, signature, StringComparison.Ordinal) &&
                    now - _activeFault.FirstSeen <= _repeatWindow)
                {
                    _activeFault.Count++;
                    _activeFault.LastSeen = now;
                    return UnhandledExceptionPresentationDecision.Suppress(
                        signature,
                        now,
                        _activeFault.Count,
                        "An equivalent runtime exception was already reported in the active fault window.");
                }

                _activeFault = new ActiveFault(signature, now);
                _presentationInProgress = true;
                return UnhandledExceptionPresentationDecision.Present(signature, now);
            }
        }

        public UnhandledExceptionPresentationSummary EndPresentation(string signature)
        {
            lock (_syncRoot)
            {
                _presentationInProgress = false;
                if (_activeFault is null ||
                    !string.Equals(_activeFault.Signature, signature, StringComparison.Ordinal))
                {
                    return new UnhandledExceptionPresentationSummary(signature, 1, _clock(), _clock());
                }

                return new UnhandledExceptionPresentationSummary(
                    _activeFault.Signature,
                    _activeFault.Count,
                    _activeFault.FirstSeen,
                    _activeFault.LastSeen);
            }
        }

        private int RecordSuppressedOccurrence(string signature, DateTimeOffset now)
        {
            if (_activeFault is not null &&
                string.Equals(_activeFault.Signature, signature, StringComparison.Ordinal))
            {
                _activeFault.Count++;
                _activeFault.LastSeen = now;
                return _activeFault.Count;
            }

            return 1;
        }

        private static string CreateSignature(string source, Exception exception)
        {
            var stackFrame = exception.StackTrace?
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .FirstOrDefault() ?? "(no-stack)";
            return string.Join(
                "|",
                source ?? string.Empty,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message ?? string.Empty,
                stackFrame);
        }

        private sealed class ActiveFault
        {
            public ActiveFault(string signature, DateTimeOffset firstSeen)
            {
                Signature = signature;
                FirstSeen = firstSeen;
                LastSeen = firstSeen;
                Count = 1;
            }

            public string Signature { get; }

            public DateTimeOffset FirstSeen { get; }

            public DateTimeOffset LastSeen { get; set; }

            public int Count { get; set; }
        }
    }

    internal readonly record struct UnhandledExceptionPresentationDecision(
        bool ShouldPresent,
        string Signature,
        DateTimeOffset OccurredAt,
        int OccurrenceCount,
        string? SuppressionReason)
    {
        public static UnhandledExceptionPresentationDecision Present(string signature, DateTimeOffset occurredAt)
            => new(true, signature, occurredAt, 1, null);

        public static UnhandledExceptionPresentationDecision Suppress(
            string signature,
            DateTimeOffset occurredAt,
            int occurrenceCount,
            string suppressionReason)
            => new(false, signature, occurredAt, occurrenceCount, suppressionReason);
    }

    internal readonly record struct UnhandledExceptionPresentationSummary(
        string Signature,
        int OccurrenceCount,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen);
}
