using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics
{
    public enum AppLogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    public static class AppLogger
    {
        private const long MaxLogFileBytes = 2 * 1024 * 1024;
        private const int MaxArchiveFiles = 5;
        private static readonly TimeSpan LogRetentionWindow = TimeSpan.FromDays(14);
        private static readonly string RootDirectory = ApplicationBranding.LocalApplicationDataRoot;
        private static readonly string LogDirectory = Path.Combine(RootDirectory, "Logs");
        private static readonly string LogPath = Path.Combine(LogDirectory, ApplicationBranding.LogFileName);
        private static readonly string DebugFlagPath = Path.Combine(RootDirectory, "logging.debug.enabled");
        private static readonly Channel<string> LogChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        private static readonly Task WriterTask;
        private static readonly string SessionId = Guid.NewGuid().ToString("N");
        private static readonly int ProcessId = Environment.ProcessId;
        private static readonly AppLogLevel MinimumLevel;
        private static int _shutdownRequested;

        static AppLogger()
        {
            MinimumLevel = ResolveMinimumLevel();
            var retentionSummary = CleanupExpiredLogFiles();
            WriterTask = Task.Run(WriterLoopAsync);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            AppDomain.CurrentDomain.DomainUnload += (_, _) => Shutdown();
            Info("Logger", $"Logging started. Level={MinimumLevel}. Session={SessionId}. LogPath={LogPath}");
            if (!string.IsNullOrWhiteSpace(retentionSummary))
            {
                Info("Logger", retentionSummary);
            }
        }

        public static string CurrentLogDirectory => LogDirectory;

        public static string CurrentLogPath => LogPath;

        public static bool IsDebugEnabled => MinimumLevel <= AppLogLevel.Debug;

        public static void Debug(string component, string message)
        {
            Log(AppLogLevel.Debug, component, message);
        }

        public static void Info(string component, string message)
        {
            Log(AppLogLevel.Info, component, message);
        }

        public static void Warning(string component, string message)
        {
            Log(AppLogLevel.Warning, component, message);
        }

        public static void Error(string component, string message, Exception? exception = null)
        {
            Log(AppLogLevel.Error, component, message, exception);
        }

        public static void Log(AppLogLevel level, string component, string message, Exception? exception = null)
        {
            if (level < MinimumLevel || Volatile.Read(ref _shutdownRequested) != 0)
            {
                return;
            }

            var safeComponent = string.IsNullOrWhiteSpace(component) ? "General" : component.Trim();
            var safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var threadId = Environment.CurrentManagedThreadId;

            var builder = new StringBuilder(512);
            builder.Append('[').Append(timestamp).Append("] ");
            builder.Append('[').Append(level.ToString().ToUpperInvariant()).Append("] ");
            builder.Append('[').Append(safeComponent).Append("] ");
            builder.Append("[pid:").Append(ProcessId).Append(" tid:").Append(threadId).Append("] ");
            builder.Append("[session:").Append(SessionId).Append("] ");
            builder.Append(safeMessage);

            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            LogChannel.Writer.TryWrite(builder.ToString());
        }

        public static void Shutdown(TimeSpan? maxWait = null)
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            {
                return;
            }

            try
            {
                LogChannel.Writer.TryComplete();
            }
            catch
            {
                // Best effort only.
            }

            try
            {
                WriterTask.Wait(maxWait ?? TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best effort only.
            }
        }

        private static string CleanupExpiredLogFiles()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var cutoffUtc = DateTimeOffset.UtcNow.Subtract(LogRetentionWindow);
                var deletedCount = 0;
                var failedCount = 0;

                foreach (var filePath in Directory.EnumerateFiles(LogDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (fileInfo.LastWriteTimeUtc <= cutoffUtc.UtcDateTime)
                        {
                            fileInfo.Delete();
                            deletedCount++;
                        }
                    }
                    catch
                    {
                        failedCount++;
                    }
                }

                var startupErrorPath = Path.Combine(RootDirectory, "startup-error.log");
                if (File.Exists(startupErrorPath))
                {
                    try
                    {
                        var startupErrorInfo = new FileInfo(startupErrorPath);
                        if (startupErrorInfo.LastWriteTimeUtc <= cutoffUtc.UtcDateTime)
                        {
                            startupErrorInfo.Delete();
                            deletedCount++;
                        }
                    }
                    catch
                    {
                        failedCount++;
                    }
                }

                if (deletedCount == 0 && failedCount == 0)
                {
                    return string.Empty;
                }

                return $"Startup log retention cleanup completed. Deleted={deletedCount}, Failed={failedCount}, RetentionDays={LogRetentionWindow.TotalDays:0}.";
            }
            catch
            {
                return "Startup log retention cleanup could not be completed.";
            }
        }

        private static AppLogLevel ResolveMinimumLevel()
        {
            try
            {
                var environmentValue = Environment.GetEnvironmentVariable("PSSTUDIO_LOG_LEVEL");
                if (!string.IsNullOrWhiteSpace(environmentValue) && Enum.TryParse<AppLogLevel>(environmentValue, ignoreCase: true, out var parsedLevel))
                {
                    return parsedLevel;
                }
            }
            catch
            {
                // Ignore environment lookup failures.
            }

            try
            {
                if (File.Exists(DebugFlagPath))
                {
                    return AppLogLevel.Debug;
                }
            }
            catch
            {
                // Ignore debug flag lookup failures.
            }

            return AppLogLevel.Info;
        }

        private static async Task WriterLoopAsync()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                await foreach (var entry in LogChannel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    RotateIfNeeded();
                    await File.AppendAllTextAsync(LogPath, entry + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal during shutdown.
            }
            catch
            {
                // Logging must never crash the app.
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    var fileInfo = new FileInfo(LogPath);
                    if (fileInfo.Length < MaxLogFileBytes)
                    {
                        return;
                    }
                }

                for (var index = MaxArchiveFiles - 1; index >= 1; index--)
                {
                    var sourcePath = LogPath + "." + index;
                    var destinationPath = LogPath + "." + (index + 1);
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    if (File.Exists(sourcePath))
                    {
                        File.Move(sourcePath, destinationPath);
                    }
                }

                var firstArchivePath = LogPath + ".1";
                if (File.Exists(firstArchivePath))
                {
                    File.Delete(firstArchivePath);
                }

                if (File.Exists(LogPath))
                {
                    File.Move(LogPath, firstArchivePath);
                }
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}
