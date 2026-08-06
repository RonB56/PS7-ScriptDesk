using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

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
        private bool _flushScheduled;
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

        public TerminalOutputEnqueueResult Enqueue(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return default;
            }

            lock (_syncRoot)
            {
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
                _rendererReady = true;
                return TryScheduleFlush();
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

                var outputBatch = new TerminalOutputBatch(++_nextSequence, batch.ToString());
                _inFlightBatch = outputBatch;
                return outputBatch;
            }
        }

        public bool Acknowledge(long sequence)
        {
            lock (_syncRoot)
            {
                if (_inFlightBatch is not { } batch || batch.Sequence != sequence)
                {
                    return false;
                }

                _pendingCharacters -= batch.Data.Length;
                _inFlightBatch = null;
                return TryScheduleFlush();
            }
        }

        public int DiscardInFlight(long sequence)
        {
            lock (_syncRoot)
            {
                if (_inFlightBatch is not { } batch || batch.Sequence != sequence)
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
    }

    internal readonly record struct TerminalOutputEnqueueResult(
        bool ScheduleFlush,
        int AcceptedCharacters,
        int DroppedCharacters,
        int PendingCharacters);

    internal readonly record struct TerminalOutputBatch(long Sequence, string Data);

    internal static class TerminalWebMessageSerializer
    {
        public static string SerializeOutput(long sequence, string data)
        {
            return JsonSerializer.Serialize(new
            {
                type = "output_b64",
                sequence,
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes(data ?? string.Empty))
            });
        }

        public static string Serialize(string type, string data)
        {
            return type switch
            {
                "output" => SerializeOutput(0, data),
                "clear" or "focus" => JsonSerializer.Serialize(new { type }),
                _ => JsonSerializer.Serialize(new { type, data })
            };
        }
    }
}
