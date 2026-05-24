using System.IO;
using System.Text;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Infrastructure.Services
{
    public class FileDocumentService : IFileDocumentService
    {
        public string ReadAllText(string filePath)
        {
            return File.ReadAllText(filePath);
        }

        public void WriteAllText(string filePath, string content)
        {
            var directoryPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"The folder '{directoryPath ?? filePath}' does not exist.");
            }

            File.WriteAllText(filePath, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
