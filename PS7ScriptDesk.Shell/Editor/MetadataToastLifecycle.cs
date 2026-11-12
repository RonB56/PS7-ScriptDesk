using System;

namespace PS7ScriptDesk.Shell.Editor;

internal sealed class MetadataToastLifecycleState
{
    private bool _initializationActive;
    private bool _terminalStateObserved;

    public bool IsInitializationActive => _initializationActive;

    public bool ObserveProgress(bool isExplicitStart)
    {
        if (_initializationActive)
        {
            return true;
        }

        if (_terminalStateObserved && !isExplicitStart)
        {
            return false;
        }

        _initializationActive = true;
        _terminalStateObserved = false;
        return true;
    }

    public bool TryCompleteInitialization()
    {
        if (!_initializationActive)
        {
            return false;
        }

        _initializationActive = false;
        _terminalStateObserved = true;
        return true;
    }

    public void EndInitialization()
    {
        _initializationActive = false;
        _terminalStateObserved = true;
    }
}

internal sealed class MetadataToastDismissal : IDisposable
{
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Action _cancel;
    private long _generation;
    private bool _disposed;

    public MetadataToastDismissal(Action<TimeSpan, Action> schedule, Action cancel)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        Cancel();
        long generation = ++_generation;
        _schedule(delay, () =>
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            Cancel();
            callback();
        });
    }

    public void Cancel()
    {
        _generation++;
        _cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        _disposed = true;
    }
}
