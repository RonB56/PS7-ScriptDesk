using System.Threading.Channels;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Bounded output ledger for one coordinator epoch. GenerationId is an internal epoch; it is
/// deliberately not a PSES session id, backend generation, renderer generation, or transport id.
/// The integration layer must bind those identities explicitly before using this coordinator.
/// </summary>
public interface IBoundedOutputCoordinator : IAsyncDisposable
{
    Guid GenerationId { get; }
    long FirstFrameSequence { get; }
    int? SessionGeneration { get; }
    OutputCoordinatorSnapshot Snapshot { get; }
    ValueTask<OutputFrame> AdmitAsync(string source, string text, CancellationToken cancellationToken = default);
    ValueTask<OutputFrame> AdmitAsync(Guid expectedCoordinatorEpoch, int expectedSessionGeneration, string source, Guid? requestId, long sourceSequence, string text, CancellationToken cancellationToken = default);
    bool BindSessionGeneration(Guid expectedCoordinatorEpoch, int sessionGeneration);
    ValueTask<OutputFrame?> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<OutputFrame>> ReadBatchAsync(int maximumFrames, CancellationToken cancellationToken = default);
    bool MarkRendererSubmitted(OutputAcknowledgment acknowledgment);
    bool MarkRendererFrameReady(OutputAcknowledgment acknowledgment, out bool dispatchSubmitted);
    bool MarkRendererBatchSubmitted(IReadOnlyList<OutputAcknowledgment> acknowledgments);
    AcknowledgmentResult Acknowledge(OutputAcknowledgment acknowledgment);
    bool RetireGeneration(Guid generationId, string reason);
    bool RetireGeneration(Guid generationId, int? expectedSessionGeneration, string reason);
    bool TryReadDiagnostic(out OutputCoordinatorDiagnostic? diagnostic);
}

public sealed record OutputAcknowledgment(Guid GenerationId, long Sequence);

/// <summary>The immutable output and its internal coordinator-epoch identity.</summary>
public sealed record OutputFrame(Guid GenerationId, long Sequence, string Source, string Text)
{
    public int Utf16CodeUnits => Text.Length;
    public OutputAcknowledgment Acknowledgment => new(GenerationId, Sequence);
    public int? SessionGeneration { get; init; }
    public Guid? RequestId { get; init; }
    public long SourceSequence { get; init; }
}

public enum AcknowledgmentResult
{
    Acknowledged,
    Duplicate,
    StaleGeneration,
    Late,
    Invalid,
    Unknown,
    NotRendererSubmitted,
    Disposed
}

public sealed record OutputCoordinatorSnapshot(
    Guid GenerationId,
    int QueuedFrames,
    int OutstandingFrames,
    int WaitingProducers,
    long ReservedUtf16CodeUnits,
    long PendingUtf16CodeUnits,
    long AdmittedFrames,
    long AcknowledgedFrames,
    long RetiredFrames,
    long AdmittedUtf16CodeUnits,
    long AcknowledgedUtf16CodeUnits,
    long RetiredUtf16CodeUnits,
    long SaturationCount,
    long FailureCount,
    long DroppedDiagnosticRecords,
    bool IsDisposed);

public sealed record OutputCoordinatorDiagnostic(
    DateTimeOffset Timestamp,
    string Kind,
    Guid GenerationId,
    long Sequence,
    long Utf16CodeUnits,
    string Detail);

/// <summary>
/// A single authoritative capacity ledger. ACK capacity is returned only after the explicit
/// renderer-submission boundary. The caller must invoke MarkRendererSubmitted only after ordered
/// dispatch has completed; it may aggregate/split renderer batches, but must mark each frame only
/// when all of its bytes have been submitted. Renderer completion ACK then releases the credit.
/// Diagnostics are best-effort and never persist synchronously.
/// </summary>
public sealed class BoundedOutputCoordinator : IBoundedOutputCoordinator
{
    public const int MaximumUtf16CodeUnits = 524_288;
    public const int MaximumFrames = 8_192;
    public const int MaximumPendingUtf16CodeUnits = 524_288;
    private const int MaximumWaitingProducers = 256;
    private const int DiagnosticCapacity = 256;
    private const int DuplicateHistoryCapacity = MaximumFrames;

