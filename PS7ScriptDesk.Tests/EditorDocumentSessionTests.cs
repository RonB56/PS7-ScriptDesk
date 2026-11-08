using PS7ScriptDesk.Shell.Editor;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.UI.ViewModels;
using ICSharpCode.AvalonEdit;
using System.Windows.Threading;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class EditorDocumentSessionTests
{
    [Fact]
    public void SessionKeepsOneDocumentAndSnapshotAcrossEditorUnregistration()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("Untitled.ps1", "first\r\nline");
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            var document = session.Document;
            var firstRegistration = session.Register();
            var snapshot = session.CaptureSnapshot();

            Assert.Equal(tab.DiagnosticDocument.DocumentId, snapshot.DocumentId);
            Assert.Equal(tab.DiagnosticDocument.Revision, snapshot.Revision);
            Assert.Same(document.Version, snapshot.DocumentVersion);
            Assert.Equal(tab.Content, Task.Run(() => snapshot.TextSource.Text).GetAwaiter().GetResult());
            Assert.True(session.IsRegistrationCurrent(snapshot));

            session.Unregister();
            Assert.Same(document, session.Document);
            Assert.True(session.IsCurrent(snapshot));
            Assert.False(session.IsRegistrationCurrent(snapshot));
            Assert.True(session.Register() > firstRegistration);
            Assert.False(session.IsRegistrationCurrent(snapshot));
        });
    }

    [Fact]
    public void GroupedEditsUndoAndRedoAdvanceRevisionWithoutEagerContentSynchronization()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("script.ps1", "base");
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            var changes = new List<EditorDocumentChangeInfo>();
            session.DocumentChanged += (_, change) => changes.Add(change);
            var editor = new TextEditor { Document = session.Document };
            var updateStateAtTextChanged = new List<bool>();
            editor.TextChanged += (_, _) =>
            {
                updateStateAtTextChanged.Add(editor.Document.IsInUpdate);
                session.CommitEditorMutation(editor.Document.LineCount);
            };
            var original = session.CaptureSnapshot();

            session.Document.BeginUpdate();
            try
            {
                session.Document.Insert(4, "1");
                session.Document.Insert(5, "2");
                Assert.Throws<InvalidOperationException>(() => session.CaptureSnapshot());
            }
            finally
            {
                session.Document.EndUpdate();
            }
            DrainEditorDispatch();

            Assert.Equal("base12", tab.Content);
            Assert.Equal(1, session.Revision);
            Assert.Equal(2, changes.Count);
            Assert.Contains(true, updateStateAtTextChanged);
            Assert.Equal((4, 0, 1), (changes[0].Offset, changes[0].RemovedLength, changes[0].InsertedLength));
            Assert.False(session.IsCurrent(original));

            session.Document.UndoStack.Undo();
            DrainEditorDispatch();
            Assert.Equal("base", tab.Content);
            Assert.Equal(2, session.Revision);

            session.Document.UndoStack.Redo();
            DrainEditorDispatch();
            Assert.Equal("base12", tab.Content);
            Assert.Equal(3, session.Revision);
            Assert.Equal(tab.Content, session.CaptureSnapshot().TextSource.Text);
        });
    }

    [Fact]
    public void PasteReplacementAndImmediateSnapshotCommitAfterAvalonEditUpdateCompletes()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("paste.ps1", "before after");
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            var editor = new TextEditor { Document = session.Document };
            var textChangedRaisedInUpdate = false;
            editor.TextChanged += (_, _) =>
            {
                textChangedRaisedInUpdate |= editor.Document.IsInUpdate;
                session.CommitEditorMutation(editor.Document.LineCount);
            };
            var initialRevision = session.Revision;

            // AvalonEdit's Replace is the same document mutation used by selection paste.
            editor.Document.Replace(7, 5, "pasted text");

            Assert.True(textChangedRaisedInUpdate);
            var snapshot = session.CaptureSnapshot();
            Assert.Equal("before pasted text", snapshot.TextSource.Text);
            Assert.Equal(initialRevision + 1, snapshot.Revision);
            Assert.True(tab.IsDirty);
            Assert.Equal(snapshot.Revision, tab.DocumentRevision);
        });
    }

    [Fact]
    public void SingleCharacterEditInLargeDocumentAdvancesDirtyRevisionAndSnapshot()
    {
        WpfTestDispatcher.Run(() =>
        {
            var initial = new string('x', 1_000_000);
            var tab = new EditorTabViewModel("large.ps1", initial);
            var contentNotificationCount = 0;
            tab.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EditorTabViewModel.Content)) contentNotificationCount++;
            };
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            var editor = new TextEditor { Document = session.Document };
            editor.TextChanged += (_, _) => session.CommitEditorMutation(editor.Document.LineCount);
            var previousRevision = session.Revision;
            editor.Document.Insert(500_000, "y");
            var committedRevision = session.CaptureSnapshot().Revision;
            Assert.True(tab.IsDirty);
            Assert.Equal(previousRevision + 1, committedRevision);
            Assert.Equal(1, contentNotificationCount);
            Assert.False(tab.IsEditorDocumentMutationNotification);
            Assert.Equal(1_000_001, session.Document.TextLength);
        });
    }

    [Fact]
    public void DisposalAfterEditorMutationFlushesThePendingRevisionAndRetainsContent()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("shutdown.ps1", "old");
            var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            var editor = new TextEditor { Document = session.Document };
            editor.TextChanged += (_, _) => session.CommitEditorMutation(editor.Document.LineCount);
            editor.Document.Replace(0, 3, "new text");

            session.Dispose();

            Assert.Equal(1, tab.DocumentRevision);
            Assert.Equal("new text", tab.Content);
            Assert.Throws<ObjectDisposedException>(() => session.CaptureSnapshot());
        });
    }

    [Fact]
    public void DebuggerContentAvailabilityUsesDocumentLengthDuringAnEditAndFallsBackWithoutSession()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("debug.ps1", "x");
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            session.Document.BeginUpdate();
            try
            {
                session.Document.Insert(1, "y");
                Assert.True(MainWindow.HasDebuggableContent(tab, session));
            }
            finally
            {
                session.Document.EndUpdate();
            }

            Assert.True(session.CommitEditorMutation(session.Document.LineCount));
            Assert.True(MainWindow.HasDebuggableContent(tab, session));
            Assert.True(MainWindow.HasDebuggableContent(tab, null));
        });
    }

    private static void DrainEditorDispatch()
        => WpfTestDispatcher.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    [Fact]
    public void SeparateTabsKeepIndependentDocumentIdentityAndRevision()
    {
        WpfTestDispatcher.Run(() =>
        {
            var firstTab = new EditorTabViewModel("first.ps1", "first");
            var secondTab = new EditorTabViewModel("second.ps1", "second");
            using var first = new EditorDocumentSession(firstTab, WpfTestDispatcher.Dispatcher);
            using var second = new EditorDocumentSession(secondTab, WpfTestDispatcher.Dispatcher);
            first.Register();
            second.Register();
            first.Document.Insert(first.Document.TextLength, "!");
            Assert.True(first.CommitEditorMutation(first.Document.LineCount));

            Assert.Equal(1, first.Revision);
            Assert.Equal(0, second.Revision);
            Assert.NotEqual(first.DocumentId, second.DocumentId);
            Assert.Equal("second", secondTab.Content);
        });
    }

    [Fact]
    public void ProgrammaticReplacementUpdatesUnloadedDocumentAndInvalidatesSnapshot()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("file.ps1", "old", "C:\\Scripts\\file.ps1");
            using var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            session.Register();
            var previous = session.CaptureSnapshot();
            session.Unregister();

            tab.Content = "new\r\ntext";
            Assert.Equal("new\r\ntext", session.Document.Text);
            Assert.Equal(1, session.Revision);
            Assert.False(session.IsCurrent(previous));
            Assert.Equal(tab.Content, session.CaptureSnapshot().TextSource.Text);
            Assert.False(session.Document.UndoStack.CanUndo);
            Assert.Equal(1, tab.DocumentRevision);
        });
    }

    [Fact]
    public void DisposingSessionCancelsLifetimeAndRejectsStaleUse()
    {
        WpfTestDispatcher.Run(() =>
        {
            var tab = new EditorTabViewModel("script.ps1", "text");
            var session = new EditorDocumentSession(tab, WpfTestDispatcher.Dispatcher);
            session.Register();
            var snapshot = session.CaptureSnapshot();
            session.Dispose();

            Assert.True(snapshot.LifetimeToken.IsCancellationRequested);
            Assert.False(session.IsCurrent(snapshot));
            Assert.Throws<ObjectDisposedException>(() => session.CaptureSnapshot());
            Assert.Equal("text", Task.Run(() => snapshot.TextSource.Text).GetAwaiter().GetResult());
        });
    }
}
