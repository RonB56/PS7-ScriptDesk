using System;

namespace PS7ScriptDesk.Domain.Models
{
    public class LiveConsoleCommandResult
    {
        public LiveConsoleCommandResult(
            string displayName,
            bool wasStopped,
            string? currentWorkingDirectory,
            DateTime startedAt,
            DateTime endedAt)
        {
            DisplayName = displayName;
            WasStopped = wasStopped;
            CurrentWorkingDirectory = currentWorkingDirectory;
            StartedAt = startedAt;
            EndedAt = endedAt;
        }

        public string DisplayName { get; }

        public bool WasStopped { get; }

        public string? CurrentWorkingDirectory { get; }

        public DateTime StartedAt { get; }

        public DateTime EndedAt { get; }

        public TimeSpan Duration => EndedAt - StartedAt;
    }
}
