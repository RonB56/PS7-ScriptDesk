using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class EditorMultiCaretClipboardTests
{
    [Fact]
    public void ClipboardPolicyIsMultiTargetAndDeferredCommandsAreIdentified()
    {
        Assert.Equal(EditorMultiCaretPolicy.MultiTarget, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.Copy));
        Assert.Equal(EditorMultiCaretPolicy.MultiTarget, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.Cut));
        Assert.Equal(EditorMultiCaretPolicy.MultiTarget, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.Paste));
        Assert.Equal(EditorMultiCaretPolicy.NativeSelectionAndNormalize, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.SelectAll));
        Assert.Equal(EditorMultiCaretPolicy.NativeUndoRedo, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.Undo));
        Assert.Equal(EditorMultiCaretPolicy.ClearSecondaryCarets, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.Escape));
        Assert.Equal(EditorMultiCaretPolicy.SuppressWhileMultipleCaretsAreActive, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.IntelliSense));
        Assert.Equal(EditorMultiCaretPolicy.DeferredPrimaryOnlyWithCleanup, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.RunSelection));
        Assert.Equal(EditorMultiCaretPolicy.DeferredPrimaryOnlyWithCleanup, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.CommentIndentTransforms));
        Assert.Equal(EditorMultiCaretPolicy.DeferredPrimaryOnlyWithCleanup, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.DuplicateMoveDeleteLines));
        Assert.Equal(EditorMultiCaretPolicy.DeferredPrimaryOnlyWithCleanup, EditorMultiCaretPolicyResolver.GetPolicy(EditorMultiCaretCommand.SearchReplace));
    }

    [Fact]
    public void SingleCaretClipboardKeepsSelectedTextAndNativeReplacementBehavior()
    {
        WpfTestDispatcher.Run(() =>
        {
            var clipboard = new FakeClipboard();
            var service = new EditorClipboardService(clipboard);
            var editor = new TextEditor { Text = "  value \t tail" };
            editor.Select(0, 8);

            var copy = service.Copy(editor);
            Assert.True(copy.Succeeded);
            Assert.Equal("  value ", clipboard.Text);

            var cut = service.Cut(editor, ReplaceSelection);
            Assert.True(cut.Succeeded);
            Assert.Equal("\t tail", editor.Text);

            clipboard.Text = "Ω\r\nnext";
            editor.Select(0, 0);
            var paste = service.Paste(editor, ReplaceSelection);
            Assert.True(paste.Succeeded);
            Assert.Equal("Ω\r\nnext\t tail", editor.Text);
        });
    }

    [Fact]
    public void MultiCaretPasteBroadcastsWholePayloadAndIsOneUndoStep()
    {
        WpfTestDispatcher.Run(() =>
        {
            var clipboard = new FakeClipboard { Text = "Ω\r\nline\t " };
            var service = new EditorClipboardService(clipboard);
            var editor = new TextEditor { Text = "a---b---c" };
            Assert.True(editor.TextArea.AddCaretAtOffset(4));
            Assert.True(editor.TextArea.AddCaretAtOffset(8));

            var paste = service.Paste(editor, ReplaceSelection);
            const string expected = "Ω\r\nline\t a---Ω\r\nline\t b---Ω\r\nline\t c";
            Assert.True(paste.Succeeded);
            Assert.Equal(expected, editor.Text);

            editor.Document.UndoStack.Undo();
            Assert.Equal("a---b---c", editor.Text);
            editor.Document.UndoStack.Redo();
            Assert.Equal(expected, editor.Text);

            var beforeRepeat = editor.Text;
            var repeatPaste = service.Paste(editor, ReplaceSelection);
            Assert.True(repeatPaste.Succeeded);
            var afterRepeat = editor.Text;
            Assert.Equal(beforeRepeat.Length + clipboard.Text.Length * 3, afterRepeat.Length);
            editor.Document.UndoStack.Undo();
            Assert.Equal(beforeRepeat, editor.Text);
            editor.Document.UndoStack.Redo();
            Assert.Equal(afterRepeat, editor.Text);

            Assert.True(editor.TextArea.AddCaretAtOffset(1));
            Assert.Equal(4, editor.TextArea.MultiCaretCount);
            editor.TextArea.ClearSecondaryCarets();
            Assert.Equal(1, editor.TextArea.MultiCaretCount);
        });
    }

    [Fact]
    public void MultiCaretClipboardFailuresAndReadOnlyPasteDoNotMutateDocument()
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new TextEditor { Text = "a---b---c" };
            Assert.True(editor.TextArea.AddCaretAtOffset(4));
            Assert.True(editor.TextArea.AddCaretAtOffset(8));
            var original = editor.Text;

            var failedClipboard = new FakeClipboard { ThrowOnGet = true, ThrowOnSet = true, Text = "replacement" };
            var failedService = new EditorClipboardService(failedClipboard);
            var pasteFailure = failedService.Paste(editor, ReplaceSelection);
            Assert.Equal(EditorClipboardFailure.ClipboardUnavailable, pasteFailure.Failure);
            Assert.Equal(original, editor.Text);

            editor.CaretOffset = 4;
            editor.Select(4, 1);
            var cutFailure = failedService.Cut(editor, ReplaceSelection);
            Assert.Equal(EditorClipboardFailure.ClipboardUnavailable, cutFailure.Failure);
            Assert.Equal(original, editor.Text);

            var readOnlyClipboard = new FakeClipboard { Text = "new" };
            editor.IsReadOnly = true;
            var readOnlyResult = new EditorClipboardService(readOnlyClipboard).Paste(editor, ReplaceSelection);
            Assert.Equal(EditorClipboardFailure.ReadOnly, readOnlyResult.Failure);
            Assert.Equal(original, editor.Text);
        });
    }

    [Fact]
    public void EmptyMultiCaretClipboardIsConsumedWithoutDeletingSelections()
    {
        WpfTestDispatcher.Run(() =>
        {
            var clipboard = new FakeClipboard { Text = string.Empty };
            var editor = new TextEditor { Text = "abc---def" };
            Assert.True(editor.TextArea.AddCaretAtOffset(6));
            editor.Select(0, 3);
            editor.Document.UndoStack.ClearAll();

            var result = new EditorClipboardService(clipboard).Paste(editor, ReplaceSelection);
            Assert.True(result.Handled);
            Assert.False(result.Succeeded);
            Assert.Equal(EditorClipboardFailure.EmptyClipboard, result.Failure);
            Assert.Equal("abc---def", editor.Text);
            Assert.False(editor.Document.UndoStack.CanUndo);
        });
    }

    [Fact]
    public void MultiSelectionCopyJoinUsesPreferredDocumentNewlineAndPreservesWhitespace()
    {
        var document = new TextDocument("first\r\nsecond\nthird");
        var copied = EditorClipboardText.CombineSelections(document, new[] { "  first\t", " second " });

        Assert.Equal("  first\t\r\n second ", copied);
    }

    [Fact]
    public void MultiCaretCopyCutDoesNotMutateEmptySecondaryCarets()
    {
        WpfTestDispatcher.Run(() =>
        {
            var clipboard = new FakeClipboard();
            var editor = new TextEditor { Text = "aa--bb--cc" };
            Assert.True(editor.TextArea.AddCaretAtOffset(6));
            Assert.True(editor.TextArea.AddCaretAtOffset(10));
            editor.Select(0, 2);

            var service = new EditorClipboardService(clipboard);
            var copy = service.Copy(editor);

            Assert.True(copy.Succeeded);
            Assert.Equal("aa", clipboard.Text);

            var cut = service.Cut(editor, ReplaceSelection);

            Assert.True(cut.Succeeded);
            Assert.Equal("aa", clipboard.Text);
            Assert.Equal("--bb--cc", editor.Text);
            editor.Document.UndoStack.Undo();
            Assert.Equal("aa--bb--cc", editor.Text);
        });
    }

    [Fact]
    public void CtrlAThenPasteUsesOneNativeSelection()
    {
        WpfTestDispatcher.Run(() =>
        {
            var clipboard = new FakeClipboard { Text = "replacement" };
            var editor = new TextEditor { Text = "whole document" };
            Assert.True(editor.TextArea.AddCaretAtOffset(6));

            editor.SelectAll();
            Assert.Equal(1, editor.TextArea.MultiCaretCount);
            var paste = new EditorClipboardService(clipboard).Paste(editor, ReplaceSelection);

            Assert.True(paste.Succeeded);
            Assert.Equal("replacement", editor.Text);
        });
    }

    [Fact]
    public void EscapeCleanupRouteRemainsPresentForMultiCaretEditor()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        Assert.Contains("key == Key.Escape", source, StringComparison.Ordinal);
        Assert.Contains("editorTextEditor.TextArea.ClearSecondaryCarets()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyboardAndEditorContextClipboardHandlersShareTheSameMethods()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");

        Assert.Contains("TryCopySelectedEditorTextToClipboard(editorTextEditor)", source);
        Assert.Contains("TryCutSelectedEditorTextToClipboard(editorTextEditor)", source);
        Assert.Contains("TryPasteClipboardTextIntoEditor(editorTextEditor)", source);
        Assert.Contains("_editorClipboardService.Copy(editorTextEditor)", source);
        Assert.Contains("_editorClipboardService.Cut(editorTextEditor, ReplaceEditorSelection)", source);
        Assert.Contains("_editorClipboardService.Paste(editorTextEditor, ReplaceEditorSelection)", source);
        Assert.Contains("Click=\"EditorCut_Click\"", xaml);
        Assert.Contains("Click=\"EditorCopy_Click\"", xaml);
        Assert.Contains("Click=\"EditorPaste_Click\"", xaml);
        Assert.DoesNotContain("Copy Selected Text", source);

        var keyboardPasteStart = source.IndexOf("if (modifiers == ModifierKeys.Control && key == Key.V)", StringComparison.Ordinal);
        Assert.True(keyboardPasteStart >= 0);
        Assert.Contains("TryPasteClipboardTextIntoEditor(editorTextEditor)", source[keyboardPasteStart..]);

        var contextPasteStart = source.IndexOf("private void EditorPaste_Click", StringComparison.Ordinal);
        var contextPasteEnd = source.IndexOf("private static TextEditor? ResolveEditorForContextMenuCommand", contextPasteStart, StringComparison.Ordinal);
        Assert.True(contextPasteStart >= 0 && contextPasteEnd > contextPasteStart);
        var contextPaste = source[contextPasteStart..contextPasteEnd];
        Assert.Contains("ResolveEditorForContextMenuCommand(sender)", contextPaste);
        Assert.Contains("TryPasteClipboardTextIntoEditor(editorTextEditor)", contextPaste);
        Assert.Contains("placementTargetResolved", contextPaste);
    }

    private static void ReplaceSelection(TextEditor editor, string replacement)
    {
        var start = editor.SelectionStart;
        editor.Document.Replace(start, editor.SelectionLength, replacement);
        var caret = Math.Clamp(start + replacement.Length, 0, editor.Document.TextLength);
        editor.Select(caret, 0);
        editor.CaretOffset = caret;
    }

    private sealed class FakeClipboard : IEditorClipboard
    {
        internal string Text { get; set; } = string.Empty;
        internal bool ThrowOnGet { get; set; }
        internal bool ThrowOnSet { get; set; }

        public bool ContainsText()
        {
            if (ThrowOnGet) throw new InvalidOperationException("clipboard unavailable");
            return Text.Length > 0;
        }

        public string GetText()
        {
            if (ThrowOnGet) throw new InvalidOperationException("clipboard unavailable");
            return Text;
        }

        public void SetText(string text)
        {
            if (ThrowOnSet) throw new InvalidOperationException("clipboard unavailable");
            Text = text;
        }
    }
}
