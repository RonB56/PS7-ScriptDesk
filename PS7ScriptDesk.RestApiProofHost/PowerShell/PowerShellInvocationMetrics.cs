namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class PowerShellInvocationMetrics
{
    private int _activeInvocationCount;
    private int _queuedInvocationCount;
    private long _acceptedCount;
    private long _completedCount;
    private long _rejectedQueueFullCount;
    private long _queueTimeoutCount;
    private long _callerCanceledCount;
    private long _invocationTimeoutCount;
    private long _powerShellFailureCount;
    private long _internalFailureCount;
    private int _maxObservedActiveInvocationCount;

    public int ActiveInvocationCount => Volatile.Read(ref _activeInvocationCount);
    public int QueuedInvocationCount => Volatile.Read(ref _queuedInvocationCount);
    public long AcceptedCount => Volatile.Read(ref _acceptedCount);
    public long CompletedCount => Volatile.Read(ref _completedCount);
    public long RejectedQueueFullCount => Volatile.Read(ref _rejectedQueueFullCount);
    public long QueueTimeoutCount => Volatile.Read(ref _queueTimeoutCount);
    public long CallerCanceledCount => Volatile.Read(ref _callerCanceledCount);
    public long InvocationTimeoutCount => Volatile.Read(ref _invocationTimeoutCount);
    public long PowerShellFailureCount => Volatile.Read(ref _powerShellFailureCount);
    public long InternalFailureCount => Volatile.Read(ref _internalFailureCount);
    public int MaxObservedActiveInvocationCount => Volatile.Read(ref _maxObservedActiveInvocationCount);

    public void IncrementAccepted() => Interlocked.Increment(ref _acceptedCount);
    public void IncrementCompleted() => Interlocked.Increment(ref _completedCount);
    public void IncrementRejectedQueueFull() => Interlocked.Increment(ref _rejectedQueueFullCount);
    public void IncrementQueueTimeout() => Interlocked.Increment(ref _queueTimeoutCount);
    public void IncrementCallerCanceled() => Interlocked.Increment(ref _callerCanceledCount);
    public void IncrementInvocationTimeout() => Interlocked.Increment(ref _invocationTimeoutCount);
    public void IncrementPowerShellFailure() => Interlocked.Increment(ref _powerShellFailureCount);
    public void IncrementInternalFailure() => Interlocked.Increment(ref _internalFailureCount);
    public void IncrementQueued() => Interlocked.Increment(ref _queuedInvocationCount);
    public void DecrementQueued() => Interlocked.Decrement(ref _queuedInvocationCount);

    public void IncrementActive()
    {
        var active = Interlocked.Increment(ref _activeInvocationCount);
        while (true)
        {
            var observed = Volatile.Read(ref _maxObservedActiveInvocationCount);
            if (active <= observed ||
                Interlocked.CompareExchange(ref _maxObservedActiveInvocationCount, active, observed) == observed)
            {
                return;
            }
        }
    }

    public void DecrementActive() => Interlocked.Decrement(ref _activeInvocationCount);

    public PowerShellInvocationMetricsSnapshot CreateSnapshot(
        int maxConcurrency,
        int queueCapacity,
        int poolGeneration,
        int poolRebuildCount)
        => new(
            ActiveInvocationCount,
            QueuedInvocationCount,
            maxConcurrency,
            queueCapacity,
            poolGeneration,
            poolRebuildCount,
            AcceptedCount,
            CompletedCount,
            RejectedQueueFullCount,
            QueueTimeoutCount,
            CallerCanceledCount,
            InvocationTimeoutCount,
            PowerShellFailureCount,
            InternalFailureCount,
            MaxObservedActiveInvocationCount);
}

public sealed record PowerShellInvocationMetricsSnapshot(
    int ActiveInvocationCount,
    int QueuedInvocationCount,
    int MaxConcurrency,
    int QueueCapacity,
    int PoolGeneration,
    int PoolRebuildCount,
    long AcceptedCount,
    long CompletedCount,
    long RejectedQueueFullCount,
    long QueueTimeoutCount,
    long CallerCanceledCount,
    long InvocationTimeoutCount,
    long PowerShellFailureCount,
    long InternalFailureCount,
    int MaxObservedActiveInvocationCount);
