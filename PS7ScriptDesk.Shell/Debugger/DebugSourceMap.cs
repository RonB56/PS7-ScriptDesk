using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace PS7ScriptDesk.Shell.Debug;

public enum DebugSourceMappingStatus
{
    Exact,
    SnapshotMapped,
    RevisionMismatch,
    MissingSource,
    RuntimeGenerated,
    Unmapped,
    Invalidated
}

/// <summary>
/// The immutable source identity contract for one debugger session.
/// Runtime paths are keys; editor document identity and the launch revision are
/// the authority for deciding whether a line may be shown in an editor.
/// </summary>
public sealed class DebugSourceMap
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _invalidated;

    public DebugSourceMap(Guid sessionId)
    {
        SessionId = sessionId == Guid.Empty
            ? throw new ArgumentException("A debugger session identity is required.", nameof(sessionId))
            : sessionId;
    }

    public Guid SessionId { get; }

    public bool IsValid => Volatile.Read(ref _invalidated) == 0;

    public void Register(
        string runtimePath,
        Guid sourceDocumentId,
        long sourceRevision,
        string? editorPath,
        string executionText,
        bool isSnapshot)
    {
        if (!IsValid) throw new InvalidOperationException("The debugger source map has been invalidated.");
        if (sourceDocumentId == Guid.Empty) throw new ArgumentException("A source document identity is required.", nameof(sourceDocumentId));
        if (sourceRevision < 0) throw new ArgumentOutOfRangeException(nameof(sourceRevision));

        var normalizedRuntimePath = NormalizePath(runtimePath);
        var normalizedEditorPath = string.IsNullOrWhiteSpace(editorPath) ? null : NormalizePath(editorPath);
        _entries[normalizedRuntimePath] = new Entry(
            sourceDocumentId,
            sourceRevision,
            normalizedEditorPath,
            ComputeTextHash(executionText),
            isSnapshot);
    }

    public DebugMappedSourceLocation Map(
        string? runtimePath,
        int runtimeLine,
        Func<Guid, long?>? currentRevisionProvider = null)
    {
        if (!IsValid)
        {
            return DebugMappedSourceLocation.Invalidated(runtimePath, runtimeLine);
        }

        if (string.IsNullOrWhiteSpace(runtimePath) || runtimeLine <= 0)
        {
            return DebugMappedSourceLocation.Unmapped(runtimePath, runtimeLine);
        }

        string normalizedRuntimePath;
        try
        {
            normalizedRuntimePath = NormalizePath(runtimePath);
        }
        catch
        {
            return DebugMappedSourceLocation.Unmapped(runtimePath, runtimeLine);
        }

        if (!_entries.TryGetValue(normalizedRuntimePath, out var entry))
        {
            return DebugMappedSourceLocation.RuntimeGenerated(runtimePath, runtimeLine);
        }

        var currentRevision = currentRevisionProvider?.Invoke(entry.SourceDocumentId);
        if (currentRevision.HasValue && currentRevision.Value != entry.SourceRevision)
        {
            return new DebugMappedSourceLocation(
                runtimePath,
                runtimeLine,
                entry.SourceDocumentId,
                entry.EditorPath,
                runtimeLine,
                DebugSourceMappingStatus.RevisionMismatch,
                CanNavigate: false,
                entry.SourceRevision,
                currentRevision.Value,
                entry.IsSnapshot);
        }

        var status = entry.IsSnapshot ? DebugSourceMappingStatus.SnapshotMapped : DebugSourceMappingStatus.Exact;
        return new DebugMappedSourceLocation(
            runtimePath,
            runtimeLine,
            entry.SourceDocumentId,
            entry.EditorPath,
            runtimeLine,
            status,
            CanNavigate: true,
            entry.SourceRevision,
            currentRevision,
            entry.IsSnapshot);
    }

    public void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

    public bool IsDocumentRevisionMismatch(Guid sourceDocumentId, Func<Guid, long?>? currentRevisionProvider)
    {
        if (!IsValid || sourceDocumentId == Guid.Empty || currentRevisionProvider is null)
        {
            return false;
        }

        var currentRevision = currentRevisionProvider(sourceDocumentId);
        return currentRevision.HasValue && _entries.Values.Any(entry =>
            entry.SourceDocumentId == sourceDocumentId && entry.SourceRevision != currentRevision.Value);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A source path is required.", nameof(path));
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string ComputeTextHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record Entry(
        Guid SourceDocumentId,
        long SourceRevision,
        string? EditorPath,
        string ExecutionTextHash,
        bool IsSnapshot);
}

public sealed record DebugMappedSourceLocation(
    string? RuntimePath,
    int RuntimeLine,
    Guid? SourceDocumentId,
    string? EditorPath,
    int? EditorLine,
    DebugSourceMappingStatus Status,
    bool CanNavigate,
    long? CapturedRevision,
    long? CurrentRevision,
    bool IsSnapshot)
{
    public static DebugMappedSourceLocation Unmapped(string? runtimePath, int runtimeLine)
        => new(runtimePath, runtimeLine, null, null, null, DebugSourceMappingStatus.Unmapped, false, null, null, false);

    public static DebugMappedSourceLocation RuntimeGenerated(string? runtimePath, int runtimeLine)
        => new(runtimePath, runtimeLine, null, null, null, DebugSourceMappingStatus.RuntimeGenerated, false, null, null, false);

    public static DebugMappedSourceLocation Invalidated(string? runtimePath, int runtimeLine)
        => new(runtimePath, runtimeLine, null, null, null, DebugSourceMappingStatus.Invalidated, false, null, null, false);
}