    private readonly object _sync = new();
    private readonly Queue<FrameEntry> _queue = new();
    private readonly Dictionary<long, FrameEntry> _outstanding = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly Queue<long> _recentlyAcknowledgedOrder = new();
    private readonly HashSet<long> _recentlyAcknowledged = new();
    private readonly Channel<OutputCoordinatorDiagnostic> _diagnostics = Channel.CreateBounded<OutputCoordinatorDiagnostic>(
        new BoundedChannelOptions(DiagnosticCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private TaskCompletionSource<bool> _readPulse = NewPulse();
    private Guid _generationId = Guid.NewGuid();
    private int? _sessionGeneration;
    private long _nextSequence;
    private long _firstFrameSequence = 1;
    private long _reservedUtf16CodeUnits;
    private long _pendingUtf16CodeUnits;
    private long _admittedFrames;
    private long _acknowledgedFrames;
    private long _retiredFrames;
    private long _admittedUtf16CodeUnits;
    private long _acknowledgedUtf16CodeUnits;
    private long _retiredUtf16CodeUnits;
    private long _saturationCount;
    private long _failureCount;
    private long _droppedDiagnosticRecords;
    private long? _dispatchSequence;
    private List<long>? _dispatchBatchSequences;
    private HashSet<long>? _dispatchBatchSequenceSet;
    private HashSet<long>? _dispatchReadySequences;
    private bool _readerActive;
    private Guid? _readerGeneration;
    private bool _disposed;

    public Guid GenerationId { get { lock (_sync) return _generationId; } }
    public long FirstFrameSequence { get { lock (_sync) return _firstFrameSequence; } }
    public int? SessionGeneration { get { lock (_sync) return _sessionGeneration; } }

    public OutputCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return new(_generationId, _queue.Count, _outstanding.Count, _waiters.Count,
                    _reservedUtf16CodeUnits, _pendingUtf16CodeUnits, _admittedFrames, _acknowledgedFrames,
                    _retiredFrames, _admittedUtf16CodeUnits, _acknowledgedUtf16CodeUnits,
                    _retiredUtf16CodeUnits, _saturationCount, _failureCount, _droppedDiagnosticRecords, _disposed);
        }
    }

    public ValueTask<OutputFrame> AdmitAsync(string source, string text, CancellationToken cancellationToken = default)
        => AdmitCoreAsync(null, null, source, null, 0, text, cancellationToken);

    /// <summary>
    /// Admits output only when both the caller's coordinator epoch and the authoritative
    /// PSES session generation still match. Identity validation and credit reservation share
    /// the same lock, so retirement cannot relabel a stale producer as current.
    /// </summary>
    public ValueTask<OutputFrame> AdmitAsync(
        Guid expectedCoordinatorEpoch,
        int expectedSessionGeneration,
        string source,
        Guid? requestId,
        long sourceSequence,
        string text,
        CancellationToken cancellationToken = default)
        => AdmitCoreAsync(expectedCoordinatorEpoch, expectedSessionGeneration, source, requestId, sourceSequence, text, cancellationToken);

    public bool BindSessionGeneration(Guid expectedCoordinatorEpoch, int sessionGeneration)
    {
        if (expectedCoordinatorEpoch == Guid.Empty || sessionGeneration < 0) return false;
        lock (_sync)
        {
            if (_disposed || expectedCoordinatorEpoch != _generationId) return false;
            if (_sessionGeneration is null && (_outstanding.Count > 0 || _waiters.Count > 0))
                return false;
            if (_sessionGeneration is { } active && active != sessionGeneration)
                return false;
            _sessionGeneration = sessionGeneration;
            WriteDiagnostic("SessionGenerationBound", 0, 0, "Bound coordinator epoch to the authoritative PSES session generation.");
            return true;
        }
    }

