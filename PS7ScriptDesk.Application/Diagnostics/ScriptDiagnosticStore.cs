namespace PS7ScriptDesk.Application.Diagnostics;

public sealed class ScriptDiagnosticsChangedEventArgs : EventArgs
{
    public ScriptDiagnosticsChangedEventArgs(Guid documentId, ScriptDiagnosticSource? sourceId, int diagnosticCount)
    {
        DocumentId = documentId;
        SourceId = sourceId;
        DiagnosticCount = diagnosticCount;
    }

    public Guid DocumentId { get; }
    public ScriptDiagnosticSource? SourceId { get; }
    public int DiagnosticCount { get; }
}

/// <summary>
/// Thread-safe store with atomic replacement per logical document and diagnostic source.
/// </summary>
public sealed class ScriptDiagnosticStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Dictionary<ScriptDiagnosticSource, IReadOnlyList<ScriptDiagnostic>>> _diagnostics = new();
    private readonly Dictionary<(Guid DocumentId, ScriptDiagnosticSource SourceId), long> _latestRevisions = new();

    public event EventHandler<ScriptDiagnosticsChangedEventArgs>? Changed;

    public bool ReplaceDiagnostics(
        Guid documentId,
        ScriptDiagnosticSource sourceId,
        long documentRevision,
        IEnumerable<ScriptDiagnostic> diagnostics)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document identity cannot be empty.", nameof(documentId));
        }
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (documentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentRevision));
        }

        var replacement = diagnostics.ToArray();
        if (replacement.Any(diagnostic => diagnostic.DocumentId != documentId ||
                                         diagnostic.SourceId != sourceId ||
                                         diagnostic.DocumentRevision != documentRevision))
        {
            throw new ArgumentException("Every diagnostic must match the replacement document, source, and revision.", nameof(diagnostics));
        }

        IReadOnlyList<ScriptDiagnostic> ordered = replacement
            .OrderBy(diagnostic => diagnostic.StartLine)
            .ThenBy(diagnostic => diagnostic.StartColumn)
            .ThenBy(diagnostic => diagnostic.EndLine)
            .ThenBy(diagnostic => diagnostic.EndColumn)
            .ThenBy(diagnostic => diagnostic.RuleId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();

        lock (_sync)
        {
            var key = (documentId, sourceId);
            if (_latestRevisions.TryGetValue(key, out var latestRevision) && documentRevision < latestRevision)
            {
                return false;
            }

            if (!_diagnostics.TryGetValue(documentId, out var bySource))
            {
                bySource = new Dictionary<ScriptDiagnosticSource, IReadOnlyList<ScriptDiagnostic>>();
                _diagnostics[documentId] = bySource;
            }

            bySource[sourceId] = ordered;
            _latestRevisions[key] = documentRevision;
        }

        Changed?.Invoke(this, new ScriptDiagnosticsChangedEventArgs(documentId, sourceId, ordered.Count));
        return true;
    }

    public bool ClearDiagnostics(Guid documentId, ScriptDiagnosticSource sourceId)
    {
        lock (_sync)
        {
            if (!_diagnostics.TryGetValue(documentId, out var bySource) || !bySource.Remove(sourceId))
            {
                return false;
            }

            if (bySource.Count == 0)
            {
                _diagnostics.Remove(documentId);
            }
        }

        Changed?.Invoke(this, new ScriptDiagnosticsChangedEventArgs(documentId, sourceId, 0));
        return true;
    }

    public bool ClearDiagnostics(Guid documentId, ScriptDiagnosticSource sourceId, long documentRevision)
    {
        if (documentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentRevision));
        }

        lock (_sync)
        {
            var key = (documentId, sourceId);
            if (_latestRevisions.TryGetValue(key, out var latestRevision) && documentRevision < latestRevision)
            {
                return false;
            }

            _latestRevisions[key] = documentRevision;
            if (!_diagnostics.TryGetValue(documentId, out var bySource) || !bySource.Remove(sourceId))
            {
                return false;
            }

            if (bySource.Count == 0)
            {
                _diagnostics.Remove(documentId);
            }
        }

        Changed?.Invoke(this, new ScriptDiagnosticsChangedEventArgs(documentId, sourceId, 0));
        return true;
    }

    public IReadOnlyList<ScriptDiagnostic> GetDiagnostics(Guid documentId)
    {
        lock (_sync)
        {
            return _diagnostics.TryGetValue(documentId, out var bySource)
                ? bySource.Values.SelectMany(static diagnostics => diagnostics).OrderBy(diagnostic => diagnostic.StartLine).ThenBy(diagnostic => diagnostic.StartColumn).ThenBy(diagnostic => diagnostic.SourceId).ToArray()
                : Array.Empty<ScriptDiagnostic>();
        }
    }

    public IReadOnlyList<ScriptDiagnostic> GetDiagnostics(Guid documentId, ScriptDiagnosticSource sourceId)
    {
        lock (_sync)
        {
            return _diagnostics.TryGetValue(documentId, out var bySource) && bySource.TryGetValue(sourceId, out var diagnostics)
                ? diagnostics.ToArray()
                : Array.Empty<ScriptDiagnostic>();
        }
    }

    public bool ClearDocument(Guid documentId)
    {
        bool changed;
        lock (_sync)
        {
            changed = _diagnostics.Remove(documentId);
            foreach (var key in _latestRevisions.Keys.Where(key => key.DocumentId == documentId).ToArray())
            {
                _latestRevisions.Remove(key);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, new ScriptDiagnosticsChangedEventArgs(documentId, null, 0));
        }

        return changed;
    }
}
