using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics;

public enum AppLogLevel { Debug, Info, Warning, Error }

public static class AppLogger
{
    // Engineering safety bounds, not measured production burst guarantees. Stress calibration owns future changes.
    private const int NormalQueueCapacity = 128;
    private const int EmergencyQueueCapacity = 32;
    private const int MaximumQueuedEntryCharacters = 16_384;
    private const string TruncationMarker = "\r\n[Log entry truncated by the bounded logging safety policy.]\r\n";
    private const long MaxLogFileBytes = 2 * 1024 * 1024;
    private const int MaxArchiveFiles = 5;
    private static readonly TimeSpan LogRetentionWindow = TimeSpan.FromDays(14);
    private static readonly string RootDirectory = ApplicationBranding.LocalApplicationDataRoot;
    private static readonly string LogDirectory = Path.Combine(RootDirectory, "Logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, ApplicationBranding.LogFileName);
    private static readonly string EmergencyLogPath = Path.Combine(RootDirectory, "startup-error.log");
    private static readonly string DebugFlagPath = Path.Combine(RootDirectory, "logging.debug.enabled");
    // Private, production-default delegates provide deterministic per-class file-failure injection through reflection tests.
    private static Action<string> _primaryDirectoryCreate = static path => Directory.CreateDirectory(path);
    private static Func<string, string, Encoding, Task> _primaryAppend = static (path, text, encoding) => File.AppendAllTextAsync(path, text, encoding);
    private static Action<string> _emergencyDirectoryCreate = static path => Directory.CreateDirectory(path);
    private static Func<string, string, Encoding, Task> _emergencyAppend = static (path, text, encoding) => File.AppendAllTextAsync(path, text, encoding);
    private static readonly Channel<LogEntry> LogChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(NormalQueueCapacity)
    {
        SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait
    });
    private static readonly Channel<string> EmergencyChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(EmergencyQueueCapacity)
    {
        SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait
    });
    private static readonly Task WriterTask;
    private static readonly Task EmergencyWriterTask;
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static readonly int ProcessId = Environment.ProcessId;
    private static readonly AppLogLevel MinimumLevel;
    private static int _loggerState;
    private static int _shutdownRequested;
    private static long _debugDropCount;
    private static long _infoDropCount;
    private static long _warningDropCount;
    private static long _errorDropCount;
    private static long _emergencyRejectionCount;
    private static long _emergencyPersistenceFailureCount;

    static AppLogger()
    {
        MinimumLevel = ResolveMinimumLevel();
        var retentionSummary = CleanupExpiredLogFiles();
        WriterTask = Task.Run(WriterLoopAsync);
        EmergencyWriterTask = Task.Run(EmergencyWriterLoopAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        AppDomain.CurrentDomain.DomainUnload += (_, _) => Shutdown();
        Info("Logger", $"Logging started. Level={MinimumLevel}. Session={SessionId}. LogPath={LogPath}");
        if (!string.IsNullOrWhiteSpace(retentionSummary)) Info("Logger", retentionSummary);
    }

    public static string CurrentLogDirectory => LogDirectory;
    public static string CurrentLogPath => LogPath;
    public static bool IsDebugEnabled => MinimumLevel <= AppLogLevel.Debug;
    public static void Debug(string component, string message) => Log(AppLogLevel.Debug, component, message);
    public static void Info(string component, string message) => Log(AppLogLevel.Info, component, message);
    public static void Warning(string component, string message) => Log(AppLogLevel.Warning, component, message);
    public static void Error(string component, string message, Exception? exception = null) => Log(AppLogLevel.Error, component, message, exception);

    public static void Log(AppLogLevel level, string component, string message, Exception? exception = null)
    {
        if (level < MinimumLevel) return;

        var entry = new LogEntry(level, BuildEntry(level, component, message, exception));
        if (Volatile.Read(ref _shutdownRequested) != 0 || Volatile.Read(ref _loggerState) != (int)LoggerState.Active)
        {
            RejectNormalEntry(entry);
            return;
        }

        if (!LogChannel.Writer.TryWrite(entry)) RejectNormalEntry(entry);
    }

    public static void Shutdown(TimeSpan? maxWait = null)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;

        Interlocked.CompareExchange(ref _loggerState, (int)LoggerState.ShuttingDown, (int)LoggerState.Active);
        var waitBudget = maxWait ?? TimeSpan.FromSeconds(2);
        var stopwatch = Stopwatch.StartNew();
        TryComplete(LogChannel.Writer);
        var normalBudget = TimeSpan.FromTicks(waitBudget.Ticks / 2);
        TryWait(WriterTask, normalBudget);
        TryComplete(EmergencyChannel.Writer);
        var remaining = waitBudget - stopwatch.Elapsed;
        if (remaining > TimeSpan.Zero) TryWait(EmergencyWriterTask, remaining);
    }

    private static string BuildEntry(AppLogLevel level, string component, string message, Exception? exception)
    {
        var safeComponent = string.IsNullOrWhiteSpace(component) ? "General" : component.Trim();
        var safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        var builder = new StringBuilder(512);
        builder.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ");
        builder.Append('[').Append(level.ToString().ToUpperInvariant()).Append("] ");
        builder.Append('[').Append(safeComponent).Append("] ");
        builder.Append("[pid:").Append(ProcessId).Append(" tid:").Append(Environment.CurrentManagedThreadId).Append("] ");
        builder.Append("[session:").Append(SessionId).Append("] ");
        builder.Append(safeMessage);
        if (exception is not null) { builder.AppendLine(); builder.Append(exception); }
        return BoundEntry(builder.ToString());
    }

    private static string BoundEntry(string entry)
    {
        if (entry.Length <= MaximumQueuedEntryCharacters) return entry;
        var remaining = MaximumQueuedEntryCharacters - TruncationMarker.Length;
        var prefixLength = Math.Max(1, remaining / 2);
        var suffixLength = Math.Max(1, remaining - prefixLength);
        return entry[..prefixLength] + TruncationMarker + entry[^suffixLength..];
    }

    private static void RejectNormalEntry(LogEntry entry)
    {
        switch (entry.Level)
        {
            case AppLogLevel.Debug: Interlocked.Increment(ref _debugDropCount); return;
            case AppLogLevel.Info: Interlocked.Increment(ref _infoDropCount); return;
            case AppLogLevel.Warning: Interlocked.Increment(ref _warningDropCount); return;
            case AppLogLevel.Error:
                Interlocked.Increment(ref _errorDropCount);
                QueueEmergency(entry.Text);
                return;
        }
    }

    private static void TransitionToDegraded(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _loggerState, (int)LoggerState.Degraded, (int)LoggerState.Active) != (int)LoggerState.Active) return;
        TryComplete(LogChannel.Writer);
        QueueEmergency(BuildEntry(AppLogLevel.Error, "Logger",
            $"Primary application log writer failed and was disabled for this process. Dropped Debug={Volatile.Read(ref _debugDropCount)}, Info={Volatile.Read(ref _infoDropCount)}, Warning={Volatile.Read(ref _warningDropCount)}, Error={Volatile.Read(ref _errorDropCount)}.", exception));
    }

    private static void QueueEmergency(string entry)
    {
        if (!EmergencyChannel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _emergencyRejectionCount);
            System.Diagnostics.Debug.WriteLine("[AppLogger] Emergency error logging spool rejected an Error record.");
        }
    }

    private static async Task WriterLoopAsync()
    {
        try
        {
            _primaryDirectoryCreate(LogDirectory);
            await foreach (var entry in LogChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                RotateIfNeeded();
                await _primaryAppend(LogPath, entry.Text + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TransitionToDegraded(ex); }
    }

    private static async Task EmergencyWriterLoopAsync()
    {
        await foreach (var entry in EmergencyChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _emergencyDirectoryCreate(RootDirectory);
                await _emergencyAppend(EmergencyLogPath, entry + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _emergencyPersistenceFailureCount);
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Emergency error persistence failed: {ex}");
            }
        }
    }

    private static void TryComplete<T>(ChannelWriter<T> writer) { try { writer.TryComplete(); } catch { } }
    private static void TryWait(Task task, TimeSpan wait) { try { task.Wait(wait); } catch { } }

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
                    if (fileInfo.LastWriteTimeUtc <= cutoffUtc.UtcDateTime) { fileInfo.Delete(); deletedCount++; }
                }
                catch { failedCount++; }
            }

            var startupErrorPath = Path.Combine(RootDirectory, "startup-error.log");
            if (File.Exists(startupErrorPath))
            {
                try
                {
                    var startupErrorInfo = new FileInfo(startupErrorPath);
                    if (startupErrorInfo.LastWriteTimeUtc <= cutoffUtc.UtcDateTime) { startupErrorInfo.Delete(); deletedCount++; }
                }
                catch { failedCount++; }
            }
            return deletedCount == 0 && failedCount == 0
                ? string.Empty
                : $"Startup log retention cleanup completed. Deleted={deletedCount}, Failed={failedCount}, RetentionDays={LogRetentionWindow.TotalDays:0}.";
        }
        catch { return "Startup log retention cleanup could not be completed."; }
    }

    private static AppLogLevel ResolveMinimumLevel()
    {
        try
        {
            var environmentValue = Environment.GetEnvironmentVariable("PSSTUDIO_LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(environmentValue) && Enum.TryParse<AppLogLevel>(environmentValue, true, out var parsedLevel)) return parsedLevel;
        }
        catch { }
        try { if (File.Exists(DebugFlagPath)) return AppLogLevel.Debug; } catch { }
        return AppLogLevel.Info;
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length < MaxLogFileBytes) return;
            for (var index = MaxArchiveFiles - 1; index >= 1; index--)
            {
                var sourcePath = LogPath + "." + index;
                var destinationPath = LogPath + "." + (index + 1);
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                if (File.Exists(sourcePath)) File.Move(sourcePath, destinationPath);
            }
            var firstArchivePath = LogPath + ".1";
            if (File.Exists(firstArchivePath)) File.Delete(firstArchivePath);
            if (File.Exists(LogPath)) File.Move(LogPath, firstArchivePath);
        }
        catch { }
    }

    private sealed record LogEntry(AppLogLevel Level, string Text);
    private enum LoggerState { Active, Degraded, ShuttingDown }
}
