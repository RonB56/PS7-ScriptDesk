using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Tests;

public sealed class IntelliSenseCompletionContractTests
{
    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('\n')]
    public void WhitespaceDismissesWithoutBeingACommitCharacter(char input)
    {
        Assert.True(MainWindow.ShouldDismissCompletionForTextInput(input));
        Assert.False(MainWindow.ShouldCommitCompletionForTextInput(input));
    }

    [Theory]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(':')]
    [InlineData('=')]
    [InlineData('+')]
    [InlineData('-')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('{')]
    [InlineData('}')]
    public void PunctuationDoesNotCommitPopupCompletion(char input)
        => Assert.False(MainWindow.ShouldCommitCompletionForTextInput(input));

    [Fact]
    public void EditorDispatcherHasExplicitCommitDismissAndMousePaths()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("key is Key.Tab or Key.Enter", source, StringComparison.Ordinal);
        Assert.Contains("key == Key.Escape", source, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonUp += CompletionList_PreviewMouseLeftButtonUp", source, StringComparison.Ordinal);
        Assert.Contains("ShouldDismissCompletionForTextInput(ch)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldCommitCompletionForTextInput(ch))", source, StringComparison.Ordinal);

        var completionData = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Editor", "PowerShellCompletionData.cs");
        Assert.Contains("textArea.Document.Replace(segment, Text)", completionData, StringComparison.Ordinal);
        Assert.Contains("textArea.Caret.Offset = segment.StartOffset + Text.Length", completionData, StringComparison.Ordinal);
    }
}
