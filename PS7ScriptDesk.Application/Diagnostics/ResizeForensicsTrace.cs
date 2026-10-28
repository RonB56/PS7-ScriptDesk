using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Temporary, opt-in Phase 2C resize-boundary diagnostics. Disabled unless
/// PS7SCRIPTDESK_RESIZE_FORENSICS=1. This class never changes terminal data.
/// </summary>
public static class ResizeForensicsTrace
{
    private const int PreviewLimit = 200;
    private const int ScreenSnapshotLimit = 128;
    private static readonly object Sync = new();
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private static readonly string SessionGuid = Guid.NewGuid().ToString("N");
    private static readonly ConcurrentDictionary<string, StreamWriter> Writers = new(StringComparer.Ordinal);
    private static long _sequence;
    private static int? _authoritativePid;
    private static string? _root;
    private static int _screenSnapshotCount;

    public static bool Enabled => string.Equals(Environment.GetEnvironmentVariable("PS7SCRIPTDESK_RESIZE_FORENSICS"), "1", StringComparison.Ordinal);
    public static string CurrentSessionGuid => SessionGuid;

    public static void SetAuthoritativeProcessId(int processId)
    {
        if (!Enabled || processId <= 0) return;
        _authoritativePid = processId;
        EnsureManifest();
    }

    public static long Record(
        string boundary,
        int generation,
        string payload,
        int? processId = null,
        long? parentSequence = null,
        long? submissionId = null,
        long? barrierInsertionId = null,
        long? barrierReleaseId = null,
        bool replayOrRecovery = false,
        long resizeGeneration = 0,
        int? rendererGeneration = null,
        int? cols = null,
        int? rows = null,
        int? cursorX = null,
        int? cursorY = null,
        int? baseY = null,
        int? viewportY = null,
        string file = "fingerprints.jsonl")
    {
        if (!Enabled || string.IsNullOrEmpty(payload)) return 0;
        try
        {
            EnsureManifest();
            var sequence = Interlocked.Increment(ref _sequence);
            var metadata = new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["sessionGuid"] = SessionGuid,
                ["generation"] = generation,
                ["authoritativePsesPid"] = processId ?? _authoritativePid,
                ["globalSequenceId"] = sequence,
                ["parentSequenceId"] = parentSequence,
                ["sourceBoundary"] = boundary,
                ["payloadLength"] = payload.Length,
                ["crCount"] = payload.Count(static c => c == '\r'),
                ["lfCount"] = payload.Count(static c => c == '\n'),
                ["escCount"] = payload.Count(static c => c == '\u001b'),
                ["csiCount"] = CountCsi(payload),
                ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                ["preview"] = SanitizePreview(payload),
                ["replayOrRecovery"] = replayOrRecovery,
                ["resizeGeneration"] = resizeGeneration,
                ["submissionId"] = submissionId,
                ["barrierInsertionId"] = barrierInsertionId,
                ["barrierReleaseId"] = barrierReleaseId,
                ["rendererGeneration"] = rendererGeneration,
                ["cols"] = cols,
                ["rows"] = rows,
                ["cursorX"] = cursorX,
                ["cursorY"] = cursorY,
                ["baseY"] = baseY,
                ["viewportY"] = viewportY
            };
            Append(file, metadata);
            return sequence;
        }
        catch { return 0; }
    }

    public static void RecordBarrier(string boundary, int generation, long insertionId, long releaseId, string payload, long resizeGeneration, int? processId = null)
    {
        Record(boundary, generation, payload, processId, barrierInsertionId: insertionId, barrierReleaseId: releaseId, resizeGeneration: resizeGeneration, file: "barrier.jsonl");
    }

    public static void RecordResizeEvent(string boundary, int generation, long resizeGeneration, int cols, int rows, int rendererGeneration, int? cursorX = null, int? cursorY = null, int? baseY = null, int? viewportY = null)
    {
        if (!Enabled) return;
        try
        {
            EnsureManifest();
            Append("resize-events.jsonl", new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["sessionGuid"] = SessionGuid,
                ["generation"] = generation,
                ["globalSequenceId"] = Interlocked.Increment(ref _sequence),
                ["sourceBoundary"] = boundary,
                ["resizeGeneration"] = resizeGeneration,
                ["rendererGeneration"] = rendererGeneration,
                ["cols"] = cols,
                ["rows"] = rows,
                ["cursorX"] = cursorX,
                ["cursorY"] = cursorY,
                ["baseY"] = baseY,
                ["viewportY"] = viewportY
            });
        }
        catch { }
    }

    public static void RecordScreenSnapshot(string boundary, int generation, object snapshot)
    {
        if (!Enabled || Interlocked.Increment(ref _screenSnapshotCount) > ScreenSnapshotLimit) return;
        try
        {
            EnsureManifest();
            Append("xterm-screen.jsonl", new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["sessionGuid"] = SessionGuid,
                ["generation"] = generation,
                ["globalSequenceId"] = Interlocked.Increment(ref _sequence),
                ["sourceBoundary"] = boundary,
                ["snapshot"] = snapshot
            });
        }
        catch { }
    }

    public static string SanitizePreview(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, PreviewLimit));
        foreach (var character in value)
        {
            if (builder.Length >= PreviewLimit) break;
            if (character is '\r' or '\n' or '\t') builder.Append(character switch { '\r' => '↵', '\n' => '␊', _ => '⇥' });
            else if (char.IsControl(character)) builder.Append('·');
            else builder.Append(character);
        }
        return builder.ToString();
    }

    private static int CountCsi(string value)
    {
        var count = 0;
        for (var index = 0; index + 1 < value.Length; index++)
            if (value[index] == '\u001b' && value[index + 1] == '[') count++;
        return count;
    }

    private static void EnsureManifest()
    {
        if (!Enabled || _root is not null) return;
        lock (Sync)
        {
            if (_root is not null) return;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS7ScriptDesk", "DeveloperDebugging", "Phase2CResizeFingerprint", DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(root);
            _root = root;
            File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new
            {
                runId = RunId,
                sessionGuid = SessionGuid,
                processId = Environment.ProcessId,
                authoritativePsesPid = _authoritativePid,
                startedUtc = DateTimeOffset.UtcNow,
                featureGate = "PS7SCRIPTDESK_RESIZE_FORENSICS=1"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Append(string file, IReadOnlyDictionary<string, object?> record)
    {
        EnsureManifest();
        if (_root is null) return;
        var writer = Writers.GetOrAdd(file, name => new StreamWriter(new FileStream(Path.Combine(_root, name), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true });
        lock (writer) writer.WriteLine(JsonSerializer.Serialize(record));
    }
}
