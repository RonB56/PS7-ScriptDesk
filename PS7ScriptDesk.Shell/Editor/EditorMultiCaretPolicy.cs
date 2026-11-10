using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace PS7ScriptDesk.Shell.Editor;

internal enum EditorMultiCaretCommand
{
    Copy,
    Cut,
    Paste,
    SelectAll,
    Undo,
    Redo,
    AddCaret,
    Escape,
    IntelliSense,
    RunSelection,
    CommentIndentTransforms,
    DuplicateMoveDeleteLines,
    SearchReplace
}

internal enum EditorMultiCaretPolicy
{
    MultiTarget,
    NativeSelectionAndNormalize,
    NativeUndoRedo,
    PreserveExistingCaretBehavior,
    ClearSecondaryCarets,
    SuppressWhileMultipleCaretsAreActive,
    DeferredPrimaryOnlyWithCleanup
}

/// <summary>Central record of application command ownership when secondary carets are active.</summary>
internal static class EditorMultiCaretPolicyResolver
{
    internal static bool UsesMultiTargetRoute(EditorMultiCaretCommand command, TextArea textArea) =>
        GetPolicy(command) == EditorMultiCaretPolicy.MultiTarget && textArea.MultiCaretCount > 1;

    internal static EditorMultiCaretPolicy GetPolicy(EditorMultiCaretCommand command) => command switch
    {
        EditorMultiCaretCommand.Copy or EditorMultiCaretCommand.Cut or EditorMultiCaretCommand.Paste
            => EditorMultiCaretPolicy.MultiTarget,
        EditorMultiCaretCommand.SelectAll => EditorMultiCaretPolicy.NativeSelectionAndNormalize,
        EditorMultiCaretCommand.Undo or EditorMultiCaretCommand.Redo => EditorMultiCaretPolicy.NativeUndoRedo,
        EditorMultiCaretCommand.AddCaret => EditorMultiCaretPolicy.PreserveExistingCaretBehavior,
        EditorMultiCaretCommand.Escape => EditorMultiCaretPolicy.ClearSecondaryCarets,
        EditorMultiCaretCommand.IntelliSense => EditorMultiCaretPolicy.SuppressWhileMultipleCaretsAreActive,
        EditorMultiCaretCommand.RunSelection or EditorMultiCaretCommand.CommentIndentTransforms or
            EditorMultiCaretCommand.DuplicateMoveDeleteLines or EditorMultiCaretCommand.SearchReplace
            => EditorMultiCaretPolicy.DeferredPrimaryOnlyWithCleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
}

internal interface IEditorClipboard
{
    bool ContainsText();
    string GetText();
    void SetText(string text);
}

internal sealed class WpfEditorClipboard : IEditorClipboard
{
    public bool ContainsText() => System.Windows.Clipboard.ContainsText();
    public string GetText() => System.Windows.Clipboard.GetText();
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}

internal enum EditorClipboardFailure
{
    None,
    NoSelection,
    EmptyClipboard,
    ClipboardUnavailable,
    ReadOnly,
    EditRejected,
    EditFailed
}

internal readonly record struct EditorClipboardResult(
    bool Handled,
    bool Succeeded,
    EditorClipboardFailure Failure,
    int RangeCount,
    int TextLength,
    Exception? Exception = null);

/// <summary>Routes all ScriptDesk editor clipboard surfaces through one implementation.</summary>
internal sealed class EditorClipboardService
{
    private readonly IEditorClipboard _clipboard;

    internal EditorClipboardService(IEditorClipboard clipboard) =>
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    internal EditorClipboardResult Copy(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (EditorMultiCaretPolicyResolver.UsesMultiTargetRoute(EditorMultiCaretCommand.Copy, editor.TextArea))
        {
            var selections = editor.TextArea.GetMultiCaretSelectedTexts();
            if (selections.Count == 0)
                return new(true, true, EditorClipboardFailure.None, 0, 0);

            var text = EditorClipboardText.CombineSelections(editor.Document, selections);
            return TrySetClipboard(text, selections.Count);
        }

        var selectedText = editor.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
            return new(false, false, EditorClipboardFailure.NoSelection, 0, 0);
        return TrySetClipboard(selectedText, 1);
    }

