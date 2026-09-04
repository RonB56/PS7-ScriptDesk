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

    [Fact]
    public void SortLinesByLength_IsStableAndPreservesDelimitersAndUndo()
    {
        var source = "bbb\r\na\r\ncc\r\nddd\r\nbb";
        var document = new TextDocument(source);

        EditorTransformCommands.SortLinesByLength(document, 0, document.TextLength);

        Assert.Equal("a\r\ncc\r\nbb\r\nbbb\r\nddd", document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void UniqueSortLinesKeepsFirstExactOccurrenceAndUsesOrdinalSortInOneUndo()
    {
        var source = "banana\nApple\nbanana\napple\nCherry\nApple";
        var document = new TextDocument(source);

        EditorTransformCommands.UniqueSortLines(document, 0, document.TextLength);

        Assert.Equal("Apple\nCherry\napple\nbanana", document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Theory]
    [InlineData("One\n\n\nTwo", "One\n\nTwo")]
    [InlineData("One\r\n   \r\n\r\nTwo\r\n", "One\r\n\r\nTwo\r\n")]
    public void CollapseConsecutiveBlankLinesKeepsOneEmptyBlankLine(string source, string expected)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.CollapseConsecutiveBlankLines(document, 0, document.TextLength);

        Assert.Equal(expected, document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void AddAndRemoveLineNumbersRoundTripWithWidthAndNumericSafety()
    {
        var source = "2026-09-03\n12345\n42 * $Value\n100MB\n1..10";
        var document = new TextDocument(source);

        EditorTransformCommands.AddLineNumbers(document, 0, document.TextLength);
        Assert.Equal("1. 2026-09-03\n2. 12345\n3. 42 * $Value\n4. 100MB\n5. 1..10", document.Text);
        EditorTransformCommands.RemoveLineNumbers(document, 0, document.TextLength);
        Assert.Equal(source, document.Text);
        document.UndoStack.Undo();
        Assert.Equal("1. 2026-09-03\n2. 12345\n3. 42 * $Value\n4. 100MB\n5. 1..10", document.Text);
    }

    [Fact]
    public void AddLineNumbersIsIdempotentForItsOwnConsistentNumbering()
    {
        var document = new TextDocument("1. First\n2. Second");

        EditorTransformCommands.AddLineNumbers(document, 0, document.TextLength);

        Assert.Equal("1. First\n2. Second", document.Text);
    }

    [Fact]
    public void RemoveLineNumbersLeavesNonMatchingNumericContentUnchanged()
    {
        var document = new TextDocument("2026-09-03\n12345\n42 * $Value\n100MB\n1..10");

        EditorTransformCommands.RemoveLineNumbers(document, 0, document.TextLength);

        Assert.Equal("2026-09-03\n12345\n42 * $Value\n100MB\n1..10", document.Text);
    }

    [Theory]
    [InlineData("a\nb\n", "a\r\nb\r\n")]
    [InlineData("a\r\nb\n\r\nc", "a\r\nb\r\n\r\nc")]
    [InlineData("a\nb", "a\r\nb")]
    public void ConvertLineEndingsToCrlfPreservesTerminationAndUndo(string source, string expected)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.ConvertLineEndingsToCrlf(document);

        Assert.Equal(expected, document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\r\nb\nc", "a\nb\nc")]
    public void ConvertLineEndingsToLfPreservesTerminationAndUndo(string source, string expected)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.ConvertLineEndingsToLf(document);

        Assert.Equal(expected, document.Text);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void UrlEncodeDecode_RoundTripSelectedUnicodeTextAndUndo()
    {
        const string source = "hello world + % & = ? # / : café 🚀";
        var document = new TextDocument(source);

        EditorTransformCommands.UrlEncode(document, 0, document.TextLength);
        Assert.Contains("hello+world", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", document.Text);
        EditorTransformCommands.UrlDecode(document, 0, document.TextLength);
        Assert.Equal(source, document.Text);
        document.UndoStack.Undo();
        Assert.Contains("hello+world", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlDecode_MalformedEscapesRemainUnchanged()
    {
        const string source = "bad%ZZ%";
        var document = new TextDocument(source);

        EditorTransformCommands.UrlDecode(document, 0, document.TextLength);

        Assert.Equal(source, document.Text);
    }

    [Theory]
    [InlineData("hello", "aGVsbG8=")]
    [InlineData("café 🚀", "Y2Fmw6kg8J+agA==")]
    [InlineData("a\r\nb", "YQ0KYg==")]
    public void Base64Encode_UsesUtf8AndPreservesExactSelectedText(string source, string expected)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.Base64Encode(document, 0, document.TextLength);

        Assert.Equal(expected, document.Text);
    }

    [Fact]
    public void Base64Decode_RoundTripsUnicodeAndUndo()
    {
        const string source = "PowerShell café 🚀\nGet-Process";
        var document = new TextDocument(source);
        EditorTransformCommands.Base64Encode(document, 0, document.TextLength);
        var encoded = document.Text;

        EditorTransformCommands.Base64Decode(document, 0, document.TextLength);

        Assert.Equal(source, document.Text);
        document.UndoStack.Undo();
        Assert.Equal(encoded, document.Text);
    }

    [Theory]
    [InlineData("not base64!!!")]
    [InlineData("%%%%")]
    [InlineData("abcde")]
    public void Base64Decode_InvalidInputDoesNotChangeDocument(string source)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.Base64Decode(document, 0, document.TextLength);

        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void JsonPrettyPrint_PreservesStructureStringsNumbersAndUndo()
    {
        const string source = "{\"name\":\"hello   world\",\"n\":12345678901234567890,\"items\":[1,true,null]}";
        var document = new TextDocument(source);

        EditorTransformCommands.JsonPrettyPrint(document, 0, document.TextLength);

        Assert.Contains("  \"name\": \"hello   world\",", document.Text, StringComparison.Ordinal);
        Assert.Contains("12345678901234567890", document.Text, StringComparison.Ordinal);
        Assert.Contains("\n  \"items\": [", document.Text, StringComparison.Ordinal);
        document.UndoStack.Undo();
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void JsonMinify_RemovesFormattingButPreservesStringWhitespace()
    {
        var document = new TextDocument("{\n  \"text\": \"hello   world\",\n  \"items\": [1, 2]\n}");

        EditorTransformCommands.JsonMinify(document, 0, document.TextLength);

        Assert.Equal("{\"text\":\"hello   world\",\"items\":[1,2]}", document.Text);
    }

    [Theory]
    [InlineData("{\"name\":}")]
    [InlineData("{ name: \"Ron\" }")]
    [InlineData("{\"a\":1,}")]
    public void JsonTransforms_InvalidInputDoesNotChangeDocument(string source)
    {
        var document = new TextDocument(source);

        EditorTransformCommands.JsonPrettyPrint(document, 0, document.TextLength);
        EditorTransformCommands.JsonMinify(document, 0, document.TextLength);

        Assert.Equal(source, document.Text);
    }
}
