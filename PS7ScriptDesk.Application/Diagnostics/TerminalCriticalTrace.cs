using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics;

public readonly record struct TerminalCriticalUiThreadSnapshot(bool? DispatcherAccess, int? DispatcherThreadId);
public readonly record struct TerminalCriticalTraceHealth(long Accepted, long DroppedVerbose, long DroppedBoundary, long WriteFailures, long SlowWrites, int Pending, bool WriterRunning, string? LastFailureType);

public static class TerminalCriticalTrace
{
    private const long MaximumTraceFileBytes = 1 * 1024 * 1024;
    private const int MaximumEntryCharacters = 64 * 1024;
    private const string TraceFileName = "TerminalCriticalTrace.log";
    private const string ArchiveFileName = "TerminalCriticalTrace.log.1";
    private const string TruncationMarker = "\r\n[Terminal critical trace entry truncated by bounded diagnostics policy.]\r\n";
    private const int ChannelCapacity = 4096;
    private const int OptionalAdmissionLimit = 3072;
    private const int ShutdownWaitMilliseconds = 2000;
    private static readonly object SyncRoot = new();
    private static Channel<TraceRecord>? _channel;
    private static Task? _writerTask;
    private static long _accepted;
    private static long _nextSequence;
    private static long _droppedVerbose;
    private static long _droppedBoundary;
    private static long _writeFailures;
    private static long _slowWrites;
    private static int _pending;
    private static bool _stopped;
    private static bool _forceTimingForTests;
    private static string? _lastFailureType;
    private static Func<string> _tracePathProvider = CreateDefaultTracePath;
    private static Action<string> _createDirectory = static path => Directory.CreateDirectory(path);
    private static Action<string, string, Encoding> _appendAllText = static (path, text, encoding) => File.AppendAllText(path, text, encoding);
    private static Func<string, bool> _fileExists = static path => File.Exists(path);
    private static Func<string, long> _fileLength = static path => new FileInfo(path).Length;
    private static Action<string> _deleteFile = static path => File.Delete(path);
    private static Action<string, string> _moveFile = static (source, destination) => File.Move(source, destination);
    private static Func<TerminalCriticalUiThreadSnapshot> _uiThreadSnapshotProvider = static () => default;

    public static string CurrentTracePath => _tracePathProvider();
    public static bool IsVerboseEnabled => DeveloperDiagnostics.IsEnabled;
    public static bool IsTimingEnabled => _forceTimingForTests || DeveloperDiagnostics.IsEnabled || PerformanceTrace.IsEnabled;
    public static TerminalCriticalTraceHealth Health => new(
        Interlocked.Read(ref _accepted), Interlocked.Read(ref _droppedVerbose),
        Interlocked.Read(ref _droppedBoundary), Interlocked.Read(ref _writeFailures),
        Interlocked.Read(ref _slowWrites), Volatile.Read(ref _pending),
        _writerTask is { IsCompleted: false }, Volatile.Read(ref _lastFailureType));

    public static void ConfigureUiThreadSnapshotProvider(Func<TerminalCriticalUiThreadSnapshot>? provider)
    {
        _uiThreadSnapshotProvider = provider ?? (static () => default);
    }

    public static void LogStartupIdentity(
        IReadOnlyDictionary<string, object?>? additionalMetadata = null,
        [CallerMemberName] string? memberName = null,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var executingAssembly = Assembly.GetExecutingAssembly();
        var metadata = new Dictionary<string, object?>
        {
            ["executablePath"] = Environment.ProcessPath,
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["currentDirectory"] = Environment.CurrentDirectory,
            ["processId"] = Environment.ProcessId,
            ["applicationAssemblyVersion"] = (entryAssembly ?? executingAssembly).GetName().Version?.ToString(),
            ["applicationInformationalVersion"] = (entryAssembly ?? executingAssembly)
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["diagnosticsAssemblyVersion"] = executingAssembly.GetName().Version?.ToString(),
            ["diagnosticsInformationalVersion"] = executingAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["appDataRoot"] = ApplicationBranding.LocalApplicationDataRoot,
            ["tracePath"] = CurrentTracePath,
            ["structuredExecutionFeatureGate"] = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_STRUCTURED_EXECUTION"),
            ["threadAffinityDiagnosticInstrumentation"] = true
        };

        MergeMetadata(metadata, additionalMetadata);
        LogStage("Startup.BinaryIdentity", metadata, memberName, sourceFilePath, sourceLineNumber);
    }

