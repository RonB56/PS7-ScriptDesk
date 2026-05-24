using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PS7ScriptDesk.TerminalSpike
{
    internal static class TerminalSpikeLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PS7ScriptDesk",
            "TerminalSpike");
        private static readonly string LogPath = Path.Combine(LogDirectory, "terminal-spike.log");
        private static bool _disposed;

        static TerminalSpikeLogger()
        {
            Directory.CreateDirectory(LogDirectory);
            Write("INFO", "LOGGER", $"Terminal spike logging started. LogPath={LogPath}");
        }

        public static string DirectoryPath => LogDirectory;

        public static string FilePath => LogPath;

        public static void Debug(string tag, string message)
        {
            Write("DEBUG", NormalizeTag(tag), message);
        }

        public static void Info(string tag, string message)
        {
            Write("INFO", NormalizeTag(tag), message);
        }

        public static void Warning(string tag, string message)
        {
            Write("WARN", NormalizeTag(tag), message);
        }

        public static void Error(string tag, string message, Exception? exception = null)
        {
            Write("ERROR", NormalizeTag(tag), exception is null ? message : $"{message}{Environment.NewLine}{exception}");
        }

        public static void OpenLogFolder()
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true
            });
        }

        public static void Dispose()
        {
            lock (SyncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                WriteUnlocked("INFO", "LOGGER", "Terminal spike logging stopped.");
                _disposed = true;
            }
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return "GENERAL";
            }

            return tag.Trim().ToUpperInvariant();
        }

        private static void Write(string level, string tag, string message)
        {
            lock (SyncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                WriteUnlocked(level, tag, message);
            }
        }

        private static void WriteUnlocked(string level, string tag, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [{tag}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the terminal spike.
            }
        }
    }
}
