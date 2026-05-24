using System;

namespace PS7ScriptDesk.Domain.Models
{
    public enum ExecutionOutputStreamKind
    {
        Lifecycle,
        StandardOutput,
        StandardError
    }

    public class ExecutionOutputRecord
    {
        public ExecutionOutputRecord(ExecutionOutputStreamKind streamKind, string text, DateTime timestamp)
        {
            StreamKind = streamKind;
            Text = text;
            Timestamp = timestamp;
        }

        public ExecutionOutputStreamKind StreamKind { get; }

        public string Text { get; }

        public DateTime Timestamp { get; }
    }
}
