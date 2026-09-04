using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class ListArrayConversionTests
{
    [Fact]
    public void ListToArray_QuotesPlainValuesAndLeavesSupportedLiterals()
    {
        var document = new TextDocument("Server01\nSome Server\nC:\\Temp\n1\n-5\n3.14\n$true\n$false\n$null\nO'Brien\n'Already quoted'\n\"Also quoted\"");

        EditorTransformCommands.ConvertListToPowerShellArray(document, 0, document.TextLength, 4);

        Assert.Equal("@(\n    'Server01'\n    'Some Server'\n    'C:\\Temp'\n    1\n    -5\n    3.14\n    $true\n    $false\n    $null\n    'O''Brien'\n    'Already quoted'\n    \"Also quoted\"\n)", document.Text);
    }

    [Theory]
    [InlineData("one\ntwo\n", "@(\n    'one'\n    'two'\n)\n")]
    [InlineData("  one\n  two\n", "  @(\n      'one'\n      'two'\n  )\n")]
    public void ListToArray_PreservesBaseIndentAndTerminalLineEnding(string source, string expected)
    {
        var document = new TextDocument(source);
        EditorTransformCommands.ConvertListToPowerShellArray(document, 0, document.TextLength, 4);
        Assert.Equal(expected, document.Text);
    }

    [Fact]
    public void ListToArray_IgnoresBlankLinesAndIsOneUndoableAction()
    {
        var document = new TextDocument("A\r\n\r\nB\r\n");
        EditorTransformCommands.ConvertListToPowerShellArray(document, 0, document.TextLength, 2);

        Assert.Equal("@(\r\n  'A'\r\n  'B'\r\n)\r\n", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("A\r\n\r\nB\r\n", document.Text);
    }

    [Fact]
    public void ArrayToList_DecodesSupportedValuesAndOptionalTrailingCommas()
    {
        var document = new TextDocument("@(\r\n    'Server01',\r\n    'O''Brien'\r\n    \"Some Server\",\r\n    -5,\r\n    3.14,\r\n    $true,\r\n    $null\r\n)\r\n");

        EditorTransformCommands.ConvertPowerShellArrayToList(document, 0, document.TextLength);

        Assert.Equal("Server01\r\nO'Brien\r\nSome Server\r\n-5\r\n3.14\r\n$true\r\n$null\r\n", document.Text);
    }

    [Fact]
    public void ArrayToList_RejectsMalformedOrUnsupportedExpressionsWithoutChangingText()
    {
        foreach (var source in new[] { "@('one')", "@(\n    (Get-Date)\n)", "@(\n    @{ Name = 'A' }\n)" })
        {
            var document = new TextDocument(source);
            EditorTransformCommands.ConvertPowerShellArrayToList(document, 0, document.TextLength);
            Assert.Equal(source, document.Text);
        }
    }

    [Fact]
    public void ArrayToList_SupportsEmptyAndOneItemArraysAndUndo()
    {
        var document = new TextDocument("  @(\n    'Only'\n  )");
        EditorTransformCommands.ConvertPowerShellArrayToList(document, 0, document.TextLength);
        Assert.Equal("  Only", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("  @(\n    'Only'\n  )", document.Text);

        var empty = new TextDocument("@(\n)");
        EditorTransformCommands.ConvertPowerShellArrayToList(empty, 0, empty.TextLength);
        Assert.Equal(string.Empty, empty.Text);
    }

    [Fact]
    public void RoundTrip_PreservesNormalizedMixedListValues()
    {
        const string source = "Name\n42\n$true\nO'Brien\nSome Server\n";
        var document = new TextDocument(source);
        EditorTransformCommands.ConvertListToPowerShellArray(document, 0, document.TextLength);
        EditorTransformCommands.ConvertPowerShellArrayToList(document, 0, document.TextLength);
        Assert.Equal(source, document.Text);
    }

    [Fact]
    public void MenuAndCommandPalette_ExposeBothConversionsWithoutNewShortcut()
    {
        var menu = Read("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var code = Read("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Convert List to PowerShell Array", menu, StringComparison.Ordinal);
        Assert.Contains("Convert PowerShell Array to List", menu, StringComparison.Ordinal);
        Assert.Contains("transform.listToPowerShellArray", code, StringComparison.Ordinal);
        Assert.Contains("transform.powerShellArrayToList", code, StringComparison.Ordinal);
        Assert.Contains("EditorTransformCommands.ConvertListToPowerShellArray", code, StringComparison.Ordinal);
        Assert.Contains("EditorTransformCommands.ConvertPowerShellArrayToList", code, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return File.ReadAllText(Path.Combine(new[] { current.FullName }.Concat(parts).ToArray()));
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
