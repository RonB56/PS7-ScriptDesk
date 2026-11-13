using System.Collections.ObjectModel;
using System.IO;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class BreakpointBulkOperationsTests
{
    [Fact]
    public void RemoveAllWithZeroBreakpointsIsSafeNoOp()
    {
        var documents = new[] { CreateDocument("a.ps1") };
        Assert.Equal(0, BreakpointBulkOperations.ClearAll(documents));
        Assert.Empty(documents[0].BreakpointLineNumbers);
    }

    [Fact]
    public void RemoveAllClearsOneBreakpointFromAuthoritativeModel()
    {
        var document = CreateDocument("a.ps1");
        document.ToggleBreakpoint(2);
        Assert.Equal(1, BreakpointBulkOperations.ClearAll([document]));
        Assert.Empty(document.BreakpointLineNumbers);
        Assert.Equal(0, document.EnabledBreakpointCount);
    }

    [Fact]
    public void RemoveAllClearsMultipleBreakpointsInOneDocument()
    {
        var document = CreateDocument("a.ps1");
        document.ToggleBreakpoint(2);
        document.ToggleBreakpoint(5);
        document.SetBreakpointEnabled(5, false);
        Assert.Equal(2, BreakpointBulkOperations.ClearAll([document]));
        Assert.Empty(document.BreakpointLineNumbers);
    }

    [Fact]
    public void RemoveAllClearsBreakpointsAcrossDocuments()
    {
        var first = CreateDocument("a.ps1");
        var second = CreateDocument("b.ps1");
        first.ToggleBreakpoint(1);
        second.ToggleBreakpoint(3);
        Assert.Equal(2, BreakpointBulkOperations.ClearAll(new ObservableCollection<EditorTabViewModel> { first, second }));
        Assert.Empty(first.BreakpointLineNumbers);
        Assert.Empty(second.BreakpointLineNumbers);
    }

    [Fact]
    public void CurrentDocumentRemovalPreservesOtherDocuments()
    {
        var first = CreateDocument("a.ps1");
        var second = CreateDocument("b.ps1");
        first.ToggleBreakpoint(1);
        second.ToggleBreakpoint(4);
        Assert.Equal(1, BreakpointBulkOperations.ClearDocument([first, second], first.DiagnosticDocument.DocumentId));
        Assert.Empty(first.BreakpointLineNumbers);
        Assert.Equal(new[] { 4 }, second.BreakpointLineNumbers);
    }

    [Fact]
    public void CurrentDocumentRemovalSupportsUnsavedDocumentIdentity()
    {
        var untitled = new EditorTabViewModel("Untitled1.ps1", "one\ntwo");
        untitled.ToggleBreakpoint(2);
        Assert.Null(untitled.FilePath);
        Assert.Equal(1, BreakpointBulkOperations.ClearDocument([untitled], untitled.DiagnosticDocument.DocumentId));
        Assert.Empty(untitled.BreakpointLineNumbers);
    }

    [Fact]
    public void BulkRemovalIncludesOpenModelsWithoutLoadedEditorControls()
    {
        var visible = CreateDocument("visible.ps1");
        var unloaded = CreateDocument("unloaded.ps1");
        visible.ToggleBreakpoint(1);
        unloaded.ToggleBreakpoint(2);
        // OpenTabs is the ownership collection; membership does not depend on _editorByTab.
        Assert.Equal(2, BreakpointBulkOperations.ClearAll([visible, unloaded]));
        Assert.Empty(unloaded.BreakpointLineNumbers);
    }

    [Fact]
    public void ClearBreakpointsPublishesGutterAndListVersionChangeOnce()
    {
        var document = CreateDocument("a.ps1");
        document.ToggleBreakpoint(2);
        document.ToggleBreakpoint(3);
        var initialVersion = document.BreakpointVersion;
        var observedVersions = new List<int>();
        document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorTabViewModel.BreakpointVersion))
                observedVersions.Add(document.BreakpointVersion);
        };

        Assert.Equal(2, document.ClearBreakpoints());
        Assert.Equal(initialVersion + 1, document.BreakpointVersion);
        Assert.Equal(new[] { document.BreakpointVersion }, observedVersions);
        Assert.Equal("Breakpoints: None", document.BreakpointDisplayText);
    }

    [Fact]
    public void F9ToggleCanAddAndRemoveAfterBulkClear()
    {
        var document = CreateDocument("a.ps1");
        document.ToggleBreakpoint(2);
        BreakpointBulkOperations.ClearAll([document]);
        Assert.True(document.ToggleBreakpoint(2));
        Assert.Equal(new[] { 2 }, document.GetEnabledBreakpointLines());
        Assert.False(document.ToggleBreakpoint(2));
        Assert.Empty(document.BreakpointLineNumbers);
    }

    [Fact]
    public void NewBreakpointsCanBeAddedAfterBulkClear()
    {
        var document = CreateDocument("a.ps1");
        document.ToggleBreakpoint(1);
        BreakpointBulkOperations.ClearAll([document]);
        Assert.True(document.ToggleBreakpoint(4));
        Assert.Equal(new[] { 4 }, document.BreakpointLineNumbers);
    }

    [Fact]
    public void RepeatedRemoveOperationsAreSafeNoOps()
    {
        var document = CreateDocument("a.ps1");
        document.ToggleBreakpoint(1);
        Assert.Equal(1, BreakpointBulkOperations.ClearAll([document]));
        Assert.Equal(0, BreakpointBulkOperations.ClearAll([document]));
        Assert.Equal(0, BreakpointBulkOperations.ClearDocument([document], document.DiagnosticDocument.DocumentId));
    }

    [Fact]
    public void ClosedDocumentBreakpointsAreOutsideOpenDocumentOwnershipScope()
    {
        var closed = CreateDocument("closed.ps1");
        closed.ToggleBreakpoint(6);
        var currentlyOpen = CreateDocument("open.ps1");
        currentlyOpen.ToggleBreakpoint(1);
        Assert.Equal(1, BreakpointBulkOperations.ClearAll([currentlyOpen]));
        Assert.Equal(new[] { 6 }, closed.BreakpointLineNumbers);
    }

    [Fact]
    public void CommandsAreDisabledDuringActiveDebugAndUseNoLiveBackendMutation()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        Assert.Contains("e.CanExecute = ViewModel is not null && _debugSession is null", source, StringComparison.Ordinal);
        Assert.Contains("e.CanExecute = ViewModel?.SelectedTab is not null && _debugSession is null", source, StringComparison.Ordinal);
        Assert.Contains("ClearBreakpointsForOpenDocuments(bool currentDocumentOnly)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendRequestAsync(\"setBreakpoints\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunDebugSeparationAndDebuggerReadinessPathRemainPresent()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        Assert.Contains("BeginDebuggerPromptReadinessReconciliation(debugSession.SessionId)", source, StringComparison.Ordinal);
        Assert.Contains("CollectBreakpoints(launchScriptPath, selectedTab)", source, StringComparison.Ordinal);
        Assert.Contains("Run File", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuPaletteAndHelpExposeSeparateCommandsWithoutShortcuts()
    {
        var xaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var code = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var help = ReadRepositoryFile("PS7ScriptDesk.Shell", "Help", "HelpTopicCatalog.cs");
        Assert.Contains("Command.RemoveAllBreakpoints", xaml, StringComparison.Ordinal);
        Assert.Contains("Command.RemoveCurrentFileBreakpoints", xaml, StringComparison.Ordinal);
        Assert.Contains("debug.removeAllBreakpoints", code, StringComparison.Ordinal);
        Assert.Contains("debug.removeCurrentFileBreakpoints", code, StringComparison.Ordinal);
        Assert.Contains("Command.RemoveAllBreakpoints", help, StringComparison.Ordinal);
        Assert.Contains("Command.RemoveCurrentFileBreakpoints", help, StringComparison.Ordinal);
        Assert.Contains("F9", xaml, StringComparison.Ordinal);
    }

    private static EditorTabViewModel CreateDocument(string path)
        => new(Path.GetFileName(path), "one\ntwo\nthree\nfour\nfive\nsix", path);

    private static string ReadRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return File.ReadAllText(Path.Combine([current!.FullName, .. segments]));
    }
}
