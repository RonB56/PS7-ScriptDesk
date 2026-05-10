using System;
using System.IO;
using System.Text;
using System.Threading;
using PowerShellStudio.Application.Utilities;

namespace PowerShellStudio.Shell.Debug
{
    internal static class DebuggerTraceLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogDirectory = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs");
        private static readonly string LogPath = Path.Combine(LogDirectory, "PS7ScriptDesk_DebuggerTrace.log");

        public static string CurrentPath => LogPath;

        public static void Write(string source, string message)
        {
            try
            {
                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                var safeSource = string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim();
                var safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Replace(Environment.NewLine, " | ", StringComparison.Ordinal).Trim();

                var line = $"[{timestamp}] [pid:{Environment.ProcessId} tid:{Environment.CurrentManagedThreadId}] [{safeSource}] {safeMessage}";

                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Best effort diagnostics only.
            }
        }
    }
}