    private ValueTask<OutputFrame> AdmitCoreAsync(
        Guid? expectedCoordinatorEpoch,
        int? expectedSessionGeneration,
        string source,
        Guid? requestId,
        long sourceSequence,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(text);
        if (source.Length is 0 or > 64)
            return ValueTask.FromException<OutputFrame>(new ArgumentOutOfRangeException(nameof(source), "Output source identifiers must contain 1 to 64 characters."));
        if (text.Length > MaximumUtf16CodeUnits)
            return ValueTask.FromException<OutputFrame>(new ArgumentOutOfRangeException(nameof(text), "A frame exceeds the coordinator's UTF-16 capacity."));
        cancellationToken.ThrowIfCancellationRequested();

        Waiter? waiter;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (expectedCoordinatorEpoch.HasValue &&
                (expectedCoordinatorEpoch.Value != _generationId ||
                 expectedSessionGeneration != _sessionGeneration))
            {
                _failureCount++;
                WriteDiagnostic("StaleProducerRejected", 0, text.Length,
                    "Producer coordinator epoch or PSES session generation no longer matches the active binding.", expectedCoordinatorEpoch.Value);
                return ValueTask.FromException<OutputFrame>(new InvalidOperationException("The output producer belongs to a retired coordinator epoch or PSES session generation."));
            }
            if (_sessionGeneration.HasValue && !expectedCoordinatorEpoch.HasValue)
            {
                _failureCount++;
                WriteDiagnostic("UnboundProducerRejected", 0, text.Length,
                    "Session-bound production admission requires an expected coordinator epoch and PSES generation.");
                return ValueTask.FromException<OutputFrame>(new InvalidOperationException("Session-bound output admission requires the source identity contract."));
            }
            if (_waiters.Count == 0 && HasCapacity(text.Length))
                return ValueTask.FromResult(ReserveAndEnqueue(source, text, expectedSessionGeneration, requestId, sourceSequence));

            _saturationCount++;
            WriteDiagnostic("Saturated", 0, text.Length, "Admission is waiting for acknowledged capacity.");
            if (_waiters.Count >= MaximumWaitingProducers || _pendingUtf16CodeUnits + text.Length > MaximumPendingUtf16CodeUnits)
            {
                _failureCount++;
                var overCount = _waiters.Count >= MaximumWaitingProducers;
                WriteDiagnostic("AdmissionRejected", 0, text.Length, overCount
                    ? "The bounded waiting-producer limit was reached."
                    : "The bounded pending-text budget was reached.");
                return ValueTask.FromException<OutputFrame>(new InvalidOperationException(overCount
                    ? "The bounded output coordinator has reached its waiting-producer limit."
                    : "The bounded output coordinator has reached its pending-text budget."));
            }

            waiter = new Waiter(source, text, cancellationToken, expectedCoordinatorEpoch, expectedSessionGeneration, requestId, sourceSequence);
            waiter.Node = _waiters.AddLast(waiter);
            _pendingUtf16CodeUnits += text.Length;
        }

        // Register outside _sync: a token already canceled can invoke the callback inline.
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(static state =>
            {
                var tuple = ((BoundedOutputCoordinator Owner, Waiter Waiter))state!;
                tuple.Owner.CancelWaiter(tuple.Waiter);
            }, (this, waiter));

