namespace PS7ScriptDesk.Application.Interfaces
{
    public sealed record DocumentFileState(bool Exists, DateTime? LastWriteTimeUtc, long? Length)
    {
        public static DocumentFileState Missing { get; } = new(false, null, null);
    }

    public sealed record DocumentFileSnapshot(
        string Content,
        DocumentFileState State,
        string ContentSha256);

    public sealed class DocumentFileChangedException : IOException
    {
        public DocumentFileChangedException(
            string filePath,
            DocumentFileState expectedState,
            DocumentFileState currentState)
            : base($"The destination file changed before the save could be completed: {filePath}")
        {
            FilePath = filePath;
            ExpectedState = expectedState;
            CurrentState = currentState;
        }

        public string FilePath { get; }

        public DocumentFileState ExpectedState { get; }

        public DocumentFileState CurrentState { get; }
    }
}
