using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Application.Utilities
{
    public static class StartupTimingLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs");
        private static readonly string LogPath = Path.Combine(LogDirectory, "startup-timing.log");
        // Private production-default delegates are test seams only; reflection tests restore them after injection.
        private static Action<string> _directoryCreate = static path => Directory.CreateDirectory(path);
        private static Func<string, StreamWriter> _appendWriter = static path => new StreamWriter(path, append: true);
        private static bool _sessionStarted;
        private static int _timingStorageDisabled;
        private static int _timingStorageFailureReported;

        public static void StartSession(string source)
        {
            TryWriteTimingFile(writer =>
            {
                writer.WriteLine(new string('=', 80));
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SESSION START - {source}");
                writer.WriteLine(new string('=', 80));
                _sessionStarted = true;
            });

            AppLogger.Info("StartupTiming", $"Session start - {source}");
            DeveloperDiagnostics.LogInfo("Startup", $"Startup timing session started: {source}.");
        }

        public static void Log(string source, string message)
        {
            TryWriteTimingFile(writer =>
            {
                if (!_sessionStarted)
                {
                    writer.WriteLine(new string('=', 80));
                    writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SESSION START - implicit");
                    writer.WriteLine(new string('=', 80));
                    _sessionStarted = true;
                }

                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}");
            });

            AppLogger.Info(source, message);
            DeveloperDiagnostics.LogInfo("Performance", message, new Dictionary<string, object?> { ["startupSource"] = source });
        }

        private static void TryWriteTimingFile(Action<StreamWriter> write)
        {
            if (Volatile.Read(ref _timingStorageDisabled) != 0)
            {
                return;
            }

            Exception? failure = null;
            lock (SyncRoot)
            {
                if (Volatile.Read(ref _timingStorageDisabled) != 0)
                {
                    return;
                }

                try
                {
                    _directoryCreate(LogDirectory);
                    using var writer = _appendWriter(LogPath);
                    write(writer);
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _timingStorageDisabled, 1);
                    failure = ex;
                }
            }

            if (failure is not null && Interlocked.Exchange(ref _timingStorageFailureReported, 1) == 0)
            {
                AppLogger.Error(
                    "StartupTiming",
                    "Optional startup timing-file diagnostics were disabled for this process after a storage failure.",
                    failure);
                DeveloperDiagnostics.LogOperationFailure(
                    "StartupTiming",
                    "TimingFileWrite",
                    "Optional startup timing-file diagnostics were disabled after a storage failure.",
                    failure,
                    additionalProperties: new Dictionary<string, object?> { ["timingLogPath"] = LogPath });
            }
        }
    }
}