    public static void LogStage(
        string stage,
        IReadOnlyDictionary<string, object?>? metadata = null,
        [CallerMemberName] string? memberName = null,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        WriteEntry(stage, exception: null, metadata, memberName, sourceFilePath, sourceLineNumber);
    }

    public static void LogException(
        string stage,
        Exception exception,
        IReadOnlyDictionary<string, object?>? metadata = null,
        [CallerMemberName] string? memberName = null,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        WriteEntry(stage, exception, metadata, memberName, sourceFilePath, sourceLineNumber);
    }

    public static void RecordTiming(string stage, long startedAt, int sessionGeneration = 0,
        int rendererGeneration = 0, long sequence = 0, int queueDepth = 0, int envelopeCount = 0)
    {
        if (!IsTimingEnabled || startedAt == 0) return;
        var microseconds = (long)(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds * 1000);
        RecordDurationMicroseconds(stage, microseconds, sessionGeneration, rendererGeneration, sequence,
            queueDepth, envelopeCount);
    }

    public static void RecordDurationMicroseconds(string stage, long microseconds, int sessionGeneration = 0,
        int rendererGeneration = 0, long sequence = 0, int queueDepth = 0, int envelopeCount = 0)
    {
        if (!IsTimingEnabled) return;
        try
        {
            lock (SyncRoot)
            {
                if (_stopped) return;
                EnsureWriter();
                if (_pending >= OptionalAdmissionLimit || !_channel!.Writer.TryWrite(
                    new TraceRecord(new TimingSample(stage, microseconds, sessionGeneration,
                        rendererGeneration, sequence, queueDepth, envelopeCount))))
                {
                    Interlocked.Increment(ref _droppedVerbose);
                    return;
                }
                Interlocked.Increment(ref _pending);
                Interlocked.Increment(ref _accepted);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedVerbose);
            Volatile.Write(ref _lastFailureType, ex.GetType().Name);
        }
    }

    public static Dictionary<string, object?> CreateDelegateMetadata(Delegate handler)
    {
        return new Dictionary<string, object?>
        {
            ["subscriberDeclaringType"] = handler.Method.DeclaringType?.FullName,
            ["subscriberMethod"] = handler.Method.Name,
            ["subscriberTargetType"] = handler.Target?.GetType().FullName,
            ["subscriberIsStatic"] = handler.Target is null
        };
    }

    internal static void ConfigureForTests(
        string tracePath,
        Action<string>? createDirectory = null,
        Action<string, string, Encoding>? appendAllText = null,
        Func<string, bool>? fileExists = null,
        Func<string, long>? fileLength = null,
        Action<string>? deleteFile = null,
        Action<string, string>? moveFile = null,
        Func<TerminalCriticalUiThreadSnapshot>? uiThreadSnapshotProvider = null)
    {
        Stop();
        lock (SyncRoot) _stopped = false;
        _forceTimingForTests = true;
        _tracePathProvider = () => tracePath;
        _createDirectory = createDirectory ?? (static path => Directory.CreateDirectory(path));
        _appendAllText = appendAllText ?? (static (path, text, encoding) => File.AppendAllText(path, text, encoding));
        _fileExists = fileExists ?? (static path => File.Exists(path));
        _fileLength = fileLength ?? (static path => new FileInfo(path).Length);
        _deleteFile = deleteFile ?? (static path => File.Delete(path));
        _moveFile = moveFile ?? (static (source, destination) => File.Move(source, destination));
        _uiThreadSnapshotProvider = uiThreadSnapshotProvider ?? (static () => default);
    }

