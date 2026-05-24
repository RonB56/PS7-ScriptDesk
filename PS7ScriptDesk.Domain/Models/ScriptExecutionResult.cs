using System;

namespace PS7ScriptDesk.Domain.Models
{
    public class ScriptExecutionResult
    {
        public ScriptExecutionResult(
            string runtimeDisplayName,
            int exitCode,
            bool wasStopped,
            string snapshotPath,
            DateTime startedAt,
            DateTime endedAt)
        {
            RuntimeDisplayName = runtimeDisplayName;
            ExitCode = exitCode;
            WasStopped = wasStopped;
            SnapshotPath = snapshotPath;
            StartedAt = startedAt;
            EndedAt = endedAt;
        }

        public string RuntimeDisplayName { get; }

        public int ExitCode { get; }

        public bool WasStopped { get; }

        public string SnapshotPath { get; }

        public DateTime StartedAt { get; }

        public DateTime EndedAt { get; }

        public TimeSpan Duration => EndedAt - StartedAt;
    }
}