            var unregister = false;
            lock (_sync)
            {
                if (waiter.State == WaiterState.Waiting)
                    waiter.Registration = registration;
                else
                    unregister = true;
            }
            if (unregister) registration.Unregister();
        }

        return new ValueTask<OutputFrame>(waiter.Completion.Task);
    }

    public async ValueTask<OutputFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        Guid readerGeneration;
        lock (_sync)
        {
            if (_disposed) return null;
            readerGeneration = _generationId;
            if ((_readerActive && _readerGeneration == _generationId) || _dispatchSequence.HasValue || _dispatchBatchSequences is not null)
                throw new InvalidOperationException("Only one ordered presentation consumer may read, and the previous frame must be submitted before another read.");
            _readerActive = true;
            _readerGeneration = readerGeneration;
        }

        try
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_disposed || readerGeneration != _generationId) return null;
                    if (_dispatchSequence.HasValue || _dispatchBatchSequences is not null)
                        throw new InvalidOperationException("Only one ordered presentation consumer may read at a time.");
                    if (_queue.Count > 0)
                    {
                        var entry = _queue.Dequeue();
                        entry.State = FrameState.Dequeued;
                        _dispatchSequence = entry.Frame.Sequence;
                        return entry.Frame;
                    }
                    wait = _readPulse.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_readerGeneration == readerGeneration)
                {
                    _readerActive = false;
                    _readerGeneration = null;
                }
            }
        }
    }

    /// <summary>
    /// Dequeues a bounded ordered group for a renderer batch assembler. The group must be marked
    /// submitted atomically with MarkRendererBatchSubmitted before another read is allowed.
    /// </summary>
    public async ValueTask<IReadOnlyList<OutputFrame>> ReadBatchAsync(int maximumFrames, CancellationToken cancellationToken = default)
    {
        if (maximumFrames <= 0 || maximumFrames > MaximumFrames) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        Guid readerGeneration;
        lock (_sync)
        {
            if (_disposed) return Array.Empty<OutputFrame>();
            readerGeneration = _generationId;
            if ((_readerActive && _readerGeneration == _generationId) || _dispatchSequence.HasValue || _dispatchBatchSequences is not null)
                throw new InvalidOperationException("Only one ordered presentation consumer may read, and the previous dispatch must be submitted before another read.");
            _readerActive = true;
            _readerGeneration = readerGeneration;
        }

        try
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_disposed || readerGeneration != _generationId) return Array.Empty<OutputFrame>();
                    if (_dispatchSequence.HasValue || _dispatchBatchSequences is not null)
                        throw new InvalidOperationException("Only one ordered presentation consumer may read at a time.");
                    if (_queue.Count > 0)
                    {
                        var count = Math.Min(maximumFrames, _queue.Count);
                        var frames = new List<OutputFrame>(count);
                        _dispatchBatchSequences = new List<long>(count);
                        _dispatchBatchSequenceSet = new HashSet<long>();
                        for (var i = 0; i < count; i++)
                        {
                            var entry = _queue.Dequeue();
                            entry.State = FrameState.Dequeued;
                            frames.Add(entry.Frame);
                            _dispatchBatchSequences.Add(entry.Frame.Sequence);
                            _dispatchBatchSequenceSet.Add(entry.Frame.Sequence);
                        }
                        return frames;
                    }
                    wait = _readPulse.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_readerGeneration == readerGeneration)
                {
                    _readerActive = false;
                    _readerGeneration = null;
                }
            }
        }
    }

    /// <summary>Authorizes advancement to the next frame after ordered renderer submission.</summary>
    public bool MarkRendererSubmitted(OutputAcknowledgment acknowledgment)
    {
        if (acknowledgment is null || acknowledgment.Sequence <= 0 || acknowledgment.GenerationId == Guid.Empty) return false;
        lock (_sync)
        {
            if (_disposed || acknowledgment.GenerationId != _generationId ||
                !_outstanding.TryGetValue(acknowledgment.Sequence, out var entry) ||
                entry.State != FrameState.Dequeued ||
                !(_dispatchSequence == acknowledgment.Sequence ||
                  (_dispatchBatchSequences is { Count: 1 } && _dispatchBatchSequences[0] == acknowledgment.Sequence)))
            {
                WriteDiagnostic("RendererSubmissionRejected", acknowledgment.Sequence, 0,
                    "Submission did not match the currently dispatched frame in this coordinator epoch.");
                return false;
            }
            entry.State = FrameState.RendererSubmitted;
            _dispatchSequence = null;
            _dispatchBatchSequences = null;
            _dispatchBatchSequenceSet = null;
            _dispatchReadySequences = null;
            WriteDiagnostic("RendererSubmitted", entry.Frame.Sequence, entry.Frame.Utf16CodeUnits,
                "Ordered renderer submission completed; renderer ACK may now release capacity.");
            return true;
        }
    }

    /// <summary>
    /// Marks one member of the active ReadBatchAsync dispatch ready after its ordered renderer
    /// post succeeds. Capacity remains reserved until the renderer ACK. The whole read group
    /// crosses the submission boundary atomically when every member has become ready.
    /// </summary>
    public bool MarkRendererFrameReady(OutputAcknowledgment acknowledgment, out bool dispatchSubmitted)
    {
        dispatchSubmitted = false;
        if (acknowledgment is null || acknowledgment.Sequence <= 0 || acknowledgment.GenerationId == Guid.Empty) return false;
        lock (_sync)
        {
            if (!_disposed && acknowledgment.GenerationId == _generationId && _dispatchSequence == acknowledgment.Sequence &&
                _outstanding.TryGetValue(acknowledgment.Sequence, out var singleEntry) && singleEntry.State == FrameState.Dequeued)
            {
                singleEntry.State = FrameState.RendererSubmitted;
                _dispatchSequence = null;
                WriteDiagnostic("RendererSubmitted", singleEntry.Frame.Sequence, singleEntry.Frame.Utf16CodeUnits,
                    "Ordered renderer submission completed; renderer ACK may now release capacity.");
                dispatchSubmitted = true;
                return true;
            }
            if (_disposed || acknowledgment.GenerationId != _generationId || _dispatchBatchSequences is null ||
                _dispatchBatchSequenceSet is null || !_dispatchBatchSequenceSet.Contains(acknowledgment.Sequence) ||
                !_outstanding.TryGetValue(acknowledgment.Sequence, out var entry) || entry.State != FrameState.Dequeued)
                return false;

            _dispatchReadySequences ??= new HashSet<long>();
            if (!_dispatchReadySequences.Add(acknowledgment.Sequence)) return false;
            if (_dispatchReadySequences.Count != _dispatchBatchSequences.Count) return true;

            foreach (var sequence in _dispatchBatchSequences)
            {
                var readyEntry = _outstanding[sequence];
                readyEntry.State = FrameState.RendererSubmitted;
                WriteDiagnostic("RendererSubmitted", readyEntry.Frame.Sequence, readyEntry.Frame.Utf16CodeUnits,
                    "Ordered renderer batch submission completed; renderer ACK may now release capacity.");
            }
            _dispatchBatchSequences = null;
            _dispatchBatchSequenceSet = null;
            _dispatchReadySequences = null;
            dispatchSubmitted = true;
            return true;
        }
    }

    /// <summary>Marks an exact batch-read group after its ordered renderer post succeeds.</summary>
    public bool MarkRendererBatchSubmitted(IReadOnlyList<OutputAcknowledgment> acknowledgments)
    {
        ArgumentNullException.ThrowIfNull(acknowledgments);
        lock (_sync)
        {
            if (_disposed || _dispatchBatchSequences is null || acknowledgments.Count != _dispatchBatchSequences.Count || acknowledgments.Count == 0)
            {
                WriteDiagnostic("RendererBatchSubmissionRejected", 0, 0, "Submission did not match the active batch dispatch.");
                return false;
            }
            for (var i = 0; i < acknowledgments.Count; i++)
            {
                var acknowledgment = acknowledgments[i];
                if (acknowledgment is null || acknowledgment.GenerationId != _generationId ||
                    acknowledgment.Sequence != _dispatchBatchSequences[i] ||
                    !_outstanding.TryGetValue(acknowledgment.Sequence, out var entry) || entry.State != FrameState.Dequeued)
                {
                    WriteDiagnostic("RendererBatchSubmissionRejected", acknowledgment?.Sequence ?? 0, 0,
                        "Submission identities did not match the active ordered batch dispatch.");
                    return false;
                }
            }
            foreach (var acknowledgment in acknowledgments)
            {
                var entry = _outstanding[acknowledgment.Sequence];
                entry.State = FrameState.RendererSubmitted;
                WriteDiagnostic("RendererSubmitted", entry.Frame.Sequence, entry.Frame.Utf16CodeUnits,
                    "Ordered renderer batch submission completed; renderer ACK may now release capacity.");
            }
            _dispatchBatchSequences = null;
            _dispatchBatchSequenceSet = null;
            _dispatchReadySequences = null;
            return true;
        }
    }

    public AcknowledgmentResult Acknowledge(OutputAcknowledgment acknowledgment)
    {
        if (acknowledgment is null || acknowledgment.Sequence <= 0 || acknowledgment.GenerationId == Guid.Empty)
        {
            lock (_sync) { _failureCount++; WriteDiagnostic("InvalidAcknowledgment", 0, 0, "Missing generation or non-positive sequence."); }
            return AcknowledgmentResult.Invalid;
        }

        List<Waiter> granted = [];
        AcknowledgmentResult result;
        lock (_sync)
        {
            if (_disposed) return AcknowledgmentResult.Disposed;
            if (acknowledgment.GenerationId != _generationId)
            {
                WriteDiagnostic("StaleAcknowledgment", acknowledgment.Sequence, 0, "Acknowledgment belongs to a retired generation.");
                return AcknowledgmentResult.StaleGeneration;
            }
            if (_outstanding.TryGetValue(acknowledgment.Sequence, out var entry))
            {
                if (entry.State != FrameState.RendererSubmitted)
                {
                    WriteDiagnostic("PrematureAcknowledgment", acknowledgment.Sequence, entry.Frame.Utf16CodeUnits,
                        "Frame is queued or dequeued and has not crossed the renderer-submission boundary.");
                    return AcknowledgmentResult.NotRendererSubmitted;
                }
                _outstanding.Remove(acknowledgment.Sequence);
                _reservedUtf16CodeUnits -= entry.Frame.Utf16CodeUnits;
                _acknowledgedFrames++;
                _acknowledgedUtf16CodeUnits += entry.Frame.Utf16CodeUnits;
                RememberAcknowledged(acknowledgment.Sequence);
                WriteDiagnostic("Acknowledged", entry.Frame.Sequence, entry.Frame.Utf16CodeUnits,
                    "Capacity returned by matching renderer acknowledgment.");
                GrantWaitersLocked(granted);
                result = AcknowledgmentResult.Acknowledged;
            }
            else if (_recentlyAcknowledged.Contains(acknowledgment.Sequence))
            {
                WriteDiagnostic("DuplicateAcknowledgment", acknowledgment.Sequence, 0, "Sequence was already acknowledged.");
                result = AcknowledgmentResult.Duplicate;
            }
            else
            {
                result = acknowledgment.Sequence <= _nextSequence ? AcknowledgmentResult.Late : AcknowledgmentResult.Unknown;
                WriteDiagnostic(result == AcknowledgmentResult.Late ? "LateAcknowledgment" : "UnknownAcknowledgment",
                    acknowledgment.Sequence, 0, "No eligible outstanding frame matches this acknowledgment.");
            }
        }
        CompleteGranted(granted);
        return result;
    }

    /// <summary>Retires only the supplied coordinator epoch and labels diagnostics with that epoch.</summary>
    public bool RetireGeneration(Guid generationId, string reason)
        => RetireGeneration(generationId, null, reason);

    /// <summary>Atomically retires only the epoch still owned by the expected PSES generation.</summary>
    public bool RetireGeneration(Guid generationId, int? expectedSessionGeneration, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        List<Waiter> canceled;
        TaskCompletionSource<bool> retiredPulse;
        lock (_sync)
        {
            if (_disposed || generationId == Guid.Empty || generationId != _generationId ||
                (expectedSessionGeneration.HasValue && expectedSessionGeneration != _sessionGeneration)) return false;
            var retired = _outstanding.Count;
            _retiredFrames += retired;
            _retiredUtf16CodeUnits += _reservedUtf16CodeUnits;
            _queue.Clear();
            _outstanding.Clear();
            _reservedUtf16CodeUnits = 0;
            _dispatchSequence = null;
            _dispatchBatchSequences = null;
            _dispatchBatchSequenceSet = null;
            _dispatchReadySequences = null;
            _readerActive = false;
            _readerGeneration = null;
            _recentlyAcknowledged.Clear();
            _recentlyAcknowledgedOrder.Clear();
            canceled = DetachWaitersLocked(WaiterState.Retired);
            retiredPulse = _readPulse;
            _readPulse = NewPulse();
            WriteDiagnostic("GenerationRetired", 0, 0, $"Retired {retired} outstanding frames; reason: {Bound(reason)}.", generationId);
            _generationId = Guid.NewGuid();
            _firstFrameSequence = checked(_nextSequence + 1);
            _sessionGeneration = null;
        }
        retiredPulse.TrySetResult(true);
        CompleteCanceled(canceled, new InvalidOperationException("The output generation was retired while admission was waiting."));
        return true;
    }

    public bool TryReadDiagnostic(out OutputCoordinatorDiagnostic? diagnostic) => _diagnostics.Reader.TryRead(out diagnostic);

    public ValueTask DisposeAsync()
    {
        List<Waiter> canceled;
        TaskCompletionSource<bool> pulse;
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            canceled = DetachWaitersLocked(WaiterState.Disposed);
            var retired = _outstanding.Count;
            _retiredFrames += retired;
            _retiredUtf16CodeUnits += _reservedUtf16CodeUnits;
            WriteDiagnostic("DisposedRetirement", 0, _reservedUtf16CodeUnits,
                $"Disposal retired {retired} outstanding frames and returned their capacity.");
            _queue.Clear();
            _outstanding.Clear();
            _reservedUtf16CodeUnits = 0;
            _dispatchSequence = null;
            _dispatchBatchSequences = null;
            _dispatchBatchSequenceSet = null;
            _dispatchReadySequences = null;
            _readerActive = false;
            _readerGeneration = null;
            pulse = _readPulse;
            _readPulse = NewPulse();
            _diagnostics.Writer.TryComplete();
        }
        pulse.TrySetResult(true);
        CompleteCanceled(canceled, new ObjectDisposedException(nameof(BoundedOutputCoordinator)));
        return ValueTask.CompletedTask;
    }

    private void CancelWaiter(Waiter waiter)
    {
        List<Waiter> canceled = [];
        List<Waiter> granted = [];
        lock (_sync)
        {
            // Lock acquisition decides the race: cancellation wins only while still Waiting;
            // once grant has transitioned the waiter, admission wins and cannot be withdrawn.
            if (waiter.State != WaiterState.Waiting || waiter.Node?.List != _waiters) return;
            RemoveWaiterLocked(waiter, WaiterState.Canceled);
            canceled.Add(waiter);
            GrantWaitersLocked(granted);
        }
        CompleteCanceled(canceled, null);
        CompleteGranted(granted);
    }

    private void GrantWaitersLocked(List<Waiter> granted)
    {
        while (_waiters.First is { } node && HasCapacity(node.Value.Text.Length))
        {
            var waiter = node.Value;
            RemoveWaiterLocked(waiter, WaiterState.Granted);
            if (waiter.ExpectedCoordinatorEpoch.HasValue &&
                (waiter.ExpectedCoordinatorEpoch.Value != _generationId || waiter.ExpectedSessionGeneration != _sessionGeneration))
            {
                // The waiter has already left the list and its pending budget was subtracted above.
                // Only change its terminal state here; removing it twice corrupts the budget.
                waiter.State = WaiterState.Retired;
                waiter.Frame = null;
                granted.Add(waiter);
                continue;
            }
            var frame = ReserveAndEnqueue(waiter.Source, waiter.Text, waiter.ExpectedSessionGeneration, waiter.RequestId, waiter.SourceSequence);
            waiter.Frame = frame;
            granted.Add(waiter);
        }
    }

    private void CompleteGranted(List<Waiter> granted)
    {
        foreach (var waiter in granted)
        {
            DetachRegistration(waiter);
            if (waiter.Frame is not null) waiter.Completion.TrySetResult(waiter.Frame);
            else waiter.Completion.TrySetException(new InvalidOperationException("The output producer identity became stale before capacity was reserved."));
        }
    }

    private void CompleteCanceled(List<Waiter> canceled, Exception? exception)
    {
        foreach (var waiter in canceled)
        {
            DetachRegistration(waiter);
            if (waiter.State == WaiterState.Canceled)
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            else if (exception is not null)
                waiter.Completion.TrySetException(exception);
        }
    }

    private List<Waiter> DetachWaitersLocked(WaiterState state)
    {
        var detached = _waiters.ToList();
        foreach (var waiter in detached) RemoveWaiterLocked(waiter, state);
        return detached;
    }

    private void RemoveWaiterLocked(Waiter waiter, WaiterState state)
    {
        if (waiter.Node?.List == _waiters) _waiters.Remove(waiter.Node);
        waiter.Node = null;
        waiter.State = state;
        _pendingUtf16CodeUnits -= waiter.Text.Length;
    }

    private static void DetachRegistration(Waiter waiter)
    {
        var registration = waiter.Registration;
        waiter.Registration = default;
        registration.Unregister();
    }

    private OutputFrame ReserveAndEnqueue(string source, string text, int? sessionGeneration = null, Guid? requestId = null, long sourceSequence = 0)
    {
        var frame = new OutputFrame(_generationId, checked(++_nextSequence), source, text)
        {
            SessionGeneration = sessionGeneration,
            RequestId = requestId,
            SourceSequence = sourceSequence
        };
        var entry = new FrameEntry(frame);
        _reservedUtf16CodeUnits += text.Length;
        _admittedUtf16CodeUnits += text.Length;
        _outstanding.Add(frame.Sequence, entry);
        _queue.Enqueue(entry);
        _admittedFrames++;
        _readPulse.TrySetResult(true);
        _readPulse = NewPulse();
        return frame;
    }

    private bool HasCapacity(int length) => _outstanding.Count < MaximumFrames &&
        _reservedUtf16CodeUnits + length <= MaximumUtf16CodeUnits;

    private void RememberAcknowledged(long sequence)
    {
        _recentlyAcknowledged.Add(sequence);
        _recentlyAcknowledgedOrder.Enqueue(sequence);
        if (_recentlyAcknowledgedOrder.Count > DuplicateHistoryCapacity)
            _recentlyAcknowledged.Remove(_recentlyAcknowledgedOrder.Dequeue());
    }

    private void WriteDiagnostic(string kind, long sequence, long length, string detail, Guid? generationId = null)
    {
        if (!_diagnostics.Writer.TryWrite(new(DateTimeOffset.UtcNow, kind, generationId ?? _generationId,
            sequence, length, Bound(detail))))
            _droppedDiagnosticRecords++;
    }

    private static string Bound(string value) => value.Length <= 256 ? value : value[..256];
    private static TaskCompletionSource<bool> NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(BoundedOutputCoordinator)); }

    private enum FrameState { Queued, Dequeued, RendererSubmitted }
    private enum WaiterState { Waiting, Granted, Canceled, Retired, Disposed }

    private sealed class FrameEntry(OutputFrame frame)
    {
        public OutputFrame Frame { get; } = frame;
        public FrameState State { get; set; } = FrameState.Queued;
    }

    private sealed class Waiter(string source, string text, CancellationToken cancellationToken,
        Guid? expectedCoordinatorEpoch, int? expectedSessionGeneration, Guid? requestId, long sourceSequence)
    {
        public string Source { get; } = source;
        public string Text { get; } = text;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Guid? ExpectedCoordinatorEpoch { get; } = expectedCoordinatorEpoch;
        public int? ExpectedSessionGeneration { get; } = expectedSessionGeneration;
        public Guid? RequestId { get; } = requestId;
        public long SourceSequence { get; } = sourceSequence;
        public TaskCompletionSource<OutputFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public WaiterState State { get; set; } = WaiterState.Waiting;
        public OutputFrame? Frame { get; set; }
    }
}
