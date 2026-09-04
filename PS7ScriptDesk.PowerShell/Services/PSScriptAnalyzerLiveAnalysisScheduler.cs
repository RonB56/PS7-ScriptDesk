using System.Diagnostics;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed record PSScriptAnalyzerActivity(string State, Guid DocumentId, long Revision, string? RequestId);

/// <summary>Owns bounded, per-document latest-wins scheduling for live analysis.</summary>
public sealed class PSScriptAnalyzerLiveAnalysisScheduler : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(400);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingWork> _pending = new();
    private readonly Func<PSScriptAnalyzerRequest, CancellationToken, Task<bool>> _analyze;
    private readonly TimeSpan _debounce;
    private bool _disposed;

    public PSScriptAnalyzerLiveAnalysisScheduler(
        Func<PSScriptAnalyzerRequest, CancellationToken, Task<bool>> analyze,
        TimeSpan? debounce = null)
    {
        _analyze = analyze ?? throw new ArgumentNullException(nameof(analyze));
        _debounce = debounce.GetValueOrDefault(DefaultDebounce);
        if (_debounce <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
    }

    public int ScheduledRequestCount { get; private set; }

    public int DispatchedRequestCount { get; private set; }

    public event EventHandler<PSScriptAnalyzerActivity>? ActivityChanged;

    public void Schedule(
        Guid documentId,
        long revision,
        string? path,
        string scriptText,
        string severityFilter,
        CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Document identity cannot be empty.", nameof(documentId));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pending.TryGetValue(documentId, out var prior) && prior.Revision >= revision) return;
            prior?.Cancel();
            var work = new PendingWork(documentId, revision, path, scriptText ?? string.Empty, severityFilter);
            _pending[documentId] = work;
            ScheduledRequestCount++;
            _ = RunAfterDebounceAsync(work, cancellationToken);
        }
        ActivityChanged?.Invoke(this, new PSScriptAnalyzerActivity("Waiting", documentId, revision, null));
    }

    public void Cancel(Guid documentId)
    {
        lock (_sync)
        {
            if (_pending.Remove(documentId, out var work)) work.Cancel();
        }
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            foreach (var work in _pending.Values) work.Cancel();
            _pending.Clear();
        }
    }

    private async Task RunAfterDebounceAsync(PendingWork work, CancellationToken externalCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(work.Cancellation.Token, externalCancellation);
        try
        {
            await Task.Delay(_debounce, cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || !_pending.TryGetValue(work.DocumentId, out var current) || !ReferenceEquals(current, work)) return;
            }
            var request = new PSScriptAnalyzerRequest(
                $"live-{Guid.NewGuid():N}",
                work.DocumentId.ToString(),
                work.Revision,
                work.Path,
                work.ScriptText,
                work.SeverityFilter);
            lock (_sync) DispatchedRequestCount++;
            ActivityChanged?.Invoke(this, new PSScriptAnalyzerActivity("Analyzing", work.DocumentId, work.Revision, request.RequestId));
            await _analyze(request, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ActivityChanged?.Invoke(this, new PSScriptAnalyzerActivity("Canceled", work.DocumentId, work.Revision, null));
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogOperationFailure("PSScriptAnalyzer", "LiveAnalysis", "Live analyzer request failed.", ex,
                additionalProperties: new Dictionary<string, object?> { ["documentId"] = work.DocumentId, ["revision"] = work.Revision });
        }
        finally
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(work.DocumentId, out var current) && ReferenceEquals(current, work)) _pending.Remove(work.DocumentId);
            }
            work.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var work in _pending.Values) work.Cancel();
            _pending.Clear();
        }
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PSScriptAnalyzerLiveAnalysisScheduler)); }

    private sealed class PendingWork : IDisposable
    {
        public PendingWork(Guid documentId, long revision, string? path, string scriptText, string severityFilter)
        { DocumentId = documentId; Revision = revision; Path = path; ScriptText = scriptText; SeverityFilter = severityFilter; }
        public Guid DocumentId { get; }
        public long Revision { get; }
        public string? Path { get; }
        public string ScriptText { get; }
        public string SeverityFilter { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public void Cancel() { if (!Cancellation.IsCancellationRequested) Cancellation.Cancel(); }
        public void Dispose() => Cancellation.Dispose();
    }
}
