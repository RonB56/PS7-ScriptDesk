using System.Collections.Concurrent;
using System.Threading.Channels;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Bounded, asynchronous observation side-channel. A full observation queue can lose
/// diagnostics, but can never delay or reject the authoritative terminal delivery path.
/// </summary>
public sealed class TerminalOutputObservationHub : ITerminalDataPlane, IDisposable
{
    private const int Capacity = 2048;
    private readonly Channel<TerminalOutputObservation> _observations = Channel.CreateBounded<TerminalOutputObservation>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<long, ITerminalObserver> _observers = new();
    private long _nextSubscriptionId;
    private readonly Task _worker;
    private int _disposed;

    public TerminalOutputObservationHub()
    {
        _worker = Task.Run(DispatchAsync);
    }

    public bool Publish(TerminalOutputObservation observation) =>
        Volatile.Read(ref _disposed) == 0 && !string.IsNullOrEmpty(observation.Data) && _observations.Writer.TryWrite(observation);

    public IDisposable Subscribe(ITerminalObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var id = Interlocked.Increment(ref _nextSubscriptionId);
        _observers[id] = observer;
        return new Subscription(() => _observers.TryRemove(id, out _));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _observations.Writer.TryComplete();
        _observers.Clear();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }

    private async Task DispatchAsync()
    {
        await foreach (var observation in _observations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            foreach (var observer in _observers.Values)
            {
                try { observer.Observe(observation); }
                catch { /* observation must never affect terminal delivery */ }
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}
