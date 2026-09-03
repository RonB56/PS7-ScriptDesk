using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorTransformCommandsTests
{
    [Theory]
    [InlineData("Zebra\nApple\nMango\n", "Apple\nMango\nZebra\n")]
    [InlineData("Zebra\r\nApple\r\nMango", "Apple\r\nMango\r\nZebra")]
    public void SortLinesAscending_PreservesFullDocumentAndLineEndings(string source, string expected)
    {
        var document = new TextDocument(source);
        var result = EditorTransformCommands.SortLinesAscending(document, 0, document.TextLength);
        Assert.Equal(expected, document.Text);
        Assert.Equal(expected, document.GetText(result.SelectionStart, result.SelectionLength));
    }

    [Fact]
    public void CoreLineTransformsPreserveFirstOccurrenceAndDoNotDuplicateText()
    {
        var document = new TextDocument("Alpha\nBeta\nAlpha\nGamma\nBeta\n");
        EditorTransformCommands.RemoveDuplicateLines(document, 0, document.TextLength);
        Assert.Equal("Alpha\nBeta\nGamma\n", document.Text);
        EditorTransformCommands.ReverseLines(document, 0, document.TextLength);
        Assert.Equal("Gamma\nBeta\nAlpha\n", document.Text);
    }

    [Fact]
    public void WhitespaceCasePrefixSuffixQuoteAndCommaTransformsAreExactAndUndoable()
    {
        var document = new TextDocument("  Alpha  \nBeta\t\n");
        EditorTransformCommands.TrimLines(document, 0, document.TextLength);
        Assert.Equal("Alpha\nBeta\n", document.Text);
        EditorTransformCommands.PrefixLines(document, 0, document.TextLength, "# ");
        EditorTransformCommands.SuffixLines(document, 0, document.TextLength, ",");
        Assert.Equal("# Alpha,\n# Beta,\n", document.Text);
        EditorTransformCommands.QuoteLines(document, 0, document.TextLength, '\'');
        Assert.Equal("'# Alpha,'\n'# Beta,'\n", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("# Alpha,\n# Beta,\n", document.Text);
    }

    [Fact]
    public void CharacterTransformsRequireSelectionAndUseInvariantCaseRules()
    {
        var document = new TextDocument("hello WORLD");
        Assert.Equal(0, EditorTransformCommands.UppercaseSelection(document, 0, 0).SelectionLength);
        EditorTransformCommands.TitleCaseSelection(document, 0, document.TextLength);
        Assert.Equal("Hello WORLD", document.Text);
        EditorTransformCommands.LowercaseSelection(document, 0, document.TextLength);
        Assert.Equal("hello world", document.Text);
    }

    [Fact]
    public void RemoveBlankLinesTreatsWhitespaceOnlyLinesAsBlank()
    {
        var document = new TextDocument("A\n   \n\nB\n");
        EditorTransformCommands.RemoveBlankLines(document, 0, document.TextLength);
        Assert.Equal("A\nB\n", document.Text);
    }

    [Fact]
    public void TrimDocumentTrailingWhitespace_PreservesDelimitersAndOneUndo()
    {
        var source = " one \r\n two\t\nthree";
        var document = new TextDocument(source);

        EditorTransformCommands.TrimDocumentTrailingWhitespace(document);

        Assert.Equal(" one\r\n two\nthree", document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void IgnoreCaseSort_IsStableAndPreservesLineEndings()
    {
        var document = new TextDocument("b\r\nA\r\na\r\nB");

        EditorTransformCommands.SortLinesIgnoreCaseAscending(document, 0, document.TextLength);
        Assert.Equal("A\r\na\r\nb\r\nB", document.Text);
        document.UndoStack.Undo();
        EditorTransformCommands.SortLinesIgnoreCaseDescending(document, 0, document.TextLength);
        Assert.Equal("b\r\nB\r\nA\r\na", document.Text);
    }

    [Theory]
    [InlineData("Get-Process\nWhere-Object {$_.CPU -gt 10}\nSelect-Object Name,CPU", "Get-Process Where-Object {$_.CPU -gt 10} Select-Object Name,CPU")]
    [InlineData("one\r\n\r\n  two\r\n", "one two\r\n")]
    public void JoinLines_TrimsOnlyBoundariesSkipsBlankLinesAndPreservesLastDelimiter(string source, string expected)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.JoinLines(document, 0, document.TextLength);

        Assert.Equal(expected, document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }
}
