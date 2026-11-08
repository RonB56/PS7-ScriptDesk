using PS7ScriptDesk.Shell.Editor;
using ICSharpCode.AvalonEdit.Document;

namespace PS7ScriptDesk.Tests;

public sealed class EditorCompletionCycleSessionTests
{
    [Fact]
    public void ForwardAndReverseMovementWrapAround()
    {
        var session = CreateSession();

        Assert.Equal(0, session.GetNextIndex(1));
        session.RecordApplied(0, new object(), 20);
        Assert.Equal(1, session.GetNextIndex(1));
        session.RecordApplied(2, new object(), 32);
        Assert.Equal(0, session.GetNextIndex(1));
        Assert.Equal(1, session.GetNextIndex(-1));
    }

    [Fact]
    public void QueuedTabDirectionsResolveDeterministicallyAfterPendingRequest()
    {
        var session = CreateSession();

        Assert.Equal(2, session.GetIndexAfterMoves(firstDirection: 1, queuedDirectionDelta: -1));
        Assert.Equal(1, session.GetIndexAfterMoves(firstDirection: -1, queuedDirectionDelta: 2));
        Assert.Equal(0, session.GetIndexAfterMoves(firstDirection: 1, queuedDirectionDelta: 3));
    }

    [Fact]
    public void CurrentReplacementLengthTracksCandidateLengthWithoutMovingBaseOffset()
    {
        var session = CreateSession();

        Assert.Equal(1, session.GetCurrentReplacementLength());
        session.RecordApplied(1, new object(), 24);
        Assert.Equal("Filter", AssertCandidate(session, 1).Text);
        Assert.Equal(6, session.GetCurrentReplacementLength());
        Assert.Equal(15, session.ReplacementOffset);
        session.RecordApplied(0, new object(), 21);
        Assert.Equal(4, session.GetCurrentReplacementLength());
        Assert.Equal(15, session.ReplacementOffset);
    }

    [Fact]
    public void DocumentCaretOrEditorChangesInvalidateSessionButInternalApplyCanRefreshIt()
    {
        var editor = new object();
        var document = new object();
        var version = new object();
        var session = new EditorCompletionCycleSession(
            editor, document, version, 15, 14, 1,
            [new EditorCompletionCycleCandidate("Path", 14, 1)]);

        Assert.True(session.IsValid(editor, document, version, 15));
        Assert.False(session.IsValid(new object(), document, version, 15));
        Assert.False(session.IsValid(editor, document, new object(), 15));
        Assert.False(session.IsValid(editor, document, version, 14));

        var nextVersion = new object();
        session.RecordApplied(0, nextVersion, 19);
        Assert.True(session.IsValid(editor, document, nextVersion, 19));
    }

    [Fact]
    public void AvalonEditDocumentVersionInvalidatesCycleAfterAnUnrelatedEdit()
    {
        var document = new TextDocument("Get-ChildItem -Pa");
        var editor = new object();
        var session = new EditorCompletionCycleSession(
            editor,
            document,
            document.Version,
            expectedCaretOffset: document.TextLength,
            replacementOffset: 14,
            replacementLength: 2,
            [new EditorCompletionCycleCandidate("Path", 14, 2)]);

        Assert.True(session.IsValid(editor, document, document.Version, document.TextLength));

        document.Insert(0, "# unrelated edit\n");

        Assert.False(session.IsValid(editor, document, document.Version, document.TextLength));
    }

    [Theory]
    [InlineData("Get-", 0, 4, "Get-Command", "Get-Content", "Get-ChildItem")]
    [InlineData("Get-ChildItem -", 14, 1, "-Path", "-Filter", "-Force")]
    public void ConsecutiveCandidateReplacementsRefreshDocumentVersionAndPreserveSurroundingText(
        string source,
        int replacementOffset,
        int replacementLength,
        string first,
        string second,
        string third)
    {
        var document = new TextDocument($"before {source} after");
        var editor = new object();
        var absoluteReplacementOffset = replacementOffset + 7;
        var unchangedPrefix = document.GetText(0, absoluteReplacementOffset);
        var versionBeforeFirstTab = document.Version;
        var candidates = new[]
        {
            new EditorCompletionCycleCandidate(first, absoluteReplacementOffset, replacementLength),
            new EditorCompletionCycleCandidate(second, absoluteReplacementOffset, replacementLength),
            new EditorCompletionCycleCandidate(third, absoluteReplacementOffset, replacementLength)
        };
        var session = new EditorCompletionCycleSession(
            editor,
            document,
            document.Version,
            document.TextLength,
            absoluteReplacementOffset,
            replacementLength,
            candidates);

        var firstIndex = session.GetNextIndex(1);
        Assert.True(session.TryApplyCandidate(document, firstIndex, out var firstCaret));
        Assert.NotSame(versionBeforeFirstTab, document.Version);
        Assert.Equal(unchangedPrefix + first, document.GetText(0, firstCaret));
        Assert.EndsWith(" after", document.Text, StringComparison.Ordinal);
        Assert.True(session.IsValid(editor, document, document.Version, firstCaret));

        var secondIndex = session.GetNextIndex(1);
        Assert.True(session.TryApplyCandidate(document, secondIndex, out var secondCaret));
        Assert.Equal(unchangedPrefix + second, document.GetText(0, secondCaret));
        Assert.True(session.IsValid(editor, document, document.Version, secondCaret));

        var thirdIndex = session.GetNextIndex(1);
        Assert.True(session.TryApplyCandidate(document, thirdIndex, out var thirdCaret));
        Assert.Equal(unchangedPrefix + third, document.GetText(0, thirdCaret));
        Assert.EndsWith(" after", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateReplacementSupportsUndoAndRedo()
    {
        var document = new TextDocument("before Get- after");
        var editor = new object();
        var session = new EditorCompletionCycleSession(
            editor,
            document,
            document.Version,
            document.TextLength,
            replacementOffset: 7,
            replacementLength: 4,
            [
                new EditorCompletionCycleCandidate("Get-Command", 7, 4),
                new EditorCompletionCycleCandidate("Get-Content", 7, 4)
            ]);

        Assert.True(session.TryApplyCandidate(document, 0, out _));
        Assert.Equal("before Get-Command after", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("before Get- after", document.Text);
        document.UndoStack.Redo();
        Assert.Equal("before Get-Command after", document.Text);
    }

    [Fact]
    public void CaretAndDocumentReplacementInvalidateTheActiveCycle()
    {
        var editor = new object();
        var originalDocument = new TextDocument("Get-");
        var session = new EditorCompletionCycleSession(
            editor,
            originalDocument,
            originalDocument.Version,
            expectedCaretOffset: 4,
            replacementOffset: 0,
            replacementLength: 4,
            [new EditorCompletionCycleCandidate("Get-Command", 0, 4)]);

        Assert.False(session.IsValid(editor, originalDocument, originalDocument.Version, caretOffset: 3));
        Assert.False(session.IsValid(editor, new TextDocument("Get-"), originalDocument.Version, caretOffset: 4));
    }

    private static EditorCompletionCycleSession CreateSession()
    {
        return new EditorCompletionCycleSession(
            new object(), new object(), new object(), 15, 15, 1,
            [
                new EditorCompletionCycleCandidate("Path", 15, 1),
                new EditorCompletionCycleCandidate("Filter", 15, 1),
                new EditorCompletionCycleCandidate("PipelineVariable", 15, 1)
            ]);
    }

    private static EditorCompletionCycleCandidate AssertCandidate(EditorCompletionCycleSession session, int index)
    {
        Assert.True(session.TryGetCandidate(index, out var candidate));
        return candidate;
    }
}
