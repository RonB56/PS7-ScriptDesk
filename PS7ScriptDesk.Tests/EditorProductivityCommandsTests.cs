using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorProductivityCommandsTests
{
    [Theory]
    [InlineData("if ($Enabled) {\r\n    Get-Service\r\n    Get-Process\r\n}", "if ($Enabled) {\r\n    # Get-Service\r\n    # Get-Process\r\n}")]
    [InlineData("one\ntwo\n", "# one\n# two\n")]
    public void ToggleComment_PreservesIndentationAndLineEndings(string source, string expected)
    {
        var document = new TextDocument(source);
        var start = document.Text.IndexOf("Get-Service", StringComparison.Ordinal);
        var end = start >= 0
            ? document.Text.IndexOf("Get-Process", start, StringComparison.Ordinal) + "Get-Process".Length
            : document.TextLength;
        EditorProductivityCommands.ToggleComment(document, start, end - start);
        Assert.Equal(expected, document.Text);
        var transformedStart = source.Contains("Get-Service", StringComparison.Ordinal)
            ? document.Text.IndexOf("Get-Service", StringComparison.Ordinal)
            : 0;
        var transformedEnd = source.Contains("Get-Process", StringComparison.Ordinal)
            ? document.Text.IndexOf("Get-Process", StringComparison.Ordinal) + "Get-Process".Length
            : document.TextLength;
        EditorProductivityCommands.ToggleComment(document, transformedStart, transformedEnd - transformedStart);
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void ToggleComment_BlankAndMixedLinesCommentWithoutChangingBlankLine()
    {
        var document = new TextDocument("  one\n\n# two");
        EditorProductivityCommands.ToggleComment(document, 0, document.TextLength);
        Assert.Equal("  # one\n\n# # two", document.Text);
    }

    [Fact]
    public void IndentAndOutdent_GroupUndoAndDoNotUnderflow()
    {
        var document = new TextDocument("  one\n\ttwo\nthree");
        EditorProductivityCommands.Indent(document, 0, document.TextLength, 4);
        Assert.Equal("      one\n    \ttwo\n    three", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("  one\n\ttwo\nthree", document.Text);
        EditorProductivityCommands.Outdent(document, 0, document.TextLength, 4);
        Assert.Equal("one\ntwo\nthree", document.Text);
    }

    [Fact]
    public void MoveLines_SwapsWholeBlocksAtBoundaries()
    {
        var document = new TextDocument("one\ntwo\nthree\n");
        EditorProductivityCommands.MoveLines(document, 4, 4, -1);
        Assert.Equal("two\none\nthree\n", document.Text);
        EditorProductivityCommands.MoveLines(document, 0, 8, 1);
        Assert.Equal("three\ntwo\none\n", document.Text);
    }

    [Fact]
    public void DuplicateLines_WorksForFinalLineWithoutNewline()
    {
        var document = new TextDocument("one\ntwo");
        EditorProductivityCommands.DuplicateLines(document, 4, 0, 1);
        Assert.Equal("one\ntwo\ntwo", document.Text);
    }

    [Fact]
    public void DeleteLines_HandlesFinalLineAndUndo()
    {
        var document = new TextDocument("one\ntwo\nthree");
        EditorProductivityCommands.DeleteLines(document, 8, 0);
        Assert.Equal("one\ntwo", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("one\ntwo\nthree", document.Text);
    }

    [Theory]
    [InlineData('(', ')')]
    [InlineData('[', ']')]
    [InlineData('{', '}')]
    [InlineData('"', '"')]
    [InlineData('\'', '\'')]
    public void SurroundSelection_WrapsTextAsOneUndoableEdit(char opener, char closer)
    {
        var document = new TextDocument("$Name");
        EditorProductivityCommands.SurroundSelection(document, 0, document.TextLength, opener, closer);
        Assert.Equal($"{opener}$Name{closer}", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("$Name", document.Text);
    }
}
