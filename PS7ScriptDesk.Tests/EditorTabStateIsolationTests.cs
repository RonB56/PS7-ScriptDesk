using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class EditorTabStateIsolationTests
{
    [Fact]
    public void SelectionCaretAndViewportStateBelongToTheIndividualTab()
    {
        var tabA = new EditorTabViewModel("A.ps1", new string('a', 30000));
        var tabB = new EditorTabViewModel("B.ps1", new string('b', 500));
        var tabC = new EditorTabViewModel("C.ps1", new string('c', 5000));

        tabA.UpdateEditorViewState(400, 9, 12000, 11000, 1000, 80, 6000);
        tabB.UpdateEditorViewState(20, 3, 140, 100, 40, 0, 120);
        tabC.UpdateEditorViewState(75, 2, 3200, 3000, 200, 24, 1500);

        Assert.Equal((11000, 1000, 12000, 80d, 6000d), (tabA.SelectionStart, tabA.SelectionLength, tabA.CaretOffset, tabA.HorizontalScrollOffset, tabA.VerticalScrollOffset));
        Assert.Equal((100, 40, 140, 0d, 120d), (tabB.SelectionStart, tabB.SelectionLength, tabB.CaretOffset, tabB.HorizontalScrollOffset, tabB.VerticalScrollOffset));
        Assert.Equal((3000, 200, 3200, 24d, 1500d), (tabC.SelectionStart, tabC.SelectionLength, tabC.CaretOffset, tabC.HorizontalScrollOffset, tabC.VerticalScrollOffset));
    }

    [Fact]
    public void DifferentDocumentLengthsNormalizeOnlyTheirOwnState()
    {
        var shortTab = new EditorTabViewModel("short.ps1", "short");
        var longTab = new EditorTabViewModel("long.ps1", new string('x', 30000));

        shortTab.UpdateEditorViewState(1, 1, 50000, 40000, 10000, 0, 0);
        longTab.UpdateEditorViewState(3000, 1, 25000, 24000, 500, 0, 2000);

        Assert.Equal(50000, shortTab.CaretOffset);
        Assert.Equal(40000, shortTab.SelectionStart);
        Assert.Equal(25000, longTab.CaretOffset);
        Assert.Equal(24000, longTab.SelectionStart);
    }

    [Fact]
    public void EditorHostUsesPerTabDocumentsAndRestoresStateInsteadOfClearingSelection()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Dictionary<EditorTabViewModel, TextDocument>", main, StringComparison.Ordinal);
        Assert.Contains("editorTextEditor.Document = document", main, StringComparison.Ordinal);
        Assert.Contains("RestoreEditorViewState(editorTextEditor, tab)", main, StringComparison.Ordinal);
        Assert.Contains("editorTextEditor.Select(selectionStart, selectionLength)", main, StringComparison.Ordinal);
        Assert.Contains("ScrollToVerticalOffset", main, StringComparison.Ordinal);
        Assert.DoesNotContain("editorTextEditor.Select(0, 0)", main, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorAsyncAndVisualStateMapsRemainIdentityKeyed()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Dictionary<TextEditor, EditorTabViewModel>", main, StringComparison.Ordinal);
        Assert.Contains("IsLiveSyntaxRequestCurrent", main, StringComparison.Ordinal);
        Assert.Contains("IsAuthoringDiagnosticsRequestCurrent", main, StringComparison.Ordinal);
        Assert.Contains("_editorRegistrationVersions", main, StringComparison.Ordinal);
        Assert.Contains("_diagnosticsRequestVersions", main, StringComparison.Ordinal);
        Assert.Contains("CloseEditorCompletion", main, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentRebindUninstallsFoldingAndGuardsDeferredFocusByIdentity()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("UninstallFoldingManager(editorTextEditor)", main, StringComparison.Ordinal);
        Assert.Contains("FoldingManager.Uninstall(foldingManager)", main, StringComparison.Ordinal);
        Assert.Contains("_documentByTab.TryGetValue(targetTab, out var targetDocument)", main, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(editorTextEditor.Document, targetDocument)", main, StringComparison.Ordinal);
        Assert.Contains("focusRequestVersion != Volatile.Read", main, StringComparison.Ordinal);
        Assert.Contains("EnsureVisualLines", main, StringComparison.Ordinal);
        var focusStart = main.IndexOf("private void FocusActiveEditorSoon()", StringComparison.Ordinal);
        var focusEnd = main.IndexOf("private void OpenFile_Click", focusStart, StringComparison.Ordinal);
        Assert.True(focusStart >= 0 && focusEnd > focusStart);
        Assert.DoesNotContain("catch (ArgumentException", main[focusStart..focusEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void FileOpenEntryPointUsesTheSharedDocumentOpenPathAndGuardedFocus()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var openStart = main.IndexOf("private void OpenFile_Click", StringComparison.Ordinal);
        var openEnd = main.IndexOf("private void FileMenuItem_SubmenuOpened", openStart, StringComparison.Ordinal);

        Assert.True(openStart >= 0 && openEnd > openStart);
        var openHandler = main[openStart..openEnd];
        Assert.Contains("ViewModel.OpenFileFromPath(dialog.FileName)", openHandler, StringComparison.Ordinal);
        Assert.Contains("FocusActiveEditorSoon()", openHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("editorTextEditor.Document = new TextDocument", openHandler, StringComparison.Ordinal);
    }
}
