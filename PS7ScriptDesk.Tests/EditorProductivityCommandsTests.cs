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

    [Theory]
    [InlineData("if ($Enabled) {\n    Get-Service\n    Get-Process\n    Get-ChildItem\n}", "\n")]
    [InlineData("if ($Enabled) {\r\n    Get-Service\r\n    Get-Process\r\n    Get-ChildItem\r\n}\r\n", "\r\n")]
    public void IndentSelection_RestoresExactlyTheSameLogicalBlock(string source, string lineEnding)
    {
        var document = new TextDocument(source);
        var start = document.Text.IndexOf("    Get-Service", StringComparison.Ordinal);
        var end = document.Text.IndexOf("    Get-ChildItem", start, StringComparison.Ordinal) + "    Get-ChildItem".Length;

        var indented = EditorProductivityCommands.Indent(document, start, end - start, 4);
        Assert.Equal(start, indented.SelectionStart);
        Assert.Equal(end - start + 12, indented.SelectionLength);
        Assert.StartsWith("        Get-Service", document.GetText(document.GetLineByNumber(2).Offset, document.GetLineByNumber(2).Length), StringComparison.Ordinal);
        Assert.DoesNotContain("}" + lineEnding, document.GetText(indented.SelectionStart, indented.SelectionLength), StringComparison.Ordinal);

        var indentedAgain = EditorProductivityCommands.Indent(document, indented.SelectionStart, indented.SelectionLength, 4);
        Assert.Equal(indented.SelectionStart, indentedAgain.SelectionStart);
        Assert.Equal(indented.SelectionLength + 12, indentedAgain.SelectionLength);
        Assert.DoesNotContain("}" + lineEnding, document.GetText(indentedAgain.SelectionStart, indentedAgain.SelectionLength), StringComparison.Ordinal);
    }

    [Fact]
    public void IndentSelection_EndingAtNextLineOffset_DoesNotSelectNextLine()
    {
        var document = new TextDocument("one\ntwo\nthree\n}");
        var start = document.Text.IndexOf("two", StringComparison.Ordinal);
        var end = document.Text.IndexOf("three", StringComparison.Ordinal);

        var result = EditorProductivityCommands.Indent(document, start, end - start, 4);

        Assert.Equal("    two", document.GetText(result.SelectionStart, result.SelectionLength));
    }

    [Fact]
    public void IndentThenOutdent_ReusesExactBlockWithoutDrift()
    {
        var document = new TextDocument("{\r\n    one\r\n    two\r\n    three\r\n}\r\n");
        var start = document.Text.IndexOf("    one", StringComparison.Ordinal);
        var end = document.Text.IndexOf("    three", StringComparison.Ordinal) + "    three".Length;

        var indented = EditorProductivityCommands.Indent(document, start, end - start, 4);
        var outdented = EditorProductivityCommands.Outdent(document, indented.SelectionStart, indented.SelectionLength, 4);

        Assert.Equal("    one\r\n    two\r\n    three", document.GetText(outdented.SelectionStart, outdented.SelectionLength));
        Assert.Equal(end - start, outdented.SelectionLength);
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

    [Theory]
    [InlineData("A\nB\nC\nD\n", "\n")]
    [InlineData("A\r\nB\r\nC\r\nD\r\n", "\r\n")]
    public void MoveLines_MultiLineBlockPreservesExactDocumentText(string source, string lineEnding)
    {
        var document = new TextDocument(source);
        var start = document.Text.IndexOf("B", StringComparison.Ordinal);
        var end = document.Text.IndexOf("C", StringComparison.Ordinal) + 1;

        var down = EditorProductivityCommands.MoveLines(document, start, end - start, 1);
        Assert.Equal($"A{lineEnding}D{lineEnding}B{lineEnding}C{lineEnding}", document.Text);
        Assert.Equal($"B{lineEnding}C", document.GetText(down.SelectionStart, down.SelectionLength));

        var up = EditorProductivityCommands.MoveLines(document, down.SelectionStart, down.SelectionLength, -1);
        Assert.Equal(source, document.Text);
        Assert.Equal($"B{lineEnding}C", document.GetText(up.SelectionStart, up.SelectionLength));
    }

    [Fact]
    public void MoveLines_ExactFirstTwoSelectedLinesDownMutatesProductionDocument()
    {
        const string source = "Get-Service\nGet-Process\nGet-ChildItem\nWrite-Host \"Done\"";
        var document = new TextDocument(source);
        var start = 0;
        var end = source.IndexOf("Get-ChildItem", StringComparison.Ordinal);

        var result = EditorProductivityCommands.MoveLines(document, start, end - start, 1);

        Assert.NotEqual(source, document.Text);
        Assert.Equal("Get-ChildItem\nGet-Service\nGet-Process\nWrite-Host \"Done\"", document.Text);
        Assert.Equal("Get-Service\nGet-Process\n", document.GetText(result.SelectionStart, result.SelectionLength));
    }

    [Fact]
    public void MoveLines_ThreeLineBlockDownAndUpDoesNotConcatenateNeighbors()
    {
        var source = "Get-Service\nGet-Process\nGet-ChildItem\nWrite-Host \"Done\"";
        var document = new TextDocument(source);
        var start = 0;
        var end = document.Text.IndexOf("Get-ChildItem", StringComparison.Ordinal) + "Get-ChildItem".Length;

        var down = EditorProductivityCommands.MoveLines(document, start, end, 1);
        Assert.Equal("Write-Host \"Done\"\nGet-Service\nGet-Process\nGet-ChildItem", document.Text);
        Assert.Equal("Get-Service\nGet-Process\nGet-ChildItem", document.GetText(down.SelectionStart, down.SelectionLength));

        EditorProductivityCommands.MoveLines(document, down.SelectionStart, down.SelectionLength, -1);
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void MoveLines_FinalUnterminatedNeighborRemainsSeparated()
    {
        var document = new TextDocument("A\nB\nC");
        var start = document.Text.IndexOf("A", StringComparison.Ordinal);

        var result = EditorProductivityCommands.MoveLines(document, start, 3, 1);

        Assert.Equal("C\nA\nB", document.Text);
        Assert.Equal("A\nB", document.GetText(result.SelectionStart, result.SelectionLength));
    }

    [Fact]
    public void MoveLines_UndoRestoresExactOriginalAndRedoRestoresMove()
    {
        const string source = "A\r\nB\r\nC\r\nD";
        var document = new TextDocument(source);
        var start = source.IndexOf("B", StringComparison.Ordinal);
        var end = source.IndexOf("C", StringComparison.Ordinal) + 1;

        EditorProductivityCommands.MoveLines(document, start, end - start, 1);
        Assert.Equal("A\r\nD\r\nB\r\nC", document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
        document.UndoStack.Redo();
        Assert.Equal("A\r\nD\r\nB\r\nC", document.Text);
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

    [Theory]
    [InlineData("one\ntwo\nthree", 4, "one\n\ntwo\nthree", 4)]
    [InlineData("one\r\ntwo", 0, "\r\none\r\ntwo", 0)]
    [InlineData("one\ntwo", 4, "one\n\ntwo", 4)]
    public void InsertLineAbove_PreservesLineEndingsCaretAndUndo(string source, int caret, string expected, int expectedCaret)
    {
        var document = new TextDocument(source);
        var result = EditorProductivityCommands.InsertLineAbove(document, caret);

        Assert.Equal(expected, document.Text);
        Assert.Equal(expectedCaret, result.SelectionStart);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void InsertLineBelow_HandlesFinalUnterminatedLine()
    {
        var document = new TextDocument("one\ntwo");
        var result = EditorProductivityCommands.InsertLineBelow(document, document.TextLength);

        Assert.Equal("one\ntwo\n", document.Text);
        Assert.Equal(document.TextLength, result.SelectionStart);
        document.UndoStack.Undo();
        Assert.Equal("one\ntwo", document.Text);
    }

    [Fact]
    public void DeleteToLineStartAndEnd_DoNotDeleteLineDelimitersAndRejectSelections()
    {
        var startDocument = new TextDocument("alpha\r\nbeta\r\ngamma");
        var startResult = EditorProductivityCommands.DeleteToLineStart(startDocument, 9, 0);
        Assert.Equal("alpha\r\nta\r\ngamma", startDocument.Text);
        Assert.Equal(7, startResult.SelectionStart);
        startDocument.UndoStack.Undo();
        Assert.Equal("alpha\r\nbeta\r\ngamma", startDocument.Text);

        var endDocument = new TextDocument("alpha\r\nbeta\r\ngamma");
        var endResult = EditorProductivityCommands.DeleteToLineEnd(endDocument, 9, 0);
        Assert.Equal("alpha\r\nbe\r\ngamma", endDocument.Text);
        Assert.Equal(9, endResult.SelectionStart);
        var unchanged = EditorProductivityCommands.DeleteToLineEnd(endDocument, 0, 2);
        Assert.Equal("alpha\r\nbe\r\ngamma", endDocument.Text);
        Assert.Equal(2, unchanged.SelectionLength);
    }

    [Fact]
    public void DuplicateSelection_DuplicatesExactCharactersAndUndoRestores()
    {
        var document = new TextDocument("Get-Process\r\n$Name");
        var start = document.Text.IndexOf("Process", StringComparison.Ordinal);
        var result = EditorProductivityCommands.DuplicateSelection(document, start, "Process".Length);

        Assert.Equal("Get-ProcessProcess\r\n$Name", document.Text);
        Assert.Equal(start + "Process".Length, result.SelectionStart);
        Assert.Equal("Process", document.GetText(result.SelectionStart, result.SelectionLength));
        document.UndoStack.Undo();
        Assert.Equal("Get-Process\r\n$Name", document.Text);
    }
}
