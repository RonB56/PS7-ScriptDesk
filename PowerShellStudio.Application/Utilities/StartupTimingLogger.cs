using System;
using System.IO;
using PowerShellStudio.Application.Diagnostics;

namespace PowerShellStudio.Application.Utilities
{
    public static class StartupTimingLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerShellStudio");
        private static readonly string LogPath = Path.Combine(LogDirectory, "startup-timing.log");
        private static bool _sessionStarted;

        public static void StartSession(string source)
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                using var writer = new StreamWriter(LogPath, append: true);
                writer.WriteLine(new string('=', 80));
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SESSION START - {source}");
                writer.WriteLine(new string('=', 80));
                _sessionStarted = true;
            }

            AppLogger.Info("StartupTiming", $"Session start - {source}");
        }

        public static void Log(string source, string message)
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                using var writer = new StreamWriter(LogPath, append: true);
                if (!_sessionStarted)
                {
                    writer.WriteLine(new string('=', 80));
                    writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SESSION START - implicit");
                    writer.WriteLine(new string('=', 80));
                    _sessionStarted = true;
                }

                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}");
            }

            AppLogger.Info(source, message);
        }
    }
}
