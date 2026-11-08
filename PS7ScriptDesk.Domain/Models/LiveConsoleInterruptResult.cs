using System;

namespace PS7ScriptDesk.Domain.Models
{
    public enum LiveConsoleInterruptOutcome
    {
        NotAttempted,
        SignalDelivered,
        RecoveredAtPrompt,
        TimedOutUnverified,
        Failed,
        SessionTerminated,
        SessionReplaced
    }

    public sealed class LiveConsoleInterruptResult
    {
        public LiveConsoleInterruptResult(
            bool interruptAttempted,
            bool completedGracefully,
            bool escalationRequired,
            bool processTerminationSucceeded,
            bool sessionRestarted,
            int? ownedProcessId,
            TimeSpan gracefulTimeout,
            LiveConsoleInterruptOutcome outcome = LiveConsoleInterruptOutcome.NotAttempted,
            bool signalDelivered = false)
        {
            InterruptAttempted = interruptAttempted;
            CompletedGracefully = completedGracefully;
            EscalationRequired = escalationRequired;
            ProcessTerminationSucceeded = processTerminationSucceeded;
            SessionRestarted = sessionRestarted;
            OwnedProcessId = ownedProcessId;
            GracefulTimeout = gracefulTimeout;
            Outcome = outcome;
            SignalDelivered = signalDelivered;
        }

        public bool InterruptAttempted { get; }

        public bool RequestAccepted => InterruptAttempted;

        /// <summary>True only when the ETX write completed successfully.</summary>
        public bool SignalDelivered { get; }

        public bool CompletedGracefully { get; }

        /// <summary>A prompt candidate was observed after ETX; this does not prove script success.</summary>
        public bool InteractiveReadinessObserved => Outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt;

        /// <summary>True only when process exit was observed, not when a prompt-shaped output was seen.</summary>
        public bool ExecutionTerminationConfirmed => Outcome == LiveConsoleInterruptOutcome.SessionTerminated;

        public bool EscalationRequired { get; }

        public bool ProcessTerminationSucceeded { get; }

        public bool SessionRestarted { get; }

        public int? OwnedProcessId { get; }

        public TimeSpan GracefulTimeout { get; }

        public LiveConsoleInterruptOutcome Outcome { get; }
    }
}