    internal static void ResetForTests()
    {
        Stop();
        lock (SyncRoot) _stopped = false;
        _forceTimingForTests = false;
        _tracePathProvider = CreateDefaultTracePath;
        _createDirectory = static path => Directory.CreateDirectory(path);
        _appendAllText = static (path, text, encoding) => File.AppendAllText(path, text, encoding);
        _fileExists = static path => File.Exists(path);
        _fileLength = static path => new FileInfo(path).Length;
        _deleteFile = static path => File.Delete(path);
        _moveFile = static (source, destination) => File.Move(source, destination);
        _uiThreadSnapshotProvider = static () => default;
        Interlocked.Exchange(ref _accepted, 0);
        Interlocked.Exchange(ref _nextSequence, 0);
        Interlocked.Exchange(ref _droppedVerbose, 0);
        Interlocked.Exchange(ref _droppedBoundary, 0);
        Interlocked.Exchange(ref _writeFailures, 0);
        Interlocked.Exchange(ref _slowWrites, 0);
        Volatile.Write(ref _lastFailureType, null);
    }

    public static void Stop()
    {
        Task? writer;
        lock (SyncRoot)
        {
            _stopped = true;
            _channel?.Writer.TryComplete();
            writer = _writerTask;
            _channel = null;
            _writerTask = null;
        }
        try { writer?.Wait(ShutdownWaitMilliseconds); } catch { }
    }

