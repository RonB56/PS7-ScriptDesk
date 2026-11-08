using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>
/// Owns the single AvalonEdit document for a tab, independently of the visual editor.
/// The R48 Content API remains available through lazy immutable snapshots; edits advance
/// logical revisions without materializing or comparing full document text.
/// </summary>
internal sealed class EditorDocumentSession : IDisposable
{
    private readonly EditorTabViewModel _tab;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private int _editorMirrorUpdateDepth;
    private bool _pendingMirrorSynchronization;
    private bool _pendingDocumentChange;
    private bool _editorCommitWasDeferred;
    private bool _isRegistered;
    private bool _isDisposed;
    private long _registrationGeneration;
    private long _sessionGeneration = 1;
    private long _mutationGeneration;
    private DispatcherOperation? _pendingEditorCommit;

    public EditorDocumentSession(EditorTabViewModel tab, Dispatcher dispatcher)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _dispatcher.VerifyAccess();
        Document = new TextDocument(tab.Content ?? string.Empty);
        Document.Changed += Document_Changed;
        _tab.PropertyChanged += Tab_PropertyChanged;
        _tab.AttachContentSnapshotProvider(CaptureSnapshotForContentAccess);

        if (DeveloperDiagnostics.IsEnabled)
        {
            DeveloperDiagnostics.LogInfo("EditorSession", "Editor document session created.", Metadata());
        }
    }

    public TextDocument Document { get; }
    public Guid DocumentId => _tab.DiagnosticDocument.DocumentId;
    public long Revision => _tab.DiagnosticDocument.Revision;
    public long RegistrationGeneration => _registrationGeneration;
    public bool IsRegistered => _isRegistered;
    public bool IsDisposed => _isDisposed;
    public CancellationToken LifetimeToken => _lifetime.Token;

    public ITextSourceVersion DocumentVersion
    {
        get
        {
            VerifyActive();
            return Document.Version;
        }
    }

    public event EventHandler<EditorDocumentChangeInfo>? DocumentChanged;
    public event EventHandler? EditorMutationCommitted;

    public long Register()
    {
        VerifyActive();
        _isRegistered = true;
        _registrationGeneration++;
        LogLifecycle("Registered");
        return _registrationGeneration;
    }

    public void Unregister()
    {
        VerifyAccess();
        if (_isDisposed || !_isRegistered) return;
        _isRegistered = false;
        _registrationGeneration++;
        LogLifecycle("Unregistered");
    }

    /// <summary>Commits one editor mutation after TextChanged without reading Document.Text.</summary>
    public bool CommitEditorMutation(int lineCount)
    {
        VerifyActive();
        if (!_pendingDocumentChange) return false;
        if (Document.IsInUpdate)
        {
            ScheduleEditorMutationCommit();
            return false;
        }

        return CommitPendingEditorMutation(lineCount);
    }

    private bool CommitPendingEditorMutation(int lineCount)
    {
        if (!_pendingDocumentChange || _isDisposed) return false;
        if (Document.IsInUpdate) return false;

        var notificationDeferral = _tab.DeferPropertyChangedNotifications();
        _editorMirrorUpdateDepth++;
        var before = Revision;
        try
        {
            _tab.UpdateDocumentStateFromEditor(lineCount);
        }
        finally
        {
            _editorMirrorUpdateDepth--;
            _pendingMirrorSynchronization = false;
            _pendingDocumentChange = false;
            notificationDeferral.Dispose();
        }

        var wasDeferred = _editorCommitWasDeferred;
        if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled() && Revision != before)
        {
            DeveloperDiagnostics.LogDebug("EditorSession", "Editor revision and view-model notifications completed after synchronization guards cleared.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = DocumentId,
                    ["revisionBefore"] = before,
                    ["revisionAfter"] = Revision,
                    ["documentLength"] = Document.TextLength,
                    ["lineCount"] = lineCount,
                    ["commitDeferredUntilUpdateCompleted"] = wasDeferred,
                    ["propertyObserversSawCommittedSnapshotState"] = true
                });
        }

        _editorCommitWasDeferred = false;
        if (Revision != before)
        {
            EditorMutationCommitted?.Invoke(this, EventArgs.Empty);
        }

        return Revision != before;
    }

    private void ScheduleEditorMutationCommit()
    {
        if (_pendingEditorCommit is { Status: DispatcherOperationStatus.Pending }) return;

        _editorCommitWasDeferred = true;
        var sessionGeneration = _sessionGeneration;
        var mutationGeneration = _mutationGeneration;
        if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
        {
            DeveloperDiagnostics.LogDebug("EditorSession", "Editor mutation commit deferred until AvalonEdit completes its document update.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = DocumentId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["mutationGeneration"] = mutationGeneration,
                    ["documentLength"] = Document.TextLength,
                    ["dispatcherPriority"] = DispatcherPriority.DataBind.ToString()
                });
        }
        _pendingEditorCommit = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _pendingEditorCommit = null;
            if (_isDisposed || sessionGeneration != _sessionGeneration || !_pendingDocumentChange) return;
            if (Document.IsInUpdate)
            {
                ScheduleEditorMutationCommit();
                return;
            }

            CommitPendingEditorMutation(Document.LineCount);
        }));
    }

    public void CompleteProgrammaticSynchronization()
    {
        VerifyActive();
        _pendingMirrorSynchronization = false;
        _pendingDocumentChange = false;
    }

    public EditorDocumentSnapshot CaptureSnapshot()
        => CaptureSnapshot(logCapture: true);

    private EditorDocumentSnapshot CaptureSnapshotForContentAccess()
        => CaptureSnapshot(logCapture: false);

    private EditorDocumentSnapshot CaptureSnapshot(bool logCapture)
    {
        VerifyActive();
        if (_pendingDocumentChange && !Document.IsInUpdate && _editorMirrorUpdateDepth == 0)
        {
            _pendingEditorCommit?.Abort();
            _pendingEditorCommit = null;
            CommitPendingEditorMutation(Document.LineCount);
        }

        if (Document.IsInUpdate || _pendingMirrorSynchronization || _editorMirrorUpdateDepth != 0)
        {
            throw new InvalidOperationException("A document snapshot cannot be captured during an uncommitted content synchronization.");
        }

        var stopwatch = logCapture && DeveloperDiagnostics.IsEnabled ? Stopwatch.StartNew() : null;
        var textSource = Document.CreateSnapshot();
        var snapshot = new EditorDocumentSnapshot(
            _sessionId, DocumentId, Revision, textSource.Version, textSource,
            _registrationGeneration, _lifetime.Token);
        if (stopwatch is not null)
        {
            stopwatch.Stop();
            DeveloperDiagnostics.LogInfo("EditorSession", "Immutable editor snapshot captured.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = DocumentId,
                    ["revision"] = snapshot.Revision,
                    ["documentLength"] = textSource.TextLength,
                    ["registrationGeneration"] = _registrationGeneration,
                    ["elapsedMicroseconds"] = stopwatch.ElapsedTicks * 1_000_000d / Stopwatch.Frequency
                });
        }

        return snapshot;
    }

    public bool IsCurrent(EditorDocumentSnapshot snapshot)
    {
        VerifyAccess();
        return !_isDisposed && !_pendingMirrorSynchronization &&
               snapshot.SessionId == _sessionId && snapshot.DocumentId == DocumentId &&
               snapshot.Revision == Revision &&
               ReferenceEquals(snapshot.DocumentVersion, Document.Version);
    }

    public bool IsRegistrationCurrent(EditorDocumentSnapshot snapshot)
        => IsCurrent(snapshot) && _isRegistered && snapshot.RegistrationGeneration == _registrationGeneration;

    private void Document_Changed(object? sender, DocumentChangeEventArgs e)
    {
        VerifyAccess();
        _mutationGeneration++;
        _pendingMirrorSynchronization = true;
        _pendingDocumentChange = true;
        DocumentChanged?.Invoke(this, new EditorDocumentChangeInfo(
            e.Offset, e.RemovalLength, e.InsertionLength, Document.TextLength, Document.Version));

        if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
        {
            DeveloperDiagnostics.LogDebug("EditorSession", "AvalonEdit document changed.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = DocumentId,
                    ["revisionBeforeMirror"] = Revision,
                    ["offset"] = e.Offset,
                    ["removedLength"] = e.RemovalLength,
                    ["insertedLength"] = e.InsertionLength,
                    ["documentLength"] = Document.TextLength
                });
        }
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorTabViewModel.Content) || _editorMirrorUpdateDepth != 0) return;
        VerifyActive();
        // A registered editor keeps the existing R48 reverse-sync path, including its
        // TextChanged suppression. Only an unloaded tab needs session-owned sync.
        if (_isRegistered) return;
        var content = _tab.Content ?? string.Empty;
        if (!string.Equals(Document.Text, content, StringComparison.Ordinal))
        {
            var oldLength = Document.TextLength;
            Document.Text = content;
            Document.UndoStack = new UndoStack();
            if (DeveloperDiagnostics.IsEnabled)
            {
                DeveloperDiagnostics.LogInfo("EditorSession", "Programmatic content synchronized into the authoritative document.",
                    new Dictionary<string, object?>
                    {
                        ["documentId"] = DocumentId,
                        ["revision"] = Revision,
                        ["oldLength"] = oldLength,
                        ["newLength"] = Document.TextLength,
                        ["editorRegistered"] = _isRegistered
                    });
            }
        }
        _pendingMirrorSynchronization = false;
        _pendingDocumentChange = false;
    }

    private void VerifyActive()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void VerifyAccess() => _dispatcher.VerifyAccess();

    private Dictionary<string, object?> Metadata() => new()
    {
        ["documentId"] = DocumentId,
        ["revision"] = Revision,
        ["documentLength"] = Document.TextLength,
        ["registrationGeneration"] = _registrationGeneration,
        ["mutationGeneration"] = _mutationGeneration,
        ["sessionGeneration"] = _sessionGeneration
    };

    private void LogLifecycle(string state)
    {
        if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
        {
            DeveloperDiagnostics.LogDebug("EditorSession", $"Editor document session {state}.", Metadata());
        }
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_isDisposed) return;
        if (_pendingDocumentChange && !Document.IsInUpdate) CommitPendingEditorMutation(Document.LineCount);
        _sessionGeneration++;
        _pendingEditorCommit?.Abort();
        _pendingEditorCommit = null;
        _tab.DetachContentSnapshotProvider();
        _isDisposed = true;
        _isRegistered = false;
        _registrationGeneration++;
        _tab.PropertyChanged -= Tab_PropertyChanged;
        Document.Changed -= Document_Changed;
        _lifetime.Cancel();
        if (DeveloperDiagnostics.IsEnabled)
        {
            DeveloperDiagnostics.LogInfo("EditorSession", "Editor document session disposed.", Metadata());
        }
        _lifetime.Dispose();
    }
}

internal sealed record EditorDocumentChangeInfo(
    int Offset, int RemovedLength, int InsertedLength, int DocumentLength, ITextSourceVersion DocumentVersion);

/// <summary>Immutable source and metadata; it never exposes a live document or editor control.</summary>
internal sealed record EditorDocumentSnapshot(
    Guid SessionId,
    Guid DocumentId,
    long Revision,
    ITextSourceVersion DocumentVersion,
    ITextSource TextSource,
    long RegistrationGeneration,
    CancellationToken LifetimeToken) : IEditorTextSnapshot
{
    public int TextLength => TextSource.TextLength;

    public string MaterializeText() => TextSource.Text;
}
