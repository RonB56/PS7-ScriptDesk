using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics;

public readonly record struct TerminalCriticalUiThreadSnapshot(bool? DispatcherAccess, int? DispatcherThreadId);

public static class TerminalCriticalTrace
{
    private const long MaximumTraceFileBytes = 1 * 1024 * 1024;
    private const int MaximumEntryCharacters = 64 * 1024;
    private const string TraceFileName = "TerminalCriticalTrace.log";
    private const string ArchiveFileName = "TerminalCriticalTrace.log.1";
    private const string TruncationMarker = "\r\n[Terminal critical trace entry truncated by bounded diagnostics policy.]\r\n";
    private static readonly object SyncRoot = new();
    private static Func<string> _tracePathProvider = CreateDefaultTracePath;
    private static Action<string> _createDirectory = static path => Directory.CreateDirectory(path);
    private static Action<string, string, Encoding> _appendAllText = static (path, text, encoding) => File.AppendAllText(path, text, encoding);
    private static Func<string, bool> _fileExists = static path => File.Exists(path);
    private static Func<string, long> _fileLength = static path => new FileInfo(path).Length;
    private static Action<string> _deleteFile = static path => File.Delete(path);
    private static Action<string, string> _moveFile = static (source, destination) => File.Move(source, destination);
    private static Func<TerminalCriticalUiThreadSnapshot> _uiThreadSnapshotProvider = static () => default;

    public static string CurrentTracePath => _tracePathProvider();

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
            ["terminalOutputDispatchImplementation"] = "MainWindowDispatcherEnvelopeQueue",
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
        _tracePathProvider = CreateDefaultTracePath;
        _createDirectory = static path => Directory.CreateDirectory(path);
        _appendAllText = static (path, text, encoding) => File.AppendAllText(path, text, encoding);
        _fileExists = static path => File.Exists(path);
        _fileLength = static path => new FileInfo(path).Length;
        _deleteFile = static path => File.Delete(path);
        _moveFile = static (source, destination) => File.Move(source, destination);
        _uiThreadSnapshotProvider = static () => default;
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
            var tracePath = CurrentTracePath;
            var directory = Path.GetDirectoryName(tracePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var builder = new StringBuilder(1024);
            var uiSnapshot = CaptureUiThreadSnapshot();
            builder.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ");
            builder.Append("stage=").Append(SanitizeScalar(stage)).Append("; ");
            builder.Append("pid=").Append(Environment.ProcessId).Append("; ");
            builder.Append("managedThreadId=").Append(Environment.CurrentManagedThreadId).Append("; ");
            builder.Append("apartmentState=").Append(CaptureApartmentState()).Append("; ");
            builder.Append("uiDispatcherAccess=").Append(uiSnapshot.DispatcherAccess?.ToString() ?? "unknown").Append("; ");
            builder.Append("uiDispatcherThreadId=").Append(uiSnapshot.DispatcherThreadId?.ToString() ?? "unknown").Append("; ");
            builder.Append("sourceMember=").Append(SanitizeScalar(memberName)).Append("; ");
            builder.Append("sourceFile=").Append(SanitizeScalar(sourceFilePath)).Append("; ");
            builder.Append("sourceLine=").Append(sourceLineNumber).AppendLine();

            if (metadata is not null)
            {
                foreach (var item in metadata)
                {
                    builder.Append("metadata.")
                        .Append(SanitizeScalar(item.Key))
                        .Append('=')
                        .Append(SanitizeMetadataValue(item.Key, item.Value))
                        .AppendLine();
                }
            }

            if (exception is not null)
            {
                builder.Append("exception.type=").Append(exception.GetType().FullName).AppendLine();
                builder.Append("exception.toString=").AppendLine();
                builder.Append(exception);
                builder.AppendLine();
            }

            var entry = BoundEntry(builder.ToString());
            lock (SyncRoot)
            {
                _createDirectory(directory);
                RotateIfNeeded(tracePath);
                _appendAllText(tracePath, entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalCriticalTrace] Diagnostic write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
        try
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
        catch
        {
        }
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
