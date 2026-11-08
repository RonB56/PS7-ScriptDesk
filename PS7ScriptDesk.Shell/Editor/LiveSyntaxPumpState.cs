using System.Threading;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Editor;

internal sealed class LiveSyntaxWorkItem
{
    public LiveSyntaxWorkItem(
        EditorTextSnapshotMaterialization textMaterialization,
        string pwshPath,
        int registrationVersion,
        int requestVersion,
        int lineCount,
        string documentTitle,
        string? filePath)
    {
        TextMaterialization = textMaterialization;
        PwshPath = pwshPath;
        RegistrationVersion = registrationVersion;
        RequestVersion = requestVersion;
        LineCount = Math.Max(1, lineCount);
        DocumentTitle = documentTitle;
        FilePath = filePath;
    }

    public EditorTextSnapshotMaterialization TextMaterialization { get; }
    public EditorDocumentSnapshot DocumentSnapshot => TextMaterialization.DocumentSnapshot;
    public string ScriptSnapshot => TextMaterialization.Text;
    public int TextLength => TextMaterialization.TextLength;
    public string PwshPath { get; }
    public int RegistrationVersion { get; }
    public int RequestVersion { get; }
    public int LineCount { get; }
    public string DocumentTitle { get; }
    public string? FilePath { get; }
}

internal sealed class LiveSyntaxPumpState : IDisposable
{
    public const int MaximumRecoveryAttempts = 1;

    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly CancellationToken _cancellationToken;
    private int _signalPending;
    private LiveSyntaxWorkItem? _latestWorkItem;
    private Task? _workerTask;
    private int _recoveryAttempts;
    private bool _recoveryLimitLogged;
    private bool _isDisposed;

    public LiveSyntaxPumpState()
    {
        _cancellationToken = _cancellationTokenSource.Token;
        Signal = new SemaphoreSlim(0, 1);
    }

    public CancellationToken CancellationToken => _cancellationToken;
    public SemaphoreSlim Signal { get; }
    public DateTimeOffset LastParseStartedUtc { get; set; } = DateTimeOffset.MinValue;

    public Task? WorkerTask
    {
        get { lock (_syncRoot) return _workerTask; }
    }

    public int RecoveryAttempts
    {
        get { lock (_syncRoot) return _recoveryAttempts; }
    }

    public bool TryMarkRecoveryLimitLogged()
    {
        lock (_syncRoot)
        {
            if (_isDisposed || _recoveryLimitLogged) return false;
            _recoveryLimitLogged = true;
            return true;
        }
    }

    public bool IsDisposed
    {
        get { lock (_syncRoot) return _isDisposed; }
    }

    public LiveSyntaxWorkItem? LatestWorkItem
    {
        get { lock (_syncRoot) return _latestWorkItem; }
    }

    public bool SetWorkerTask(Task workerTask)
    {
        ArgumentNullException.ThrowIfNull(workerTask);
        lock (_syncRoot)
        {
            if (_isDisposed || _cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            _workerTask = workerTask;
            return true;
        }
    }

    public bool TryClaimRecovery(Task completedWorker)
    {
        ArgumentNullException.ThrowIfNull(completedWorker);
        lock (_syncRoot)
        {
            if (_isDisposed || _cancellationToken.IsCancellationRequested ||
                !completedWorker.IsCompleted || !ReferenceEquals(_workerTask, completedWorker) ||
                _recoveryAttempts >= MaximumRecoveryAttempts)
            {
                return false;
            }

            _recoveryAttempts++;
            _workerTask = null;
            return true;
        }
    }

    public void Publish(LiveSyntaxWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        lock (_syncRoot)
        {
            if (_isDisposed) return;
            _latestWorkItem = workItem;
        }

        SignalWorker();
    }

    public bool SignalLatestWorkItem()
    {
        lock (_syncRoot)
        {
            if (_isDisposed || _latestWorkItem is null) return false;
        }

        SignalWorker();
        return true;
    }

    private void SignalWorker()
    {
        if (Interlocked.Exchange(ref _signalPending, 1) != 0) return;
        try
        {
            Signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // The latest work item is authoritative; one wake signal is sufficient.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task WaitForSignalAsync(CancellationToken cancellationToken)
    {
        await Signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _signalPending, 0);
    }

    public void DrainSignals()
    {
        while (Signal.Wait(0)) { }
        Interlocked.Exchange(ref _signalPending, 0);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { _cancellationTokenSource.Cancel(); }
            catch (ObjectDisposedException) { }
            try { Signal.Dispose(); }
            catch (ObjectDisposedException) { }
            _cancellationTokenSource.Dispose();
        }
    }
}

internal static class LiveSyntaxRequestFreshness
{
    public static bool IsCurrent(
        int currentRegistrationVersion,
        int currentRequestVersion,
        EditorDocumentSnapshot? currentSnapshot,
        LiveSyntaxWorkItem workItem)
        => currentRegistrationVersion == workItem.RegistrationVersion &&
           currentRequestVersion == workItem.RequestVersion &&
           EditorDocumentSnapshotFreshness.IsCurrent(currentSnapshot, workItem.DocumentSnapshot);
}

internal static class EditorDocumentSnapshotFreshness
{
    public static bool IsCurrent(EditorDocumentSnapshot? current, EditorDocumentSnapshot expected)
        => current is not null &&
           current.SessionId == expected.SessionId &&
           current.DocumentId == expected.DocumentId &&
           current.Revision == expected.Revision &&
           ReferenceEquals(current.DocumentVersion, expected.DocumentVersion);
}

/// <summary>Shares one immutable-snapshot-to-string allocation across analysis pumps.</summary>
internal sealed class EditorTextSnapshotMaterialization
{
    private readonly Lazy<string> _text;

    public EditorTextSnapshotMaterialization(EditorDocumentSnapshot documentSnapshot)
    {
        DocumentSnapshot = documentSnapshot ?? throw new ArgumentNullException(nameof(documentSnapshot));
        _text = new Lazy<string>(documentSnapshot.MaterializeText, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public EditorDocumentSnapshot DocumentSnapshot { get; }
    public int TextLength => DocumentSnapshot.TextLength;
    public string Text => _text.Value;
}
