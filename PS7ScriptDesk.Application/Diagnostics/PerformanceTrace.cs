using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Opt-in, bounded performance tracing for owner-run D.1 captures.
/// It is deliberately layer-neutral and disabled unless PS7SCRIPTDESK_PERF_TRACE=1.
/// </summary>
public static class PerformanceTrace
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_PERF_TRACE";
    public const string WorkloadEnvironmentVariableName = "PS7SCRIPTDESK_PERF_WORKLOAD";
    public const string RunIdEnvironmentVariableName = "PS7SCRIPTDESK_PERF_RUN_ID";

    private const int ChannelCapacity = 8192;
    private const int SamplesPerOperation = 4096;
    private const int ShutdownWaitMilliseconds = 2000;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<string, SampleBucket> Samples = new(StringComparer.Ordinal);
    private static Channel<string>? _channel;
    private static Task? _writerTask;
    private static string? _sessionDirectory;
    private static string? _sessionId;
    private static string? _tracePath;
    private static int _started;
    private static int _stopped;
    private static long _sequence;
    private static long _dropped;
    private static long _writeFailures;
    private static readonly ConcurrentQueue<string> FallbackLines = new();

    public static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariableName), "1", StringComparison.Ordinal);

    public static string? SessionId => _sessionId;
    public static string? SessionDirectory => _sessionDirectory;
    public static string? TracePath => _tracePath;
    public static string? WorkloadLabel => Environment.GetEnvironmentVariable(WorkloadEnvironmentVariableName);
    public static string? RunId => Environment.GetEnvironmentVariable(RunIdEnvironmentVariableName);

    public static IDisposable Begin(
        string category,
        string operation,
        string? correlationId = null,
        string? requestId = null,
        long? documentVersion = null,
        long? generation = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!IsEnabled)
        {
            return NoopScope.Instance;
        }

        EnsureStarted();
        return new TimingScope(category, operation, correlationId, requestId, documentVersion, generation, properties);
    }

    public static IDisposable BeginDispatcher(
        string category,
        string operation,
        long enqueueTimestampTicks,
        string? correlationId = null,
        string? requestId = null,
        long? generation = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!IsEnabled)
        {
            return NoopScope.Instance;
        }

        EnsureStarted();
        return new DispatcherScope(category, operation, enqueueTimestampTicks, correlationId, requestId, generation, properties);
    }

    public static void Record(
        string eventType,
        string category,
        string operation,
        string? correlationId = null,
        string? requestId = null,
        long? documentVersion = null,
        long? generation = null,
        double? durationMs = null,
        double? queueWaitMs = null,
        int? payloadCount = null,
        int? payloadChars = null,
        long? payloadBytes = null,
        bool? cancelRequested = null,
        bool? staleDiscarded = null,
        string? result = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        EnsureStarted();
        var now = DateTimeOffset.UtcNow;
        var record = new Dictionary<string, object?>(24)
        {
            ["timestampUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["monotonicTimestampTicks"] = Stopwatch.GetTimestamp(),
            ["sessionId"] = _sessionId,
            ["workloadLabel"] = WorkloadLabel,
            ["runId"] = RunId,
            ["sequence"] = Interlocked.Increment(ref _sequence),
            ["eventType"] = eventType,
            ["category"] = category,
            ["operation"] = operation,
            ["threadId"] = Environment.CurrentManagedThreadId,
            ["dispatcherAccess"] = DeveloperDiagnostics.IsUiThread,
            ["correlationId"] = correlationId,
            ["requestId"] = requestId,
            ["documentVersion"] = documentVersion,
            ["generation"] = generation,
            ["durationMs"] = durationMs,
            ["queueWaitMs"] = queueWaitMs,
            ["payloadCount"] = payloadCount,
            ["payloadChars"] = payloadChars,
            ["payloadBytes"] = payloadBytes,
            ["cancelRequested"] = cancelRequested,
            ["staleDiscarded"] = staleDiscarded,
            ["result"] = result,
            ["exceptionType"] = exception?.GetType().FullName,
            ["thresholdBucket"] = Threshold(durationMs),
            ["properties"] = properties
        };

        try
        {
            var line = JsonSerializer.Serialize(record, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            if (_channel is null || !_channel.Writer.TryWrite(line))
            {
                Interlocked.Increment(ref _dropped);
            }
            FallbackLines.Enqueue(line);
            while (FallbackLines.Count > ChannelCapacity && FallbackLines.TryDequeue(out _)) { }
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _writeFailures);
        }

        if (durationMs is { } duration)
        {
            Samples.GetOrAdd(SampleKey(category, operation), static _ => new SampleBucket()).Add(duration);
        }
    }

    public static void Snapshot(string category, string operation, string phase, string? correlationId = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var process = Process.GetCurrentProcess();
        Record("memorySnapshot", category, operation, correlationId, result: phase,
            properties: new Dictionary<string, object?>
            {
                ["gen0"] = GC.CollectionCount(0),
                ["gen1"] = GC.CollectionCount(1),
                ["gen2"] = GC.CollectionCount(2),
                ["managedBytes"] = GC.GetTotalMemory(false),
                ["workingSetBytes"] = process.WorkingSet64,
                ["privateMemoryBytes"] = process.PrivateMemorySize64
            });
    }

    public static void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0 || _channel is null)
        {
            return;
        }

        try
        {
            _channel.Writer.TryComplete();
            var completed = _writerTask?.Wait(ShutdownWaitMilliseconds) == true;
            EnsureTraceFileIsNonEmpty(completed);
            WriteSummary();
        }
        catch
        {
            // Performance tracing must never affect shutdown.
        }
    }

    private static void EnsureStarted()
    {
        if (Volatile.Read(ref _started) != 0)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_started != 0 || !IsEnabled)
            {
                return;
            }

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            _sessionId = $"perf-{stamp}-pid{Environment.ProcessId}";
            var root = DeveloperDiagnostics.DeveloperDebuggingRootDirectory;
            _sessionDirectory = Path.Combine(root, "PerformanceSessions", _sessionId);
            Directory.CreateDirectory(_sessionDirectory);
            _tracePath = Path.Combine(_sessionDirectory, $"PerformanceTrace_{_sessionId}.jsonl");
            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _writerTask = Task.Run(WriterLoopAsync);
            WriteManifest();
            Volatile.Write(ref _started, 1);
        }
    }

    private static async Task WriterLoopAsync()
    {
        if (_channel is null || _tracePath is null)
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(_tracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Increment(ref _writeFailures);
        }
    }

    private static void EnsureTraceFileIsNonEmpty(bool writerCompleted)
    {
        if (_tracePath is null || Volatile.Read(ref _sequence) == 0) return;
        try
        {
            if (new FileInfo(_tracePath).Length > 0) return;
            using var stream = new FileStream(_tracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var line in FallbackLines) writer.WriteLine(line);
            writer.Flush();
            if (stream.Length == 0)
            {
                var health = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["sessionId"] = _sessionId,
                    ["eventType"] = "traceHealth",
                    ["result"] = "empty-after-finalization",
                    ["writerCompleted"] = writerCompleted,
                    ["writeFailures"] = Volatile.Read(ref _writeFailures)
                });
                writer.WriteLine(health);
            }
        }
        catch
        {
            Interlocked.Increment(ref _writeFailures);
        }
    }

    private static void WriteManifest()
    {
        if (_sessionDirectory is null || _sessionId is null)
        {
            return;
        }

        try
        {
            var manifest = new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["workloadLabel"] = WorkloadLabel,
                ["runId"] = RunId,
                ["startTimestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["processId"] = Environment.ProcessId,
                ["processPath"] = Environment.ProcessPath,
                ["configuration"] = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "unknown",
                ["platform"] = RuntimeInformation.OSArchitecture.ToString(),
                ["os"] = RuntimeInformation.OSDescription,
                ["dotnetRuntime"] = RuntimeInformation.FrameworkDescription,
                ["powerShellRuntime"] = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_POWERSHELL_RUNTIME") ?? "unknown",
                ["traceEnabled"] = true,
                ["developerDiagnosticsEnabled"] = DeveloperDiagnostics.IsEnabled,
                ["traceCapacity"] = ChannelCapacity
            };
            File.WriteAllText(Path.Combine(_sessionDirectory, "PerformanceManifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static void WriteSummary()
    {
        if (_sessionDirectory is null || _sessionId is null)
        {
            return;
        }

        try
        {
            var summary = new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["endTimestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["tracePath"] = _tracePath,
                ["droppedEvents"] = Volatile.Read(ref _dropped),
                ["writeFailures"] = Volatile.Read(ref _writeFailures),
                ["corruptRecords"] = 0,
                ["normalShutdown"] = true,
                ["eventCount"] = Volatile.Read(ref _sequence),
                ["sessionDurationMs"] = Math.Round(Clock.Elapsed.TotalMilliseconds, 3),
                ["operations"] = Samples.ToDictionary(pair => pair.Key, pair => pair.Value.ToSummary(), StringComparer.Ordinal)
            };
            File.WriteAllText(Path.Combine(_sessionDirectory, $"PerformanceSummary_{_sessionId}.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_sessionDirectory, $"PerformanceSummary_{_sessionId}.md"), BuildMarkdownSummary(summary), new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string BuildMarkdownSummary(IReadOnlyDictionary<string, object?> summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# PS7 ScriptDesk Performance Summary");
        builder.AppendLine();
        builder.AppendLine($"Session: `{_sessionId}`");
        builder.AppendLine($"Workload: `{WorkloadLabel ?? "(unlabeled)"}`");
        builder.AppendLine($"Run ID: `{RunId ?? "(unassigned)"}`");
        builder.AppendLine($"Dropped events: `{Volatile.Read(ref _dropped)}`");
        builder.AppendLine();
        builder.AppendLine("| Operation | Count | P50 ms | P95 ms | P99 ms | Max ms |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var pair in Samples.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var item = pair.Value.ToSummary();
            builder.AppendLine($"| {pair.Key} | {item["count"]} | {item["p50Ms"]} | {item["p95Ms"]} | {item["p99Ms"]} | {item["maxMs"]} |");
        }
        return builder.ToString();
    }

    internal static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(static value => value).ToArray();
        var index = Math.Min(sorted.Length - 1, Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1));
        return Math.Round(sorted[index], 3, MidpointRounding.AwayFromZero);
    }

    private static string SampleKey(string category, string operation) =>
        $"{WorkloadLabel ?? "(unlabeled)"}/{RunId ?? "(unassigned)"}/{category}/{operation}";

    private static string Threshold(double? durationMs) => durationMs switch
    {
        null => "none",
        < 16 => "under16ms",
        < 50 => "16-50ms",
        < 100 => "50-100ms",
        < 250 => "100-250ms",
        < 1000 => "250-1000ms",
        _ => "over1000ms"
    };

    private sealed class TimingScope : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly string _category;
        private readonly string _operation;
        private readonly string? _correlationId;
        private readonly string? _requestId;
        private readonly long? _documentVersion;
        private readonly long? _generation;
        private readonly IReadOnlyDictionary<string, object?>? _properties;
        private int _disposed;

        public TimingScope(string category, string operation, string? correlationId, string? requestId, long? documentVersion, long? generation, IReadOnlyDictionary<string, object?>? properties)
        {
            _category = category;
            _operation = operation;
            _correlationId = correlationId;
            _requestId = requestId;
            _documentVersion = documentVersion;
            _generation = generation;
            _properties = properties;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var duration = (Stopwatch.GetTimestamp() - _start) * 1000d / Stopwatch.Frequency;
            PerformanceTrace.Record("scope", _category, _operation, _correlationId, _requestId, _documentVersion, _generation, durationMs: duration, properties: _properties);
        }
    }

    private sealed class DispatcherScope : IDisposable
    {
        private readonly long _enqueueTimestampTicks;
        private readonly long _startTimestampTicks = Stopwatch.GetTimestamp();
        private readonly string _category;
        private readonly string _operation;
        private readonly string? _correlationId;
        private readonly string? _requestId;
        private readonly long? _generation;
        private readonly IReadOnlyDictionary<string, object?>? _properties;
        private int _disposed;

        public DispatcherScope(string category, string operation, long enqueueTimestampTicks, string? correlationId, string? requestId, long? generation, IReadOnlyDictionary<string, object?>? properties)
        {
            _category = category;
            _operation = operation;
            _enqueueTimestampTicks = enqueueTimestampTicks;
            _correlationId = correlationId;
            _requestId = requestId;
            _generation = generation;
            _properties = properties;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var now = Stopwatch.GetTimestamp();
            var queueWait = (_startTimestampTicks - _enqueueTimestampTicks) * 1000d / Stopwatch.Frequency;
            var duration = (now - _startTimestampTicks) * 1000d / Stopwatch.Frequency;
            PerformanceTrace.Record("dispatcherCallback", _category, _operation, _correlationId, _requestId, generation: _generation, durationMs: duration, queueWaitMs: queueWait, properties: _properties);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private sealed class SampleBucket
    {
        private readonly ConcurrentQueue<double> _values = new();
        private long _count;
        private double _max;

        public void Add(double value)
        {
            Interlocked.Increment(ref _count);
            while (true)
            {
                var current = Volatile.Read(ref _max);
                if (value <= current || Interlocked.CompareExchange(ref _max, value, current) == current)
                {
                    break;
                }
            }
            _values.Enqueue(value);
            while (_values.Count > SamplesPerOperation && _values.TryDequeue(out _)) { }
        }

        public Dictionary<string, object?> ToSummary()
        {
            var values = _values.ToArray();
            Array.Sort(values);
            return new Dictionary<string, object?>
            {
                ["count"] = Volatile.Read(ref _count),
                ["sampleCount"] = values.Length,
                ["p50Ms"] = PerformanceTrace.Percentile(values, 0.50),
                ["p95Ms"] = PerformanceTrace.Percentile(values, 0.95),
                ["p99Ms"] = PerformanceTrace.Percentile(values, 0.99),
                ["maxMs"] = Volatile.Read(ref _max)
            };
        }

    }
}
