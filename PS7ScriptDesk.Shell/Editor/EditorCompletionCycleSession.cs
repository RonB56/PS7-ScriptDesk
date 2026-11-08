using ICSharpCode.AvalonEdit.Document;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>Immutable provider candidates and the mutable replacement context for one Tab cycle.</summary>
internal sealed class EditorCompletionCycleSession
{
    private readonly IReadOnlyList<EditorCompletionCycleCandidate> _candidates;

    public EditorCompletionCycleSession(
        object editorIdentity,
        object documentIdentity,
        object documentVersion,
        int expectedCaretOffset,
        int replacementOffset,
        int replacementLength,
        IReadOnlyList<EditorCompletionCycleCandidate> candidates)
    {
        EditorIdentity = editorIdentity;
        DocumentIdentity = documentIdentity;
        DocumentVersion = documentVersion;
        ExpectedCaretOffset = expectedCaretOffset;
        ReplacementOffset = replacementOffset;
        ReplacementLength = replacementLength;
        _candidates = candidates;
    }

    public object EditorIdentity { get; }
    public object DocumentIdentity { get; }
    public object DocumentVersion { get; private set; }
    public int ExpectedCaretOffset { get; private set; }
    public int ReplacementOffset { get; }
    public int ReplacementLength { get; }
    public int CurrentIndex { get; private set; } = -1;
    public int Count => _candidates.Count;

    public bool IsValid(object editorIdentity, object documentIdentity, object documentVersion, int caretOffset)
    {
        return ReferenceEquals(EditorIdentity, editorIdentity) &&
               ReferenceEquals(DocumentIdentity, documentIdentity) &&
               ReferenceEquals(DocumentVersion, documentVersion) &&
               ExpectedCaretOffset == caretOffset;
    }

    public int GetIndexAfterMoves(int firstDirection, int queuedDirectionDelta)
    {
        if (_candidates.Count == 0) return -1;
        var firstIndex = firstDirection < 0 ? _candidates.Count - 1 : 0;
        return Mod(firstIndex + queuedDirectionDelta, _candidates.Count);
    }

    public int GetNextIndex(int direction)
    {
        if (_candidates.Count == 0) return -1;
        if (CurrentIndex < 0) return direction < 0 ? _candidates.Count - 1 : 0;
        return Mod(CurrentIndex + (direction < 0 ? -1 : 1), _candidates.Count);
    }

    public bool TryGetCandidate(int index, out EditorCompletionCycleCandidate candidate)
    {
        if ((uint)index >= (uint)_candidates.Count)
        {
            candidate = default!;
            return false;
        }

        candidate = _candidates[index];
        return true;
    }

    public bool TryFindCandidateIndex(string text, out int index)
    {
        for (var candidateIndex = 0; candidateIndex < _candidates.Count; candidateIndex++)
        {
            if (string.Equals(_candidates[candidateIndex].Text, text, StringComparison.Ordinal))
            {
                index = candidateIndex;
                return true;
            }
        }

        index = -1;
        return false;
    }

    public int GetCurrentReplacementLength()
    {
        return CurrentIndex < 0 ? ReplacementLength : _candidates[CurrentIndex].Text.Length;
    }

    public bool TryApplyCandidate(TextDocument document, int index, out int caretOffset)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryGetCandidate(index, out var candidate))
        {
            caretOffset = 0;
            return false;
        }

        var replacementLength = GetCurrentReplacementLength();
        if (ReplacementOffset < 0 || replacementLength < 0 ||
            ReplacementOffset + replacementLength > document.TextLength)
        {
            caretOffset = 0;
            return false;
        }

        document.Replace(ReplacementOffset, replacementLength, candidate.Text);
        caretOffset = ReplacementOffset + candidate.Text.Length;
        RecordApplied(index, document.Version, caretOffset);
        return true;
    }

    public void RecordApplied(int index, object newDocumentVersion, int newCaretOffset)
    {
        if ((uint)index >= (uint)_candidates.Count) throw new ArgumentOutOfRangeException(nameof(index));
        CurrentIndex = index;
        DocumentVersion = newDocumentVersion;
        ExpectedCaretOffset = newCaretOffset;
    }

    private static int Mod(int value, int count) => (value % count + count) % count;
}

internal sealed record EditorCompletionCycleCandidate(string Text, int ReplacementOffset, int ReplacementLength);
