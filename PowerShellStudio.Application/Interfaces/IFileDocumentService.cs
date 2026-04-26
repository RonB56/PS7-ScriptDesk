namespace PowerShellStudio.Application.Interfaces
{
    public interface IFileDocumentService
    {
        string ReadAllText(string filePath);
        void WriteAllText(string filePath, string content);
    }
}