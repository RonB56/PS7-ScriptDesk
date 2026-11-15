using System.Management.Automation.Language;
using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>
/// Shares one PowerShell parser pass per current editor snapshot between folding and
/// live syntax diagnostics. AST and parser token objects are projected to compact
/// consumer data before the result is cached.
/// </summary>
internal sealed class PowerShellParseCoordinator : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, ParseEntry> _latestByDocument = new();
    private readonly BraceFoldingStrategy _foldingStrategy = new();
    private bool _disposed;
    private int _parserPassCount;

    internal int ParserPassCount => Volatile.Read(ref _parserPassCount);

    public Task<PowerShellDocumentParseResult> GetOrParseAsync(
        EditorDocumentSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        ParseEntry entry;
        ParseEntry? superseded = null;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_latestByDocument.TryGetValue(snapshot.DocumentId, out var current) && current.Matches(snapshot))
            {
                return current.Task.WaitAsync(cancellationToken);
            }

            var parserLifetime = CancellationTokenSource.CreateLinkedTokenSource(snapshot.LifetimeToken);
            var task = Task.Run(() => ParseSnapshot(snapshot, parserLifetime.Token), CancellationToken.None);
            entry = new ParseEntry(snapshot, task, parserLifetime);
            if (_latestByDocument.TryGetValue(snapshot.DocumentId, out superseded))
                _latestByDocument[snapshot.DocumentId] = entry;
            else
                _latestByDocument.Add(snapshot.DocumentId, entry);

            _ = task.ContinueWith(
                _ => parserLifetime.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        superseded?.Cancel();
        return entry.Task.WaitAsync(cancellationToken);
    }

    public void ForgetDocument(Guid documentId)
    {
        ParseEntry? entry;
        lock (_syncRoot)
        {
            if (!_latestByDocument.Remove(documentId, out entry)) return;
        }
        entry.Cancel();
    }

    private PowerShellDocumentParseResult ParseSnapshot(
        EditorDocumentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = snapshot.MaterializeText();
        try
        {
            var parserStopwatch = Stopwatch.StartNew();
            Interlocked.Increment(ref _parserPassCount);
            var ast = Parser.ParseInput(text, out Token[] parserTokens, out ParseError[] parserErrors);
            parserStopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            var errors = ConvertParseErrors(parserErrors, text.Length);
            var syntaxTokens = ConvertSyntaxTokens(parserTokens, text.Length, cancellationToken);
            IReadOnlyList<FoldingRange> foldings;
            string? foldingFailure = null;
            var foldingStopwatch = Stopwatch.StartNew();
            try
            {
                foldings = _foldingStrategy.CreateFoldings(ast, parserTokens, text, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Folding is one consumer. Its failure must not discard valid parser diagnostics.
                foldings = Array.Empty<FoldingRange>();
                foldingFailure = $"PowerShell folding calculation failed: {ex.Message}";
            }
            foldingStopwatch.Stop();

            return new PowerShellDocumentParseResult(
                snapshot.SessionId,
                snapshot.DocumentId,
                snapshot.Revision,
                snapshot.DocumentVersion,
                errors,
                syntaxTokens,
                foldings,
                ParseFailure: null,
                foldingFailure,
                parserStopwatch.Elapsed.TotalMilliseconds,
                foldingStopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PowerShellDocumentParseResult(
                snapshot.SessionId,
                snapshot.DocumentId,
                snapshot.Revision,
                snapshot.DocumentVersion,
                Array.Empty<ParseErrorInfo>(),
                Array.Empty<SyntaxTokenInfo>(),
                Array.Empty<FoldingRange>(),
                $"In-process PowerShell parsing failed: {ex.Message}",
                null,
                0,
                0);
        }
    }

    private static IReadOnlyList<ParseErrorInfo> ConvertParseErrors(ParseError[]? parserErrors, int textLength)
    {
        if (parserErrors is null || parserErrors.Length == 0) return Array.Empty<ParseErrorInfo>();
        var results = new List<ParseErrorInfo>(parserErrors.Length);
        foreach (var error in parserErrors)
        {
            if (error is null) continue;
            var start = Math.Clamp(error.Extent?.StartOffset ?? 0, 0, textLength);
            var end = Math.Clamp(error.Extent?.EndOffset ?? start, 0, textLength);
            if (textLength > 0 && start >= textLength) start = textLength - 1;
            if (end <= start) end = Math.Min(start + 1, textLength);
            results.Add(new ParseErrorInfo(error.Message ?? string.Empty, start, end));
        }
        return results;
    }

    private static IReadOnlyList<SyntaxTokenInfo> ConvertSyntaxTokens(
        Token[]? parserTokens,
        int textLength,
        CancellationToken cancellationToken)
    {
        if (parserTokens is null || parserTokens.Length == 0) return Array.Empty<SyntaxTokenInfo>();
        var results = new List<SyntaxTokenInfo>(parserTokens.Length);
        for (var index = 0; index < parserTokens.Length; index++)
        {
            if ((index & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var token = parserTokens[index];
            if (token is null) continue;
            var start = Math.Clamp(token.Extent?.StartOffset ?? 0, 0, textLength);
            var end = Math.Clamp(token.Extent?.EndOffset ?? start, 0, textLength);
            if (end <= start) continue;
            results.Add(new SyntaxTokenInfo(token.Kind.ToString(), token.Text ?? string.Empty, start, end));
        }
        return results;
    }

    public void Dispose()
    {
        ParseEntry[] entries;
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _latestByDocument.Values.ToArray();
            _latestByDocument.Clear();
        }
        foreach (var entry in entries) entry.Cancel();
    }

    private sealed class ParseEntry(
        EditorDocumentSnapshot snapshot,
        Task<PowerShellDocumentParseResult> task,
        CancellationTokenSource cancellation)
    {
        public Task<PowerShellDocumentParseResult> Task { get; } = task;

        public bool Matches(EditorDocumentSnapshot other)
            => snapshot.SessionId == other.SessionId &&
               snapshot.DocumentId == other.DocumentId &&
               snapshot.Revision == other.Revision &&
               ReferenceEquals(snapshot.DocumentVersion, other.DocumentVersion);

        public void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}

internal sealed record PowerShellDocumentParseResult(
    Guid SessionId,
    Guid DocumentId,
    long Revision,
    ITextSourceVersion DocumentVersion,
    IReadOnlyList<ParseErrorInfo> Errors,
    IReadOnlyList<SyntaxTokenInfo> SyntaxTokens,
    IReadOnlyList<FoldingRange> FoldingRanges,
    string? ParseFailure,
    string? FoldingFailure,
    double ParserMilliseconds,
    double FoldingCalculationMilliseconds)
{
    public bool IsFor(EditorDocumentSnapshot snapshot)
        => SessionId == snapshot.SessionId && DocumentId == snapshot.DocumentId &&
           Revision == snapshot.Revision && ReferenceEquals(DocumentVersion, snapshot.DocumentVersion);
}
