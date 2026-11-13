namespace PS7ScriptDesk.Tests;

/// <summary>
/// Guards the actual shell routing points so breakpoints cannot make a normal
/// Run entry point invoke the explicit debugger workflow.
/// </summary>
public sealed class RunDebugExecutionModeSeparationTests
{
    [Fact]
    public void RunFileMenuToolbarAndShortcutRouteToNormalSharedSessionExecution()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var runFile = ExtractMethod(shell, "private async Task RunScriptNormallyAsync()");
        var shortcutHandler = ExtractMethod(shell, "private async void Window_PreviewKeyDown");
        var runClick = ExtractMethod(shell, "private async void RunScript_Click");

        Assert.Contains("MenuItem Header=\"_Run Script\" InputGestureText=\"Ctrl+F5\" Click=\"RunScript_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Button x:Name=\"RunButton\" Click=\"RunScript_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.F5", shortcutHandler, StringComparison.Ordinal);
        Assert.Contains("isCtrl && !isShift", shortcutHandler, StringComparison.Ordinal);
        Assert.Contains("RunScriptNormallyAsync()", shortcutHandler, StringComparison.Ordinal);
        Assert.Contains("RunScriptNormallyAsync()", runClick, StringComparison.Ordinal);
        Assert.Contains("ViewModel.RunCommand.Execute(null)", runFile, StringComparison.Ordinal);
        Assert.DoesNotContain("StartDebug", runFile, StringComparison.Ordinal);
        Assert.DoesNotContain("EnabledBreakpointCount", runFile, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectBreakpoints", runFile, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSelectionAndF8RouteToNormalSelectionExecution()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var shortcutHandler = ExtractMethod(shell, "private async void EditorTextEditor_PreviewKeyDown");
        var runSelection = ExtractMethod(shell, "private async System.Threading.Tasks.Task RunSelectionFromEditorAsync");
        var runSelectionClick = ExtractMethod(shell, "private async void RunSelection_Click");

        Assert.Contains("MenuItem Header=\"Run _Selection\" InputGestureText=\"F8\" Click=\"RunSelection_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Button x:Name=\"RunSelectionButton\" Click=\"RunSelection_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("InputGestureText=\"F8\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RunSelection_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.F8", shortcutHandler, StringComparison.Ordinal);
        Assert.Contains("RunSelectionFromEditorAsync(editorTextEditor)", shortcutHandler, StringComparison.Ordinal);
        Assert.Contains("RunSelectionFromEditorAsync(editorTextEditor)", runSelectionClick, StringComparison.Ordinal);
        Assert.Contains("ViewModel.RunSelectionAsync(selectedText, sourceInfo)", runSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("StartDebug", runSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("Breakpoint", runSelection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitDebugRemainsTheOnlyRunAdjacentEntryThatCollectsAndBindsBreakpoints()
    {
        var shell = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var xaml = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var debugCommand = ExtractMethod(shell, "private void StartDebugCommand_Executed");
        var debugHandler = ExtractMethod(shell, "private async void StartDebug_Click");
        var windowShortcutHandler = ExtractMethod(shell, "private async void Window_PreviewKeyDown");
        var editorShortcutHandler = ExtractMethod(shell, "private async void EditorTextEditor_PreviewKeyDown");

        Assert.Contains("StartDebugCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("InputGestureText=\"F5\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StartDebug_Click", debugCommand, StringComparison.Ordinal);
        Assert.Contains("CollectBreakpoints(launchScriptPath, selectedTab)", debugHandler, StringComparison.Ordinal);
        Assert.Contains("StartAsync(runtime", debugHandler, StringComparison.Ordinal);
        Assert.Contains("_debugSession?.CurrentState == DebugSessionState.Paused", windowShortcutHandler, StringComparison.Ordinal);
        Assert.Contains("TryExecuteDebuggerShortcut(ContinueDebugCommand", windowShortcutHandler, StringComparison.Ordinal);
        Assert.Contains("TryExecuteDebuggerShortcut(StartDebugCommand", windowShortcutHandler, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.F9", editorShortcutHandler, StringComparison.Ordinal);
        Assert.Contains("ToggleBreakpointForEditor(editorTextEditor)", editorShortcutHandler, StringComparison.Ordinal);
        Assert.Contains("F9", xaml, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Could not locate method signature: {signature}");

        var bodyStart = source.IndexOf('{', signatureIndex);
        Assert.True(bodyStart >= 0, $"Could not locate method body: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
            {
                return source[signatureIndex..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unterminated method body: {signature}");
    }
}
