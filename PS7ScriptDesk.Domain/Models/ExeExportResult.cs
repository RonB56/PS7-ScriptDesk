namespace PS7ScriptDesk.Domain.Models
{
    public class ExeExportResult
    {
        public ExeExportResult(bool succeeded, string outputExecutablePath, string summaryMessage, string detailedLog)
        {
            Succeeded = succeeded;
            OutputExecutablePath = outputExecutablePath;
            SummaryMessage = summaryMessage;
            DetailedLog = detailedLog;
        }

        public bool Succeeded { get; }

        public string OutputExecutablePath { get; }

        public string SummaryMessage { get; }

        public string DetailedLog { get; }
    }
}
