namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IFileDocumentService
    {
        string ReadAllText(string filePath);
        DocumentFileSnapshot ReadSnapshot(string filePath);
        DocumentFileState GetFileState(string filePath);
        void WriteAllText(
            string filePath,
            string content,
            DocumentFileState? expectedDestinationState = null,
            string? operationId = null);
    }
}
