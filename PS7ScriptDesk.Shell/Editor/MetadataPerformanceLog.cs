using System;
using System.Globalization;
using System.IO;
using System.Text;

using PS7ScriptDesk.Application.Utilities;
namespace PS7ScriptDesk.Shell.Editor
{
    internal static class MetadataPerformanceLog
    {
        private const string LogFilePrefix = "metadata-performance-";
        private const string LogFileExtension = ".log";

        public static string CreateLogFile(string runtimePath, string operation)
        {
            var logDirectory = GetLogDirectory();
            Directory.CreateDirectory(logDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var filePath = Path.Combine(logDirectory, LogFilePrefix + timestamp + LogFileExtension);
            var suffix = 1;
            while (File.Exists(filePath))
            {
                filePath = Path.Combine(logDirectory, $"{LogFilePrefix}{timestamp}-{suffix:00}{LogFileExtension}");
                suffix++;
            }

            AppendLine(filePath, "Metadata performance profiler started.");
            AppendLine(filePath, $"Operation: {operation}");
            AppendLine(filePath, $"RuntimePath: {runtimePath}");
            AppendLine(filePath, $"LogPath: {filePath}");
            AppendLine(filePath, $"MachineName: {Environment.MachineName}");
            AppendLine(filePath, $"OSVersion: {Environment.OSVersion}");
            AppendLine(filePath, $"ProcessPath: {Environment.ProcessPath ?? string.Empty}");
            AppendLine(filePath, string.Empty);
            return filePath;
        }

        public static string GetLogDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationBranding.InternalName,
                "Logs");
        }

        public static void AppendSection(string? logPath, string sectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
            {
                return;
            }

            AppendLine(logPath, string.Empty);
            AppendLine(logPath, "=== " + sectionName.Trim() + " ===");
        }

        public static void AppendLine(string? logPath, string message)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
                var line = string.IsNullOrEmpty(message)
                    ? Environment.NewLine
                    : $"[{timestamp}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // This profiler must never affect metadata loading or the editor UI.
            }
        }
    }
}