    internal EditorClipboardResult Cut(TextEditor editor, Action<TextEditor, string> replaceSingleSelection)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(replaceSingleSelection);
        if (EditorMultiCaretPolicyResolver.UsesMultiTargetRoute(EditorMultiCaretCommand.Cut, editor.TextArea))
        {
            var selections = editor.TextArea.GetMultiCaretSelectedTexts();
            if (selections.Count == 0)
                return new(true, true, EditorClipboardFailure.None, 0, 0);
            if (editor.IsReadOnly)
                return new(true, false, EditorClipboardFailure.ReadOnly, selections.Count, 0);

            var text = EditorClipboardText.CombineSelections(editor.Document, selections);
            var clipboardResult = TrySetClipboard(text, selections.Count);
            if (!clipboardResult.Succeeded)
                return clipboardResult;

            try
            {
                return editor.TextArea.TryDeleteMultiCaretSelections()
                    ? new(true, true, EditorClipboardFailure.None, selections.Count, text.Length)
                    : new(true, false, EditorClipboardFailure.EditRejected, selections.Count, text.Length);
            }
            catch (Exception ex)
            {
                return new(true, false, EditorClipboardFailure.EditFailed, selections.Count, text.Length, ex);
            }
        }

        var selectedText = editor.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
            return new(false, false, EditorClipboardFailure.NoSelection, 0, 0);
        var copyResult = TrySetClipboard(selectedText, 1);
        if (!copyResult.Succeeded)
            return copyResult;
        replaceSingleSelection(editor, string.Empty);
        return new(true, true, EditorClipboardFailure.None, 1, selectedText.Length);
    }

    internal EditorClipboardResult Paste(TextEditor editor, Action<TextEditor, string> replaceSingleSelection)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(replaceSingleSelection);

        string text;
        try
        {
            if (!_clipboard.ContainsText())
                return new(true, false, EditorClipboardFailure.EmptyClipboard, 0, 0);
            text = _clipboard.GetText();
        }
        catch (Exception ex)
        {
            return new(true, false, EditorClipboardFailure.ClipboardUnavailable, 0, 0, ex);
        }

        if (EditorMultiCaretPolicyResolver.UsesMultiTargetRoute(EditorMultiCaretCommand.Paste, editor.TextArea))
        {
            if (text.Length == 0)
                return new(true, true, EditorClipboardFailure.None, editor.TextArea.MultiCaretCount, 0);
            if (editor.IsReadOnly)
                return new(true, false, EditorClipboardFailure.ReadOnly, editor.TextArea.MultiCaretCount, text.Length);

            try
            {
                return editor.TextArea.TryReplaceMultiCaretSelections(text)
                    ? new(true, true, EditorClipboardFailure.None, editor.TextArea.MultiCaretCount, text.Length)
                    : new(true, false, EditorClipboardFailure.EditRejected, editor.TextArea.MultiCaretCount, text.Length);
            }
            catch (Exception ex)
            {
                return new(true, false, EditorClipboardFailure.EditFailed, editor.TextArea.MultiCaretCount, text.Length, ex);
            }
        }

        replaceSingleSelection(editor, text);
        return new(true, true, EditorClipboardFailure.None, 1, text.Length);
    }

    private EditorClipboardResult TrySetClipboard(string text, int rangeCount)
    {
        try
        {
            _clipboard.SetText(text);
            return new(true, true, EditorClipboardFailure.None, rangeCount, text.Length);
        }
        catch (Exception ex)
        {
            return new(true, false, EditorClipboardFailure.ClipboardUnavailable, rangeCount, text.Length, ex);
        }
    }
}

internal static class EditorClipboardText
{
    internal static string CombineSelections(TextDocument document, IReadOnlyList<string> selections)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selections);
        return string.Join(GetPreferredLineEnding(document), selections);
    }

    private static string GetPreferredLineEnding(TextDocument document)
    {
        for (var lineNumber = 1; lineNumber < document.LineCount; lineNumber++)
        {
            var line = document.GetLineByNumber(lineNumber);
            var delimiterLength = line.TotalLength - line.Length;
            if (delimiterLength > 0)
                return document.GetText(line.Offset + line.Length, delimiterLength);
        }

        return Environment.NewLine;
    }
}
