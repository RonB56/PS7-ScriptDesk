using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Controls
{
    /// <summary>
    /// Provides bounded, ordered output flow control between the terminal transport and xterm.js.
    /// A batch remains counted against the limit until xterm.js acknowledges that it has rendered it.
    /// </summary>
    internal sealed class TerminalOutputFlowController
    {
        public const int DefaultMaximumPendingCharacters = 512 * 1024;
        public const int DefaultMaximumBatchCharacters = 32 * 1024;

        private readonly object _syncRoot = new();
        private readonly LinkedList<string> _pendingChunks = new();
        private readonly int _maximumPendingCharacters;
        private readonly int _maximumBatchCharacters;
        private bool _rendererReady;
        private bool _rendererUnavailable;
        private bool _flushScheduled;
        private int? _activeGeneration;
        private long _nextSequence;
        private TerminalOutputBatch? _inFlightBatch;
        private int _pendingCharacters;

        public TerminalOutputFlowController(
            int maximumPendingCharacters = DefaultMaximumPendingCharacters,
            int maximumBatchCharacters = DefaultMaximumBatchCharacters)
        {
            if (maximumPendingCharacters <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPendingCharacters));
            }

            if (maximumBatchCharacters <= 0 || maximumBatchCharacters > maximumPendingCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBatchCharacters));
            }

            _maximumPendingCharacters = maximumPendingCharacters;
            _maximumBatchCharacters = maximumBatchCharacters;
        }

        public int? ActiveGeneration
        {
            get
            {
                lock (_syncRoot)
                {
                    return _activeGeneration;
                }
            }
        }

        public bool HasOutstandingOutput
        {
            get
            {
                lock (_syncRoot)
                {
                    return _pendingCharacters > 0 || _inFlightBatch is not null;
                }
            }
        }

        public TerminalOutputGenerationInvalidationResult ActivateGeneration(int generation)
        {
            lock (_syncRoot)
            {
                var discardedCharacters = DiscardAllOutput();
                _activeGeneration = generation;
                return new TerminalOutputGenerationInvalidationResult(generation, discardedCharacters);
            }
        }

        public TerminalOutputGenerationInvalidationResult InvalidateGeneration(int generation)
        {
            lock (_syncRoot)
            {
                if (_activeGeneration != generation)
                {
                    return default;
                }

                var discardedCharacters = DiscardAllOutput();
                _activeGeneration = null;
                return new TerminalOutputGenerationInvalidationResult(generation, discardedCharacters);
            }
        }

        public TerminalOutputEnqueueResult Enqueue(int generation, string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return default;
            }

            lock (_syncRoot)
            {
                if (_rendererUnavailable)
                {
                    return new TerminalOutputEnqueueResult(
                        ScheduleFlush: false,
                        AcceptedCharacters: 0,
                        DroppedCharacters: data.Length,
                        PendingCharacters: _pendingCharacters);
                }

                if (_activeGeneration != generation)
                {
                    return new TerminalOutputEnqueueResult(
                        ScheduleFlush: false,
                        AcceptedCharacters: 0,
                        DroppedCharacters: 0,
                        PendingCharacters: _pendingCharacters,
                        RejectedStaleCharacters: data.Length);
                }

                if (data.Length > _maximumPendingCharacters - _pendingCharacters)
                {
                    return new TerminalOutputEnqueueResult(
                        ScheduleFlush: false,
                        AcceptedCharacters: 0,
                        DroppedCharacters: data.Length,
                        PendingCharacters: _pendingCharacters);
                }

                _pendingChunks.AddLast(data);
                _pendingCharacters += data.Length;
                return new TerminalOutputEnqueueResult(
                    ScheduleFlush: TryScheduleFlush(),
                    AcceptedCharacters: data.Length,
                    DroppedCharacters: 0,
                    PendingCharacters: _pendingCharacters);
            }
        }

        public bool SetRendererReady()
        {
            lock (_syncRoot)
            {
                if (_rendererUnavailable)
                {
                    return false;
                }

                _rendererReady = true;
                return TryScheduleFlush();
            }
        }

        /// <summary>
        /// Permanently disables renderer delivery for this controller lifetime and discards
        /// any output that can no longer be rendered after terminal bootstrap failure.
        /// </summary>
        public TerminalOutputRendererUnavailableResult MarkRendererUnavailable()
        {
            lock (_syncRoot)
            {
                _rendererUnavailable = true;
                _rendererReady = false;
                return new TerminalOutputRendererUnavailableResult(DiscardAllOutput());
            }
        }

        public TerminalOutputBatch? TryBeginDelivery()
        {
            lock (_syncRoot)
            {
                _flushScheduled = false;
                if (!_rendererReady || _inFlightBatch is not null || _pendingChunks.Count == 0)
                {
                    return null;
                }

                var batch = new StringBuilder(Math.Min(_pendingCharacters, _maximumBatchCharacters));
                while (_pendingChunks.Count > 0 && batch.Length < _maximumBatchCharacters)
                {
                    var chunk = _pendingChunks.First!.Value;
                    var available = _maximumBatchCharacters - batch.Length;
                    if (chunk.Length <= available)
                    {
                        batch.Append(chunk);
                        _pendingChunks.RemoveFirst();
                    }
                    else
                    {
                        batch.Append(chunk, 0, available);
                        _pendingChunks.First!.Value = chunk[available..];
                    }
                }

                if (_activeGeneration is not { } generation)
                {
                    return null;
                }

                var outputBatch = new TerminalOutputBatch(generation, ++_nextSequence, batch.ToString());
                _inFlightBatch = outputBatch;
                return outputBatch;
            }
        }

        public bool Acknowledge(int generation, long sequence)
        {
            lock (_syncRoot)
            {
                if (_activeGeneration != generation || _inFlightBatch is not { } batch ||
                    batch.Generation != generation || batch.Sequence != sequence)
                {
                    return false;
                }

                _pendingCharacters -= batch.Data.Length;
                _inFlightBatch = null;
                return TryScheduleFlush();
            }
        }

        public int DiscardInFlight(int generation, long sequence)
        {
            lock (_syncRoot)
            {
                if (_activeGeneration != generation || _inFlightBatch is not { } batch ||
                    batch.Generation != generation || batch.Sequence != sequence)
                {
                    return 0;
                }

                _pendingCharacters -= batch.Data.Length;
                _inFlightBatch = null;
                return batch.Data.Length;
            }
        }

        private bool TryScheduleFlush()
        {
            if (!_rendererReady || _inFlightBatch is not null || _pendingChunks.Count == 0 || _flushScheduled)
            {
                return false;
            }

            _flushScheduled = true;
            return true;
        }

        private int DiscardAllOutput()
        {
            var discardedCharacters = _pendingCharacters;
            _pendingChunks.Clear();
            _pendingCharacters = 0;
            _inFlightBatch = null;
            _flushScheduled = false;
            return discardedCharacters;
        }
    }

    internal readonly record struct TerminalOutputEnqueueResult(
        bool ScheduleFlush,
        int AcceptedCharacters,
        int DroppedCharacters,
        int PendingCharacters,
        int RejectedStaleCharacters = 0);

    internal readonly record struct TerminalOutputGenerationInvalidationResult(int Generation, int DiscardedCharacters);

    internal readonly record struct TerminalOutputRendererUnavailableResult(int DiscardedCharacters);

    internal readonly record struct TerminalOutputBatch(int Generation, long Sequence, string Data);

    /// <summary>
    /// Holds renderer-visible terminal bytes while ConPTY has been resized and xterm.js
    /// has not yet acknowledged the matching grid commit. The coordinator never waits on
    /// WebView2 and releases accepted chunks in their original order exactly once.
    /// </summary>
    internal sealed class TerminalResizeOutputBarrier
    {
        public const int DefaultMaximumBufferedCharacters = 256 * 1024;
        public const int DefaultMaximumBufferedChunks = 256;
        public static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(2);

        private readonly object _syncRoot = new();
        private readonly int _maximumBufferedCharacters;
        private readonly int _maximumBufferedChunks;
        private readonly TimeSpan _maximumDuration;
        private ResizeTransaction? _active;

        public TerminalResizeOutputBarrier(
            int maximumBufferedCharacters = DefaultMaximumBufferedCharacters,
            int maximumBufferedChunks = DefaultMaximumBufferedChunks,
            TimeSpan? maximumDuration = null)
        {
            if (maximumBufferedCharacters <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBufferedCharacters));
            }

            if (maximumBufferedChunks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBufferedChunks));
            }

            var duration = maximumDuration ?? DefaultMaximumDuration;
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumDuration));
            }

            _maximumBufferedCharacters = maximumBufferedCharacters;
            _maximumBufferedChunks = maximumBufferedChunks;
            _maximumDuration = duration;
        }

        public bool IsActive
        {
            get
            {
                lock (_syncRoot)
                {
                    return _active is not null;
                }
            }
        }

        public TerminalResizeBarrierBeginResult Begin(
            int rendererGeneration,
            int terminalSessionGeneration,
            long resizeGeneration,
            int columns,
            int rows,
            DateTimeOffset? startedAt = null)
        {
            lock (_syncRoot)
            {
                if (_active is not null)
                {
                    return new TerminalResizeBarrierBeginResult(false, "resize-active");
                }

                _active = new ResizeTransaction(
                    rendererGeneration,
                    terminalSessionGeneration,
                    resizeGeneration,
                    columns,
                    rows,
                    startedAt ?? DateTimeOffset.UtcNow);
                return new TerminalResizeBarrierBeginResult(true, "started");
            }
        }

        public TerminalResizeBarrierCaptureResult Capture(
            int rendererGeneration,
            int terminalSessionGeneration,
            string source,
            string data,
            DateTimeOffset? observedAt = null)
        {
            if (string.IsNullOrEmpty(data))
            {
                return new TerminalResizeBarrierCaptureResult(TerminalResizeBarrierCaptureStatus.Empty, 0, 0);
            }

            lock (_syncRoot)
            {
                if (_active is null)
                {
                    return new TerminalResizeBarrierCaptureResult(TerminalResizeBarrierCaptureStatus.NotActive, 0, 0);
                }

                if (_active.RendererGeneration != rendererGeneration ||
                    _active.TerminalSessionGeneration != terminalSessionGeneration)
                {
                    return new TerminalResizeBarrierCaptureResult(TerminalResizeBarrierCaptureStatus.Stale, 0, _active.BufferedCharacters);
                }

                if ((observedAt ?? DateTimeOffset.UtcNow) - _active.StartedAt > _maximumDuration ||
                    _active.BufferedChunks >= _maximumBufferedChunks ||
                    data.Length > _maximumBufferedCharacters - _active.BufferedCharacters)
                {
                    return new TerminalResizeBarrierCaptureResult(
                        TerminalResizeBarrierCaptureStatus.BoundedLimitExceeded,
                        0,
                        _active.BufferedCharacters);
                }

                _active.Chunks.Add(new TerminalResizeBufferedOutput(terminalSessionGeneration, source, data));
                _active.BufferedCharacters += data.Length;
                return new TerminalResizeBarrierCaptureResult(
                    TerminalResizeBarrierCaptureStatus.Buffered,
                    data.Length,
                    _active.BufferedCharacters);
            }
        }

        public TerminalResizeBarrierAcknowledgementResult Acknowledge(
            int rendererGeneration,
            int terminalSessionGeneration,
            long resizeGeneration,
            int actualColumns,
            int actualRows)
        {
            lock (_syncRoot)
            {
                if (_active is null)
                {
                    return TerminalResizeBarrierAcknowledgementResult.Rejected("no-active-resize");
                }

                if (_active.RendererGeneration != rendererGeneration ||
                    _active.TerminalSessionGeneration != terminalSessionGeneration ||
                    _active.ResizeGeneration != resizeGeneration ||
                    _active.Columns != actualColumns ||
                    _active.Rows != actualRows)
                {
                    return TerminalResizeBarrierAcknowledgementResult.Rejected("stale-or-mismatched-ack");
                }

                var released = _active.Chunks.ToArray();
                var result = new TerminalResizeBarrierAcknowledgementResult(
                    Accepted: true,
                    Reason: "acknowledged",
                    ReleasedOutput: released,
                    BufferedCharacters: _active.BufferedCharacters);
                _active = null;
                return result;
            }
        }

        public TerminalResizeBarrierExpirationResult Expire(DateTimeOffset? now = null)
        {
            lock (_syncRoot)
            {
                if (_active is null || (now ?? DateTimeOffset.UtcNow) - _active.StartedAt <= _maximumDuration)
                {
                    return new TerminalResizeBarrierExpirationResult(false, 0, 0);
                }

                return new TerminalResizeBarrierExpirationResult(
                    true,
                    _active.BufferedCharacters,
                    _active.BufferedChunks);
            }
        }

        public TerminalResizeBarrierCancellationResult Cancel()
        {
            lock (_syncRoot)
            {
                if (_active is null)
                {
                    return new TerminalResizeBarrierCancellationResult(0, 0);
                }

                var result = new TerminalResizeBarrierCancellationResult(
                    _active.BufferedCharacters,
                    _active.BufferedChunks);
                _active = null;
                return result;
            }
        }

        private sealed class ResizeTransaction
        {
            public ResizeTransaction(
                int rendererGeneration,
                int terminalSessionGeneration,
                long resizeGeneration,
                int columns,
                int rows,
                DateTimeOffset startedAt)
            {
                RendererGeneration = rendererGeneration;
                TerminalSessionGeneration = terminalSessionGeneration;
                ResizeGeneration = resizeGeneration;
                Columns = columns;
                Rows = rows;
                StartedAt = startedAt;
            }

            public int RendererGeneration { get; }
            public int TerminalSessionGeneration { get; }
            public long ResizeGeneration { get; }
            public int Columns { get; }
            public int Rows { get; }
            public DateTimeOffset StartedAt { get; }
            public List<TerminalResizeBufferedOutput> Chunks { get; } = new();
            public int BufferedCharacters { get; set; }
            public int BufferedChunks => Chunks.Count;
        }
    }

    internal readonly record struct TerminalResizeBarrierBeginResult(bool Accepted, string Reason);

    internal enum TerminalResizeBarrierCaptureStatus
    {
        Empty,
        NotActive,
        Stale,
        Buffered,
        BoundedLimitExceeded
    }

    internal readonly record struct TerminalResizeBarrierCaptureResult(
        TerminalResizeBarrierCaptureStatus Status,
        int BufferedCharacters,
        int TotalBufferedCharacters);

    internal readonly record struct TerminalResizeBufferedOutput(
        int TerminalSessionGeneration,
        string Source,
        string Data);

    internal readonly record struct TerminalResizeBarrierAcknowledgementResult(
        bool Accepted,
        string Reason,
        IReadOnlyList<TerminalResizeBufferedOutput> ReleasedOutput,
        int BufferedCharacters)
    {
        public static TerminalResizeBarrierAcknowledgementResult Rejected(string reason) =>
            new(false, reason, Array.Empty<TerminalResizeBufferedOutput>(), 0);
    }

    internal readonly record struct TerminalResizeBarrierExpirationResult(
        bool Expired,
        int BufferedCharacters,
        int BufferedChunks);

    internal readonly record struct TerminalResizeBarrierCancellationResult(
        int BufferedCharacters,
        int BufferedChunks);

    internal sealed class TerminalRendererBridge
    {
        private readonly TerminalOutputFlowController _flowController;
        private readonly object _syncRoot = new();
        private int _rendererGeneration;
        private TerminalRendererLifecycle _lifecycle = TerminalRendererLifecycle.Unavailable;

        public TerminalRendererBridge(TerminalOutputFlowController? flowController = null)
        {
            _flowController = flowController ?? new TerminalOutputFlowController();
        }

        public int RendererGeneration
        {
            get
            {
                lock (_syncRoot)
                {
                    return _rendererGeneration;
                }
            }
        }

        public TerminalRendererLifecycle Lifecycle
        {
            get
            {
                lock (_syncRoot)
                {
                    return _lifecycle;
                }
            }
        }

        public TerminalOutputGenerationInvalidationResult StartRenderer(int rendererGeneration)
        {
            lock (_syncRoot)
            {
                if (rendererGeneration < _rendererGeneration)
                {
                    return default;
                }

                _rendererGeneration = rendererGeneration;
                _lifecycle = TerminalRendererLifecycle.Starting;
                return _flowController.ActivateGeneration(rendererGeneration);
            }
        }

        public bool MarkRendererReady(int rendererGeneration)
        {
            lock (_syncRoot)
            {
                if (rendererGeneration != _rendererGeneration ||
                    _lifecycle is TerminalRendererLifecycle.Failed or TerminalRendererLifecycle.Retired)
                {
                    return false;
                }

                _lifecycle = TerminalRendererLifecycle.Ready;
                return _flowController.SetRendererReady();
            }
        }

        public TerminalOutputRendererUnavailableResult MarkRendererUnavailable(int rendererGeneration)
        {
            lock (_syncRoot)
            {
                if (rendererGeneration != _rendererGeneration)
                {
                    return default;
                }

                _lifecycle = TerminalRendererLifecycle.Failed;
                return _flowController.MarkRendererUnavailable();
            }
        }

        public TerminalOutputEnqueueResult Submit(TerminalOutputEnvelope envelope)
        {
            if (envelope.Source == TerminalOutputSource.StructuredEditor &&
                TerminalOutputMultiplexer.ContainsPrivateProtocol(envelope.Payload))
            {
                throw new InvalidOperationException("Structured editor output contains private ScriptDesk terminal protocol.");
            }

            lock (_syncRoot)
            {
                if (envelope.RendererGeneration != _rendererGeneration)
                {
                    return new TerminalOutputEnqueueResult(
                        ScheduleFlush: false,
                        AcceptedCharacters: 0,
                        DroppedCharacters: 0,
                        PendingCharacters: 0,
                        RejectedStaleCharacters: envelope.Payload?.Length ?? 0);
                }

                return _flowController.Enqueue(envelope.RendererGeneration, envelope.Payload);
            }
        }

        public TerminalOutputBatch? TryBeginDelivery() => _flowController.TryBeginDelivery();

        public bool Acknowledge(int rendererGeneration, long sequence)
        {
            lock (_syncRoot)
            {
                if (rendererGeneration != _rendererGeneration)
                {
                    return false;
                }

                return _flowController.Acknowledge(rendererGeneration, sequence);
            }
        }
    }

    internal static class TerminalWebMessageSerializer
    {
        public static string SerializeOutput(
            int generation,
            long sequence,
            string data,
            int rendererGeneration = 0,
            long submissionId = 0,
            bool resizeAdjacent = false,
            long resizeGeneration = 0,
            double resizeElapsedMilliseconds = 0,
            string? hostControlSummary = null)
        {
            var safeData = data ?? string.Empty;
            var controlSummary = hostControlSummary ??
                TerminalOutputControlClassifier.Summarize(safeData).ToDiagnosticString();
            return JsonSerializer.Serialize(new
            {
                type = "output_b64",
                generation,
                sequence,
                rendererGeneration,
                submissionId,
                resizeAdjacent,
                resizeGeneration,
                resizeElapsedMilliseconds,
                outputCharacterLength = safeData.Length,
                hostControlSummary = controlSummary,
                contentOmitted = true,
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes(safeData))
            });
        }

        public static string SerializeResizeCommit(
            int rendererGeneration,
            int terminalSessionGeneration,
            long resizeGeneration,
            int columns,
            int rows)
        {
            return JsonSerializer.Serialize(new
            {
                type = "resize_commit",
                rendererGeneration,
                terminalSessionGeneration,
                resizeGeneration,
                cols = columns,
                rows
            });
        }

        public static string Serialize(string type, string data)
        {
            return type switch
            {
                "output" => SerializeOutput(0, 0, data),
                "clear" or "focus" => JsonSerializer.Serialize(new { type }),
                _ => JsonSerializer.Serialize(new { type, data })
            };
        }
    }
}
