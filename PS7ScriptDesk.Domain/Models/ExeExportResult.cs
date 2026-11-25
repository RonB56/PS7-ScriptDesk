namespace PS7ScriptDesk.Domain.Models
{
    public class ExeExportResult
    {
        public ExeExportResult(
            bool succeeded,
            string outputExecutablePath,
            string summaryMessage,
            string detailedLog,
            long outputFileLength = 0,
            string? runtimeIdentifier = null,
            bool wasCancelled = false,
            bool recoveryRequired = false,
            string? recoveryBackupPath = null)
        {
            Succeeded = succeeded;
            OutputExecutablePath = outputExecutablePath;
            SummaryMessage = summaryMessage;
            DetailedLog = detailedLog;
            OutputFileLength = outputFileLength;
            RuntimeIdentifier = runtimeIdentifier ?? string.Empty;
            WasCancelled = wasCancelled;
            RecoveryRequired = recoveryRequired;
            RecoveryBackupPath = recoveryBackupPath ?? string.Empty;
        }

        public bool Succeeded { get; }

        public string OutputExecutablePath { get; }

        public string SummaryMessage { get; }

        public string DetailedLog { get; }

        public long OutputFileLength { get; }

        public string RuntimeIdentifier { get; }

        public bool WasCancelled { get; }

        public bool RecoveryRequired { get; }

        public string RecoveryBackupPath { get; }
    }
}
