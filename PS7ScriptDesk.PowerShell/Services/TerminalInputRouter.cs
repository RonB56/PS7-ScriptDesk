using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PS7ScriptDesk.PowerShell.Services
{
    /// <summary>
    /// Owns the ordered write queue for one terminal session generation.
    /// The writer itself remains owned by <see cref="LiveConsoleService"/> so
    /// process and native-handle teardown stay in one place.
    /// </summary>
    internal sealed class TerminalInputRouter
    {
        private readonly object _syncRoot = new();
        private Session? _session;
        private long _nextSequence;

        public void Activate(int generation, TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            lock (_syncRoot)
            {
                if (_session is not null)
                {
                    throw new InvalidOperationException("A terminal input generation is already active.");
                }

                _session = new Session(generation, writer);
            }
        }

        public Task WriteAsync(
            int generation,
            string payload,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return Task.CompletedTask;
            }

            Session session;
            Task predecessor;
            long sequence;
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_syncRoot)
            {
                session = _session ??
                    throw new InvalidOperationException("The terminal input writer is not available.");

                if (!session.AcceptingWrites || session.Generation != generation)
                {
                    throw new InvalidOperationException(
                        "The terminal input request targets a stale or stopping session.");
                }

                predecessor = session.Tail;
                sequence = ++_nextSequence;
                session.Tail = completion.Task;
            }

            _ = ProcessWriteAsync(
                predecessor,
                session,
                payload,
                sequence,
                cancellationToken,
                completion);

            return completion.Task;
        }

        public async Task<bool> DeactivateAsync(int generation, TimeSpan timeout)
        {
            Session? session;
            Task tail;

            lock (_syncRoot)
            {
                session = _session;
                if (session is null || session.Generation != generation)
                {
                    return true;
                }

                session.AcceptingWrites = false;
                session.LifetimeCancellation.Cancel();
                tail = session.Tail;
                _session = null;
            }

            var drained = true;
            try
            {
                await tail.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                drained = false;
            }
            catch (OperationCanceledException)
            {
                // A canceled terminal-generation write is fully drained.
            }
            catch
            {
                // A failed write is also complete. Its caller observes the original failure.
            }

            if (tail.IsCompleted)
            {
                session.LifetimeCancellation.Dispose();
            }
            else
            {
                _ = tail.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    session.LifetimeCancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return drained;
        }

        private async Task ProcessWriteAsync(
            Task predecessor,
            Session session,
            string payload,
            long sequence,
            CancellationToken callerCancellationToken,
            TaskCompletionSource completion)
        {
            try
            {
                try
                {
                    await predecessor.ConfigureAwait(false);
                }
                catch
                {
                    // One failed payload must not prevent later queued payloads from running.
                }

                lock (_syncRoot)
                {
                    if (!ReferenceEquals(_session, session) ||
                        !session.AcceptingWrites ||
                        session.LifetimeCancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(
                            "The terminal session stopped before the queued input could be written.",
                            session.LifetimeCancellation.Token);
                    }
                }

                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellationToken,
                    session.LifetimeCancellation.Token);

                linkedCancellation.Token.ThrowIfCancellationRequested();
                await session.Writer.WriteAsync(payload.AsMemory(), linkedCancellation.Token).ConfigureAwait(false);
                await session.Writer.FlushAsync(linkedCancellation.Token).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(new IOException(
                    $"Terminal input write {sequence} failed.",
                    ex));
            }
        }

        private sealed class Session
        {
            public Session(int generation, TextWriter writer)
            {
                Generation = generation;
                Writer = writer;
            }

            public int Generation { get; }

            public TextWriter Writer { get; }

            public CancellationTokenSource LifetimeCancellation { get; } = new();

            public bool AcceptingWrites { get; set; } = true;

            public Task Tail { get; set; } = Task.CompletedTask;
        }
    }
}
