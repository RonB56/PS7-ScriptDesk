using System;
using System.IO;

namespace PS7ScriptDesk.Domain.Models
{
    public enum DocumentLanguage
    {
        PlainText,
        PowerShell
    }

    public static class DocumentLanguageClassifier
    {
        public static DocumentLanguage ClassifyPath(string? path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            return extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".psm1", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".psd1", StringComparison.OrdinalIgnoreCase)
                ? DocumentLanguage.PowerShell
                : DocumentLanguage.PlainText;
        }
    }
}
