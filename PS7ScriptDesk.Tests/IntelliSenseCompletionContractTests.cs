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

        Assert.Contains("key == Key.Enter", source, StringComparison.Ordinal);
        Assert.Contains("HandleCompletionCycleKeyAsync(editorTextEditor, direction: 1)", source, StringComparison.Ordinal);
        Assert.Contains("HandleCompletionCycleKeyAsync(editorTextEditor, direction: -1)", source, StringComparison.Ordinal);
        Assert.Contains("key == Key.Escape", source, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonUp += CompletionList_PreviewMouseLeftButtonUp", source, StringComparison.Ordinal);
        Assert.Contains("ShouldDismissCompletionForTextInput(ch)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldCommitCompletionForTextInput(ch))", source, StringComparison.Ordinal);

        var completionData = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Editor", "PowerShellCompletionData.cs");
        Assert.Contains("textArea.Document.Replace(segment, Text)", completionData, StringComparison.Ordinal);
        Assert.Contains("textArea.Caret.Offset = segment.StartOffset + Text.Length", completionData, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitTabAwaitsProviderAndRestoresIndentOnlyForCurrentNoMatchContext()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("forceCompletion: true, showPopup: false", source, StringComparison.Ordinal);
        Assert.Contains("providerOnly: true", source, StringComparison.Ordinal);
        Assert.Contains("await requestTask.ConfigureAwait(true)", source, StringComparison.Ordinal);
        Assert.Contains("CompletionCycleFallbackToIndent", source, StringComparison.Ordinal);
        Assert.Contains("if (direction > 0 && snapshot.IsCurrent(this))", source, StringComparison.Ordinal);
        Assert.Contains("editor.TextArea.PerformTextInput(\"\\t\")", source, StringComparison.Ordinal);
        Assert.Contains("pending.QueuedDirectionDelta += direction", source, StringComparison.Ordinal);

        var service = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Editor", "PowerShellIntelliSenseService.cs");
        Assert.Contains("var shouldQueryAuthoritative = providerOnly", service, StringComparison.Ordinal);
        Assert.Contains("? authoritativeCompletionAvailable", service, StringComparison.Ordinal);
        Assert.Contains("BuildProviderOnlyWindow(editor, source, engineResult)", service, StringComparison.Ordinal);
        Assert.Contains("GetCompletionsAsync(", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ImmediateHyphenAndPartialCommandTabReachProviderBackedCompletion()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("var includeEngine = ch is '-' or '.' or ':' or '\\\\' or '/'", source, StringComparison.Ordinal);
        Assert.Contains("providerOnly: includeEngine", source, StringComparison.Ordinal);
        Assert.Contains("var isParameterToken = fragment.StartsWith(\"-\", StringComparison.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("providerOnly: isParameterToken", source, StringComparison.Ordinal);
        Assert.Contains("fragment.Length >= 2", source, StringComparison.Ordinal);
        Assert.Contains("HasCompletionContext(editorTextEditor)", source, StringComparison.Ordinal);
        Assert.Contains("forceCompletion: true, showPopup: false, providerOnly: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionRequestFreshnessUsesEditorDocumentVersionAndCaret()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("ReferenceEquals(Editor.Document, Document)", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(Document.Version, DocumentVersion)", source, StringComparison.Ordinal);
        Assert.Contains("Editor.CaretOffset != CaretOffset", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(owner.FindActiveEditor(), Editor)", source, StringComparison.Ordinal);
        Assert.Contains("ITextSource TextSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(Editor.Text, Text", source, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.Text ?? string.Empty", source[source.IndexOf("private sealed record CompletionRequestSnapshot", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("CompletionResultRejectedAsStale", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionServiceUsesSnapshotRangesBeforeAnyFullSourceMaterialization()
    {
        var service = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Editor", "PowerShellIntelliSenseService.cs");
        var methodStart = service.IndexOf("public async Task<CompletionWindow?> ShowCompletionAsync", StringComparison.Ordinal);
        var methodEnd = service.IndexOf("private async Task<CompletionServiceResult?> GetAuthoritativeCompletionsAsync", methodStart, StringComparison.Ordinal);
        var routingStart = service.IndexOf("var shouldQueryEngine =", methodStart, StringComparison.Ordinal);
        var engineMaterialization = service.IndexOf("var engineDocumentText = GetFullDocumentText(", routingStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        Assert.True(routingStart > methodStart);
        Assert.True(engineMaterialization > routingStart);
        Assert.Contains("GetCompletionContext(source, caretOffset)", service, StringComparison.Ordinal);
        Assert.Contains("source.GetText(0, source.TextLength)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.Text", service[methodStart..methodEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingPopupAcceptanceAndMultilineIndentRoutesRemain()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("_activeCompletionWindow.CompletionList.RequestInsertion(e)", source, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonUp += CompletionList_PreviewMouseLeftButtonUp", source, StringComparison.Ordinal);
        Assert.Contains("if (modifiers == ModifierKeys.Shift && key == Key.Tab)", source, StringComparison.Ordinal);
        Assert.Contains("EditorProductivityCommands.Outdent", source, StringComparison.Ordinal);
        Assert.Contains("EditorProductivityCommands.Indent", source, StringComparison.Ordinal);
        Assert.Contains("if (modifiers == ModifierKeys.Control && key == Key.Space)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PopupTabStartsCycleFromHighlightedCandidateAndRetainsSession()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var handlerStart = source.IndexOf("private async void EditorTextEditor_PreviewKeyDown", StringComparison.Ordinal);
        var handlerEnd = source.IndexOf("private void Find_Click", handlerStart, StringComparison.Ordinal);

        Assert.True(handlerStart >= 0);
        Assert.True(handlerEnd > handlerStart);

        var handler = source[handlerStart..handlerEnd];
        var popupTabIndex = handler.IndexOf("if (isForwardCompletionTab &&", StringComparison.Ordinal);
        var cycleTabIndex = handler.IndexOf("HandleCompletionCycleKeyAsync(editorTextEditor, direction: 1)", StringComparison.Ordinal);

        Assert.True(popupTabIndex >= 0);
        Assert.True(cycleTabIndex > popupTabIndex);

        var popupTabRoute = handler[popupTabIndex..cycleTabIndex];
        Assert.Contains("_activeCompletionWindow is { IsVisible: true } completionWindowForTab", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("HasCompletionWindowFor(editorTextEditor)", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("var selectedItem = completionWindowForTab.CompletionList.SelectedItem", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("TryCreateCompletionCycleSession(editorTextEditor, popupSnapshot, completionWindowForTab", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("_completionCycleSession = popupCycleSession", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("TryFindCandidateIndex(selectedCompletionData.Text, out var selectedCandidateIndex)", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("ApplyCompletionCycleCandidate(editorTextEditor, popupCycleSession, selectedCandidateIndex)", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("Completion cycle started from popup selection", popupTabRoute, StringComparison.Ordinal);
        Assert.Contains("completionWindowForTab.CompletionList.RequestInsertion(e)", popupTabRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleCompletionCycleKeyAsync", popupTabRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowCompletionAsync", popupTabRoute, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(popupTabRoute, "RequestInsertion(e)"));

        var cycleStartIndex = popupTabRoute.IndexOf("_completionCycleSession = popupCycleSession", StringComparison.Ordinal);
        var fallbackIndex = popupTabRoute.IndexOf("completionWindowForTab.CompletionList.RequestInsertion(e)", StringComparison.Ordinal);
        Assert.True(cycleStartIndex >= 0);
        Assert.True(fallbackIndex > cycleStartIndex);
        Assert.DoesNotContain("_completionCycleSession = null", popupTabRoute[..fallbackIndex], StringComparison.Ordinal);
        Assert.Contains("CompletionPopupTabAcceptance", popupTabRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlSpaceIsNotSuppressedWhenTheEditorOwnsKeyboardFocus()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("_terminalIsActive && !editorTextEditor.IsKeyboardFocusWithin", source, StringComparison.Ordinal);
        Assert.Contains("SetTerminalActive(false, \"EditorPreviewKeyDownFocusedEditor\")", source, StringComparison.Ordinal);
        Assert.Contains("EditorPreviewKeyDownRoutingDecision", source, StringComparison.Ordinal);
        Assert.Contains("key == Key.Space", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionCycleIsInvalidatedWhenEditorOwnershipEnds()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var closeMethodStart = source.IndexOf("private void CloseEditorCompletion(string reason)", StringComparison.Ordinal);
        var closeMethodEnd = source.IndexOf("private static void CloseCompletionWindowIfShown", closeMethodStart, StringComparison.Ordinal);

        Assert.True(closeMethodStart >= 0);
        Assert.True(closeMethodEnd > closeMethodStart);

        var closeMethod = source[closeMethodStart..closeMethodEnd];
        Assert.Contains("_pendingCompletionCycle = null", closeMethod, StringComparison.Ordinal);
        Assert.Contains("_completionCycleSession = null", closeMethod, StringComparison.Ordinal);
        Assert.Contains("CloseEditorCompletion(\"Document tab closed\")", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(FindActiveEditor(), editor)", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(Editor.Document, Document)", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
