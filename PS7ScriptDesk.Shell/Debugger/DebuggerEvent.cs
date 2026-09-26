using System;

namespace PS7ScriptDesk.Shell.Debug
{
    public enum DebuggerEventCategory
    {
        DebuggerLifecycle,
        Breakpoint,
        Step,
        ScriptOutput,
        HostOutput,
        Warning,
        Verbose,
        Debug,
        Information,
        Error,
        NativeStdout,
        NativeStderr,
        Exception,
        AdapterWarning,
        Protocol
    }

    public enum DebuggerEventSeverity
    {
        Trace,
        Information,
        Warning,
        Error
    }

    public enum DebuggerPauseReason
    {
        Breakpoint,
        Step,
        Prompt,
        Exception,
        ManualBreak,
        ProtocolPause,
        Unknown
    }

    public enum DebuggerTerminationReason
    {
        SessionEndedMarker,
        ConfirmedProcessExit,
        BoundedTeardown,
        NormalCompletion,
        UserStop,
        TerminatingException,
        ProtocolFailure,
        ChildProcessExit,
        StartupFailure,
        Cancellation,
        TransportFailure,
        UnknownFailure,
        Unknown
    }

    public sealed record DebuggerEvent
    {
        public const int MaxDisplayTextLength = 4096;
        public const int MaxDetailsLength = 8192;

        public DebuggerEvent(
            Guid sessionId,
            DateTimeOffset timestampUtc,
            long sequence,
            DebuggerEventCategory category,
            DebuggerEventSeverity severity,
            string source,
            string displayText,
            string? filePath = null,
            int? lineNumber = null,
            string? details = null,
            bool isNavigable = false,
            bool isExpandable = false,
            DebuggerPauseReason? pauseReason = null,
            DebuggerTerminationReason? terminationReason = null,
            int? processId = null)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A debugger session identity is required.", nameof(sessionId));
            if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));

            SessionId = sessionId;
            TimestampUtc = timestampUtc.ToUniversalTime();
            Sequence = sequence;
            Category = category;
            Severity = severity;
            Source = RequireBounded(source, nameof(source), 128);
            DisplayText = Bound(displayText, MaxDisplayTextLength);
            FilePath = BoundOptional(filePath, 1024);
            LineNumber = lineNumber is > 0 ? lineNumber : null;
            Details = BoundOptional(details, MaxDetailsLength);
            IsNavigable = isNavigable && FilePath is not null && LineNumber is not null;
            IsExpandable = isExpandable && Details is not null;
            PauseReason = pauseReason;
            TerminationReason = terminationReason;
            ProcessId = processId is > 0 ? processId : null;
        }

        public Guid SessionId { get; }
        public DateTimeOffset TimestampUtc { get; }
        public long Sequence { get; }
        public DebuggerEventCategory Category { get; }
        public DebuggerEventSeverity Severity { get; }
        public string Source { get; }
        public string DisplayText { get; }
        public string? FilePath { get; }
        public int? LineNumber { get; }
        public string? Details { get; }
        public bool IsNavigable { get; }
        public bool IsExpandable { get; }
        public DebuggerPauseReason? PauseReason { get; }
        public DebuggerTerminationReason? TerminationReason { get; }
        public int? ProcessId { get; }

        public DebuggerEvent WithPresentation(string displayText, bool isNavigable)
            => new(
                SessionId,
                TimestampUtc,
                Sequence,
                Category,
                Severity,
                Source,
                displayText,
                FilePath,
                LineNumber,
                Details,
                isNavigable,
                IsExpandable,
                PauseReason,
                TerminationReason,
                ProcessId);

        private static string RequireBounded(string? value, string parameterName, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", parameterName);
            return Bound(value, maxLength);
        }

        private static string? BoundOptional(string? value, int maxLength)
            => string.IsNullOrEmpty(value) ? null : Bound(value, maxLength);

        private static string Bound(string? value, int maxLength)
        {
            var normalized = value ?? string.Empty;
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength];
        }
    }
}
