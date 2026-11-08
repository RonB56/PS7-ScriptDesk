using System;

namespace PS7ScriptDesk.Domain.Models
{
    public enum LiveConsoleCommandOutcome
    {
        Completed,
        Submitted,
        PromptObserved,
        RecoveryUnverified,
        Interrupted,
        WriteFailed,
        SessionReplaced,
        SessionTerminated
    }

    public class LiveConsoleCommandResult
    {
        public LiveConsoleCommandResult(
            string displayName,
            bool wasStopped,
            string? currentWorkingDirectory,
            DateTime startedAt,
            DateTime endedAt,
            Guid? requestId = null,
            int sessionGeneration = 0,
            int? processId = null,
            LiveConsoleCommandOutcome outcome = LiveConsoleCommandOutcome.Completed)
        {
            DisplayName = displayName;
            WasStopped = wasStopped;
            CurrentWorkingDirectory = currentWorkingDirectory;
            StartedAt = startedAt;
            EndedAt = endedAt;
            RequestId = requestId;
            SessionGeneration = sessionGeneration;
            ProcessId = processId;
            Outcome = outcome;
        }

        public string DisplayName { get; }

        public bool WasStopped { get; }

        public string? CurrentWorkingDirectory { get; }

        public DateTime StartedAt { get; }

        public DateTime EndedAt { get; }

        public Guid? RequestId { get; }

        public int SessionGeneration { get; }

        public int? ProcessId { get; }

        /// <summary>Separates transport submission from prompt observation and command outcome.</summary>
        public LiveConsoleCommandOutcome Outcome { get; }

        public TimeSpan Duration => EndedAt - StartedAt;
    }

    public sealed record LiveConsoleCommandCompletion(
        Guid RequestId,
        int SessionGeneration,
        int ProcessId,
        LiveConsoleCommandOutcome Outcome,
        long PromptObservationSequence,
        DateTimeOffset ObservedAt);
}
