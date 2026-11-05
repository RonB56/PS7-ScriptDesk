using System.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Bounded, content-free summaries of ConPTY frames around prompt recovery.</summary>
internal sealed class PromptRecoveryFrameDiagnostics(int capacity = 256)
{
    private readonly object _sync = new();
    private readonly Queue<PromptFrameDiagnostic> _frames = new();
    private readonly int _capacity = capacity;
    private long _sequence;
    private long _evicted;
    private bool _inCsi;
    private bool _escapePending;
    private long? _escapeStartFrame;
    private readonly List<byte> _csiBytes = new(16);
    private long? _csiStartFrame;

    public PromptFrameDiagnostic Record(ReadOnlySpan<byte> bytes, int generation, Guid? requestId,
        bool candidate, bool accepted, string reason, bool cursorVisible, bool authoritative)
    {
        lock (_sync)
        {
            var seq = ++_sequence;
            var starts = false;
            var ends = false;
            var spans = false;
            var types = new HashSet<string>(StringComparer.Ordinal);
            string? csiHex = null;
            foreach (var value in bytes)
            {
                if (!_inCsi && !_escapePending && value == 0x1b) { _escapePending = true; _escapeStartFrame = seq; continue; }
                if (_escapePending)
                {
                    _escapePending = false;
                    if (value == (byte)'[') { _inCsi = true; _csiBytes.Clear(); _csiBytes.Add(0x1b); _csiBytes.Add(value); _csiStartFrame = _escapeStartFrame ?? seq; starts = true; }
                    _escapeStartFrame = null;
                    continue;
                }
                if (!_inCsi) continue;
                if (_csiBytes.Count < 16) _csiBytes.Add(value);
                if (value is >= 0x40 and <= 0x7e)
                {
                    _inCsi = false; ends = true;
                    types.Add(value switch { (byte)'H' or (byte)'f' => "CSI-CursorPosition", (byte)'m' => "CSI-SGR", _ => "CSI-Other" });
                    csiHex = Convert.ToHexString(_csiBytes.ToArray());
                    if (_csiStartFrame.HasValue && _csiStartFrame.Value != seq) spans = true;
                    _csiStartFrame = null;
                }
            }
            var record = new PromptFrameDiagnostic(seq, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), generation,
                requestId, bytes.Length, starts, ends, spans || ((_inCsi || _escapePending) && _csiStartFrame != seq), _inCsi || _escapePending,
                string.Join(',', types), csiHex, candidate, accepted, reason, cursorVisible, authoritative);
            if (_frames.Count == _capacity) { _frames.Dequeue(); _evicted++; }
            _frames.Enqueue(record);
            return record;
        }
    }

    public (PromptFrameDiagnostic[] Frames, long Evicted, int Capacity, long TotalRecorded) Snapshot()
    {
        lock (_sync) return (_frames.ToArray(), _evicted, _capacity, _sequence);
    }
}

internal sealed record PromptFrameDiagnostic(long Sequence, DateTimeOffset Utc, long MonotonicTimestamp,
    int Generation, Guid? RequestId, int ByteCount, bool CsiStarted, bool CsiEnded, bool CsiSpannedFrames,
    bool PendingControlSequence, string SequenceTypes, string? ControlHex, bool PromptCandidate,
    bool PromptAccepted, string RejectionReason, bool CursorVisible, bool Authoritative);
