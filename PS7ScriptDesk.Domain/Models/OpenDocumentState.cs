using System;

namespace PS7ScriptDesk.Domain.Models
{
    public class OpenDocumentState
    {
        public string FilePath { get; set; } = string.Empty;

        public DateTime? LastKnownWriteTimeUtc { get; set; }

        public long? LastKnownLength { get; set; }
    }
}
