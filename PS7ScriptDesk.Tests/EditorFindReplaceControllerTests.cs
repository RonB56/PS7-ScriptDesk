using System.Windows;
using ICSharpCode.AvalonEdit;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class EditorFindReplaceControllerTests
{
    [Fact]
    public void LiteralAndRegexSearchResultsPreserveMatchingOptions()
    {
        RunOnStaThread(() =>
        {
            var editor = new TextEditor { Text = "Cat cat c?t" };
            var controller = CreateController(editor);

            Assert.Equal(new[] { 0, 4 }, controller.GetFindResults("cat", matchCase: false).Select(result => result.Offset));
            Assert.Equal(new[] { 4 }, controller.GetFindResults("cat", matchCase: true).Select(result => result.Offset));
            Assert.Equal(new[] { 0, 4, 8 }, controller.GetFindResults("c.t", matchCase: false, useRegex: true).Select(result => result.Offset));
        });
    }

    [Fact]
    public void ForwardAndBackwardSearchWrapAtDocumentBoundaries()
    {
        RunOnStaThread(() =>
        {
            var editor = new TextEditor { Text = "a b a" };
            var controller = CreateController(editor);

            editor.CaretOffset = editor.Text.Length;
            controller.ExecuteFindNext("a", matchCase: true);
            Assert.Equal(0, editor.SelectionStart);

            editor.CaretOffset = 0;
            controller.RepeatFind(backwards: true);
            Assert.Equal(4, editor.SelectionStart);
        });
    }

    [Fact]
    public void ReplacementKeepsLiteralTextAndRegexCaptureSemantics()
    {
        RunOnStaThread(() =>
        {
            var editor = new TextEditor { Text = "a a" };
            var controller = CreateController(editor);

            controller.ExecuteReplaceAll("a", "$1", matchCase: true);
            Assert.Equal("$1 $1", editor.Text);

            editor.Text = "left-right";
            controller.ExecuteReplaceAll("(\\w+)-(\\w+)", "$2:$1", matchCase: true, useRegex: true);
            Assert.Equal("right:left", editor.Text);
        });
    }

    private static EditorFindReplaceController CreateController(TextEditor editor) =>
        new(new Window(), () => editor, _ => { });

    private static void RunOnStaThread(Action action)
        => WpfTestDispatcher.Run(action);
}
