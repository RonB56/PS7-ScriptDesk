using System;

namespace PS7ScriptDesk.Domain.Models
{
    public sealed class LiveConsoleInterruptResult
    {
        public LiveConsoleInterruptResult(
            bool interruptAttempted,
            bool completedGracefully,
            bool escalationRequired,
            bool processTerminationSucceeded,
            bool sessionRestarted,
            int? ownedProcessId,
            TimeSpan gracefulTimeout)
        {
            InterruptAttempted = interruptAttempted;
            CompletedGracefully = completedGracefully;
            EscalationRequired = escalationRequired;
            ProcessTerminationSucceeded = processTerminationSucceeded;
            SessionRestarted = sessionRestarted;
            OwnedProcessId = ownedProcessId;
            GracefulTimeout = gracefulTimeout;
        }

        public bool InterruptAttempted { get; }

        public bool CompletedGracefully { get; }

        public bool EscalationRequired { get; }

        public bool ProcessTerminationSucceeded { get; }

        public bool SessionRestarted { get; }

        public int? OwnedProcessId { get; }

        public TimeSpan GracefulTimeout { get; }
    }
}
