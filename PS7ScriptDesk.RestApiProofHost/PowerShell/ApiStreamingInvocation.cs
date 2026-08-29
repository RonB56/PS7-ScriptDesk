using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class ApiStreamingInvocationSession : IAsyncDisposable
{
    private readonly ApiStreamingInvocationEventChannel _events;
    private readonly CancellationTokenSource _cancellation;
    private Task _completion = Task.CompletedTask;
    private int _disposed;

    internal ApiStreamingInvocationSession(
        ApiStreamingInvocationRequest request,
        ApiStreamingInvocationEventChannel events,
        CancellationTokenSource cancellation)
    {
        Request = request;
        _events = events;
        _cancellation = cancellation;
    }

    public ApiStreamingInvocationRequest Request { get; }
    public int EventCapacity => _events.Capacity;
    public Task Completion => _completion;
    internal CancellationToken CancellationToken => _cancellation.Token;

    internal void AttachCompletion(Task completion)
        => _completion = completion;

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _cancellation.Cancel();
        }
    }

    public async IAsyncEnumerable<ApiStreamingInvocationEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static state => ((ApiStreamingInvocationSession)state!).Cancel(), this)
            : default;

        await foreach (var item in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal ValueTask<bool> WriteDataAsync(ApiStreamingInvocationEvent item, CancellationToken cancellationToken)
        => _events.WriteDataAsync(item, cancellationToken);

    internal ValueTask<bool> WriteTerminalAsync(ApiStreamingInvocationEvent item)
        => _events.WriteTerminalAsync(item);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await _completion.ConfigureAwait(false);
        }
        finally
        {
            _events.Dispose();
            _cancellation.Dispose();
        }
    }
}

internal sealed class ApiStreamingInvocationEventChannel : IDisposable
{
    private readonly Channel<ApiStreamingInvocationEvent> _channel;
    private readonly SemaphoreSlim _regularSlots;
    private int _terminalWritten;
    private int _disposed;

    public ApiStreamingInvocationEventChannel(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Event capacity must be positive.");
        }

        Capacity = capacity;
        _channel = Channel.CreateBounded<ApiStreamingInvocationEvent>(new BoundedChannelOptions(capacity + 1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _regularSlots = new SemaphoreSlim(capacity, capacity);
    }

    public int Capacity { get; }

    public async ValueTask<bool> WriteDataAsync(ApiStreamingInvocationEvent item, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (item.IsTerminal)
        {
            throw new ArgumentException("Data events cannot be terminal events.", nameof(item));
        }

        var slotHeld = false;
        try
        {
            await _regularSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            slotHeld = true;
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        finally
        {
            if (slotHeld && Volatile.Read(ref _disposed) != 0)
            {
                _regularSlots.Release();
            }
        }
    }

    public async ValueTask<bool> WriteTerminalAsync(ApiStreamingInvocationEvent item)
    {
        if (!item.IsTerminal || Interlocked.Exchange(ref _terminalWritten, 1) != 0)
        {
            return false;
        }

        try
        {
            await _channel.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
            _channel.Writer.TryComplete();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<ApiStreamingInvocationEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!item.IsTerminal)
            {
                _regularSlots.Release();
            }

            yield return item;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        _regularSlots.Dispose();
    }
}