    internal static bool FlushForTests(int timeoutMilliseconds = 2000)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (SyncRoot)
        {
            if (_channel is null) return true;
            if (!_channel.Writer.TryWrite(new TraceRecord(completion))) return false;
        }
        return completion.Task.Wait(timeoutMilliseconds);
    }

    private static void WriteEntry(
        string stage,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? metadata,
        string? memberName,
        string? sourceFilePath,
        int sourceLineNumber)
    {
        try
        {
            var boundary = exception is not null || IsBoundaryStage(stage);
            var capturedMetadata = metadata is null ? null : new KeyValuePair<string, object?>[metadata.Count];
            if (capturedMetadata is not null)
            {
                var index = 0;
                foreach (var pair in metadata!) capturedMetadata[index++] = pair;
            }
            var record = new TraceRecord(stage, exception, capturedMetadata, memberName, sourceFilePath,
                sourceLineNumber, DateTimeOffset.Now, Environment.CurrentManagedThreadId,
                CaptureApartmentState(), CaptureUiThreadSnapshot());
            lock (SyncRoot)
            {
                if (_stopped) return;
                EnsureWriter();
                if (!boundary && _pending >= OptionalAdmissionLimit)
                {
                    Interlocked.Increment(ref _droppedVerbose);
                    return;
                }
                record.Sequence = ++_nextSequence;
                if (!_channel!.Writer.TryWrite(record))
                {
                    if (boundary) Interlocked.Increment(ref _droppedBoundary);
                    else Interlocked.Increment(ref _droppedVerbose);
                    return;
                }
                Interlocked.Increment(ref _pending);
                Interlocked.Increment(ref _accepted);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedBoundary);
            Volatile.Write(ref _lastFailureType, ex.GetType().Name);
        }
    }

    private static void EnsureWriter()
    {
        if (_channel is not null) return;
        _channel = Channel.CreateBounded<TraceRecord>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
        });
        var reader = _channel.Reader;
        _writerTask = Task.Run(() => WriterLoop(reader));
    }

    private static async Task WriterLoop(ChannelReader<TraceRecord> reader)
    {
        var timings = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var timingHistory = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        var timingCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var lastTimingSamples = new Dictionary<string, TimingSample>(StringComparer.Ordinal);
        await foreach (var record in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (record.Completion is not null)
            {
                record.Completion.TrySetResult(true);
                continue;
            }
            try
            {
                var started = Stopwatch.GetTimestamp();
                var path = CurrentTracePath;
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Invalid critical trace directory.");
                if (record.Timing is { } sample)
                {
                    if (!timings.TryGetValue(sample.Stage, out var values))
                    {
                        values = new List<long>(128);
                        timings.Add(sample.Stage, values);
                    }
                    values.Add(sample.Microseconds);
                    if (!timingHistory.TryGetValue(sample.Stage, out var history))
                    {
                        history = new Queue<long>(4096);
                        timingHistory.Add(sample.Stage, history);
                    }
                    history.Enqueue(sample.Microseconds);
                    if (history.Count > 4096) history.Dequeue();
                    timingCounts[sample.Stage] = timingCounts.GetValueOrDefault(sample.Stage) + 1;
                    lastTimingSamples[sample.Stage] = sample;
                    if (values.Count < 128 && sample.Microseconds < 10_000) continue;
                    var entry = FormatTiming(sample, values, "window", timingCounts[sample.Stage]);
                    values.Clear();
                    Persist(path, directory, entry);
                }
                else
                {
                    Persist(path, directory, FormatEntry(record));
                }
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 50)
                    Interlocked.Increment(ref _slowWrites);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeFailures);
                Volatile.Write(ref _lastFailureType, ex.GetType().Name);
                Debug.WriteLine($"[TerminalCriticalTrace] Diagnostic write failed: {ex.GetType().Name}");
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
        foreach (var (stage, values) in timings)
        {
            if (values.Count == 0) continue;
            try
            {
                var path = CurrentTracePath;
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Invalid critical trace directory.");
                Persist(path, directory, FormatTiming(lastTimingSamples[stage], values, "lastWindow", timingCounts[stage]));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeFailures);
                Volatile.Write(ref _lastFailureType, ex.GetType().Name);
            }
        }
        foreach (var (stage, history) in timingHistory)
        {
            try
            {
                var path = CurrentTracePath;
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Invalid critical trace directory.");
                Persist(path, directory, FormatTiming(lastTimingSamples[stage], history.ToList(),
                    "finalRolling4096", timingCounts[stage]));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeFailures);
                Volatile.Write(ref _lastFailureType, ex.GetType().Name);
            }
        }
    }

    private static void Persist(string path, string directory, string entry)
    {
        _createDirectory(directory);
        RotateIfNeeded(path);
        _appendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
    }

    private static string FormatTiming(TimingSample sample, List<long> values, string scope, long totalObserved)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        long percentile(double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
        return $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] stage=TerminalTiming.{SanitizeScalar(sample.Stage)}; " +
            $"scope={scope}; count={sorted.Length}; totalObserved={totalObserved}; runId={SanitizeScalar(PerformanceTrace.RunId)}; " +
            $"meanUs={(long)sorted.Average()}; p50Us={percentile(0.5)}; " +
            $"p95Us={percentile(0.95)}; p99Us={percentile(0.99)}; maxUs={sorted[^1]}; " +
            $"terminalSessionGeneration={sample.SessionGeneration}; rendererGeneration={sample.RendererGeneration}; " +
            $"sequence={sample.Sequence}; queueDepth={sample.QueueDepth}; envelopeCount={sample.EnvelopeCount}; contentOmitted=true";
    }

    private static string FormatEntry(TraceRecord record)
    {
        var builder = new StringBuilder(1024);
        builder.Append('[').Append(record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ");
        builder.Append("stage=").Append(SanitizeScalar(record.Stage)).Append("; ");
        builder.Append("traceSequence=").Append(record.Sequence).Append("; ");
        builder.Append("startupAttemptId=").Append(TerminalStartupTrace.StartupAttemptId).Append("; ");
        builder.Append("pid=").Append(Environment.ProcessId).Append("; ");
        builder.Append("managedThreadId=").Append(record.ThreadId).Append("; ");
        builder.Append("apartmentState=").Append(record.ApartmentState).Append("; ");
        builder.Append("uiDispatcherAccess=").Append(record.UiSnapshot.DispatcherAccess?.ToString() ?? "unknown").Append("; ");
        builder.Append("uiDispatcherThreadId=").Append(record.UiSnapshot.DispatcherThreadId?.ToString() ?? "unknown").Append("; ");
        builder.Append("sourceMember=").Append(SanitizeScalar(record.MemberName)).Append("; ");
        builder.Append("sourceFile=").Append(SanitizeScalar(record.SourceFilePath)).Append("; ");
        builder.Append("sourceLine=").Append(record.SourceLineNumber).AppendLine();
        if (record.Metadata is not null)
            foreach (var item in record.Metadata)
                builder.Append("metadata.").Append(SanitizeScalar(item.Key)).Append('=')
                    .Append(SanitizeMetadataValue(item.Key, item.Value)).AppendLine();
        if (record.Exception is not null)
        {
            builder.Append("exception.type=").Append(record.Exception.GetType().FullName).AppendLine();
            builder.Append("exception.toString=").AppendLine().Append(record.Exception).AppendLine();
        }
        return BoundEntry(builder.ToString());
    }

    private static bool IsBoundaryStage(string stage) =>
        stage.Contains("Exception", StringComparison.Ordinal) ||
        stage.Contains("Dropped", StringComparison.Ordinal) ||
        stage.Contains("Rejected", StringComparison.Ordinal) ||
        stage.Contains("Failure", StringComparison.Ordinal) ||
        stage.Contains("Aborted", StringComparison.Ordinal) ||
        stage.Contains("Recovery", StringComparison.Ordinal) ||
        stage.Contains("Shutdown", StringComparison.Ordinal) ||
        stage.Contains("Startup", StringComparison.Ordinal);

    private sealed record TraceRecord(
        string Stage, Exception? Exception, KeyValuePair<string, object?>[]? Metadata,
        string? MemberName, string? SourceFilePath, int SourceLineNumber,
        DateTimeOffset Timestamp, int ThreadId, string ApartmentState,
        TerminalCriticalUiThreadSnapshot UiSnapshot)
    {
        public TaskCompletionSource<bool>? Completion { get; }
        public TimingSample? Timing { get; }
        public long Sequence { get; set; }
        public TraceRecord(TaskCompletionSource<bool> completion)
            : this(string.Empty, null, null, null, null, 0, default, 0, string.Empty, default)
            => Completion = completion;
        public TraceRecord(TimingSample timing)
            : this(string.Empty, null, null, null, null, 0, default, 0, string.Empty, default)
            => Timing = timing;
    }

    private readonly record struct TimingSample(string Stage, long Microseconds, int SessionGeneration,
        int RendererGeneration, long Sequence, int QueueDepth, int EnvelopeCount);

    private static TerminalCriticalUiThreadSnapshot CaptureUiThreadSnapshot()
    {
        try
        {
            return _uiThreadSnapshotProvider();
        }
        catch
        {
            return default;
        }
    }

    private static string CaptureApartmentState()
    {
        try
        {
            return Thread.CurrentThread.GetApartmentState().ToString();
        }
        catch (Exception ex)
        {
            return $"unknown:{ex.GetType().Name}";
        }
    }

    private static void RotateIfNeeded(string tracePath)
    {
        if (!_fileExists(tracePath) || _fileLength(tracePath) < MaximumTraceFileBytes)
        {
            return;
        }

        var archivePath = Path.Combine(Path.GetDirectoryName(tracePath)!, ArchiveFileName);
        if (_fileExists(archivePath))
        {
            _deleteFile(archivePath);
        }

        _moveFile(tracePath, archivePath);
    }

    private static string BoundEntry(string entry)
    {
        if (entry.Length <= MaximumEntryCharacters)
        {
            return entry;
        }

        var remaining = MaximumEntryCharacters - TruncationMarker.Length;
        var prefixLength = Math.Max(1, remaining / 2);
        var suffixLength = Math.Max(1, remaining - prefixLength);
        return entry[..prefixLength] + TruncationMarker + entry[^suffixLength..];
    }

    private static string SanitizeMetadataValue(string key, object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (IsContentKey(key))
        {
            return "[omitted]";
        }

        return SanitizeScalar(value.ToString());
    }

    private static string SanitizeScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static bool IsContentKey(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Equals("payload", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("script", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("text", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("content", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("scripttext", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("terminaltext", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("rawoutput", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateDefaultTracePath()
    {
        return Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs", TraceFileName);
    }

    private static void MergeMetadata(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var item in source)
        {
            target[item.Key] = item.Value;
        }
    }
}
