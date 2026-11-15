using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellParseCoordinatorTests
{
    [Fact]
    public async Task ConcurrentConsumersShareOneParseForTheSameSnapshot()
    {
        using var coordinator = new PowerShellParseCoordinator();
        var document = new TextDocument("function Get-Value {\n  42\n}");
        var snapshot = CreateSnapshot(document, Guid.NewGuid(), Guid.NewGuid(), 1);

        var foldingConsumer = coordinator.GetOrParseAsync(snapshot);
        var diagnosticsConsumer = coordinator.GetOrParseAsync(snapshot);
        var results = await Task.WhenAll(foldingConsumer, diagnosticsConsumer);

        Assert.Same(results[0], results[1]);
        Assert.True(results[0].IsFor(snapshot));
        Assert.Contains(results[0].FoldingRanges, range => range.Name == "function Get-Value");
        Assert.NotEmpty(results[0].SyntaxTokens);
        Assert.Equal(1, coordinator.ParserPassCount);
    }

    [Fact]
    public async Task NewRevisionProducesAResultBoundToItsOwnDocumentVersion()
    {
        using var coordinator = new PowerShellParseCoordinator();
        var sessionId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var document = new TextDocument("function Get-Value {\n  42\n}");
        var firstSnapshot = CreateSnapshot(document, sessionId, documentId, 1);
        var firstResult = await coordinator.GetOrParseAsync(firstSnapshot);

        document.Insert(0, "# header\n");
        var secondSnapshot = CreateSnapshot(document, sessionId, documentId, 2);
        var secondResult = await coordinator.GetOrParseAsync(secondSnapshot);

        Assert.True(firstResult.IsFor(firstSnapshot));
        Assert.True(secondResult.IsFor(secondSnapshot));
        Assert.False(firstResult.IsFor(secondSnapshot));
        Assert.Equal(2, coordinator.ParserPassCount);
        Assert.Contains(secondResult.FoldingRanges, range => range.StartOffset > 0);
    }

    [Fact]
    public async Task ParameterTokenSpansStayWholeAcrossTypingReplacementUndoAndRedo()
    {
        using var coordinator = new PowerShellParseCoordinator();
        var sessionId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var document = new TextDocument("Get-ChildItem -Path C:\\ -Recurse -");
        var revision = 0L;

        async Task AssertParameterSpanAsync(string expected)
        {
            var snapshot = CreateSnapshot(document, sessionId, documentId, ++revision);
            var result = await coordinator.GetOrParseAsync(snapshot);
            var token = Assert.Single(result.SyntaxTokens, token => token.Kind == "Parameter" && token.Text == expected);
            Assert.Equal(document.Text.IndexOf(expected, StringComparison.Ordinal), token.StartOffset);
            Assert.Equal(token.StartOffset + expected.Length, token.EndOffset);
            Assert.Equal(expected, document.Text.Substring(token.StartOffset, token.Length));
        }

        var typedParameter = "-";
        foreach (var character in "Force")
        {
            document.Insert(document.TextLength, character.ToString());
            typedParameter += character;
            await AssertParameterSpanAsync(typedParameter);
        }

        var forceOffset = document.Text.IndexOf("-Force", StringComparison.Ordinal);
        document.Replace(forceOffset, "-Force".Length, "-Force");
        await AssertParameterSpanAsync("-Force");

        document.Remove(forceOffset, "-Force".Length);
        var deletedSnapshot = CreateSnapshot(document, sessionId, documentId, ++revision);
        var deletedResult = await coordinator.GetOrParseAsync(deletedSnapshot);
        Assert.DoesNotContain(deletedResult.SyntaxTokens, token => token.Kind == "Parameter" && token.Text == "-Force");

        document.UndoStack.Undo();
        await AssertParameterSpanAsync("-Force");
        document.UndoStack.Redo();
        var redoneSnapshot = CreateSnapshot(document, sessionId, documentId, ++revision);
        var redoneResult = await coordinator.GetOrParseAsync(redoneSnapshot);
        Assert.DoesNotContain(redoneResult.SyntaxTokens, token => token.Kind == "Parameter" && token.Text == "-Force");
    }

    private static EditorDocumentSnapshot CreateSnapshot(TextDocument document, Guid sessionId, Guid documentId, long revision)
    {
        var source = document.CreateSnapshot();
        return new EditorDocumentSnapshot(
            sessionId, documentId, revision, source.Version, source, 1, CancellationToken.None);
    }
}
