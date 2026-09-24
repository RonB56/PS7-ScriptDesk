using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Opt-in, bounded runtime tracing for editor keyboard/focus timing investigations.
/// The trace is deliberately separate from normal developer diagnostics so a focused
/// UAT run can be correlated without making every editor event part of the normal log.
/// </summary>
public static class EditorInputTrace
{
    private const int BufferCapacity = 4096;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly AsyncLocal<string?> CurrentTrace = new();
    private static readonly object SyncRoot = new();
    private static Channel<string>? _channel;
    private static Task? _writerTask;
    private static string? _filePath;
    private static long _sequence;
    private static long _dropped;
    private static int _started;

    public static bool IsEnabled =>
        DeveloperDiagnostics.IsEnabled &&
        DeveloperDiagnostics.IsVerboseEditorEnabled() &&
        IsEnvironmentEnabled();

    public static string? FilePath => _filePath;

    public static string? CurrentCorrelationId => CurrentTrace.Value;

    public static string CreateCorrelationId() =>
        $"EI-{Interlocked.Increment(ref _sequence):D8}";

    public static IDisposable PushCorrelation(string? correlationId)
    {
        var previous = CurrentTrace.Value;
        CurrentTrace.Value = correlationId ?? previous;
        return new Scope(previous);
    }

    public static void Log(string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            EnsureStarted();
            var timestamp = DateTimeOffset.UtcNow;
            var record = new Dictionary<string, object?>(12)
            {
                ["sequence"] = Interlocked.Increment(ref _sequence),
                ["timestampUtc"] = timestamp.ToString("O", CultureInfo.InvariantCulture),
                ["monotonicTimestampTicks"] = Stopwatch.GetTimestamp(),
                ["elapsedMicrosecondsSinceTraceStart"] = Clock.ElapsedTicks * 1_000_000d / Stopwatch.Frequency,
                ["event"] = eventName,
                ["correlationId"] = correlationId ?? CurrentTrace.Value,
                ["threadId"] = Environment.CurrentManagedThreadId,
                ["isUiThread"] = DeveloperDiagnostics.IsUiThread,
                ["droppedRecordCount"] = Volatile.Read(ref _dropped),
                ["properties"] = properties
            };

            var line = JsonSerializer.Serialize(record);
            if (_channel is null || !_channel.Writer.TryWrite(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public static void Stop()
    {
        Channel<string>? channel;
        Task? writer;
        lock (SyncRoot)
        {
            channel = _channel;
            writer = _writerTask;
            _channel = null;
            _writerTask = null;
            Volatile.Write(ref _started, 0);
        }

        if (channel is null)
        {
            return;
        }

        try { channel.Writer.TryComplete(); } catch { }
        try { writer?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private static bool IsEnvironmentEnabled()
    {
        var value = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_EDITOR_INPUT_TRACE");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 1)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_started == 1)
            {
                return;
            }

            var directory = DeveloperDiagnostics.DeveloperDebuggingRootDirectory;
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            _filePath = Path.Combine(directory, $"EditorInputTrace_{stamp}_pid{Environment.ProcessId}.jsonl");
            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(BufferCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _writerTask = Task.Run(async () =>
            {
                try
                {
                    await using var stream = new FileStream(_filePath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
                    await using var writer = new StreamWriter(stream);
                    var bufferedRecords = 0;
                    await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        try
                        {
                            await writer.WriteLineAsync(line).ConfigureAwait(false);
                            if (++bufferedRecords >= 32)
                            {
                                await writer.FlushAsync().ConfigureAwait(false);
                                bufferedRecords = 0;
                            }
                        }
                        catch
                        {
                            Interlocked.Increment(ref _dropped);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _dropped);
                }
            });
            Volatile.Write(ref _started, 1);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentTrace.Value = _previous;
        }
    }
}
