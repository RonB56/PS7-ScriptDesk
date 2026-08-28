namespace PS7ScriptDesk.Tests;

public sealed class TerminalArchitecturePolicyTests
{
    [Fact]
    public void TerminalWiring_HasNoGenericApplicationTextSinkIntoXterm()
    {
        var viewModelSource = ReadRepositoryFile(
            "PS7ScriptDesk.UI",
            "ViewModels",
            "MainWindowViewModel.cs");
        var shellSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");
        var shellXaml = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml");
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.DoesNotContain("SetTerminalSinks", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_writeTextSink", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDebuggerOutputSink", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("SetTerminalSessionControls", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.WriteRaw(generation, raw)", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("raw => Dispatcher.BeginInvoke(() => TerminalConsole.WriteRaw(raw))", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalConsole.WriteDebuggerOutput", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteDebuggerOutput", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("TerminalOutputFlowController", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("output_ack", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("BeginTerminalOutputGeneration", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Acknowledge(generation, sequence)", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("startup.timeout50", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("fitTerminal('window.focus')", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("ResizeObserver", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("signalReady('startup.raf2')", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("SummarizeVtControls", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("First terminal output after the first xterm input was observed", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("write: function (d, callback)", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("term.write(d, callback)", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("DebuggerOutputText", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DebuggerOutputText, Mode=OneWay}\"", shellXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DebuggerTeardown_DoesNotPreserveOrReconstructInteractiveTerminalOutput()
    {
        var shellSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var debugSessionSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Debugger",
            "PsesDebugSession.cs");

        Assert.DoesNotContain("PreserveVisibleTranscript", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreVisiblePromptAfterDebug", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildVisiblePromptTextForDebugCompletion", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldSuppressPromptRedrawChunk", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeferResizeDuringTranscriptPreservation", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("await debugSession.StopAsync", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WaitAll", debugSessionSource, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync", debugSessionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalHotPaths_DoNotContainSynchronousTranscriptAppendCode()
    {
        var liveConsoleSource = ReadRepositoryFile(
            "PS7ScriptDesk.PowerShell",
            "Services",
            "LiveConsoleService.cs");
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.DoesNotContain("File.AppendAllText", liveConsoleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.AppendAllText", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalCaptureState", liveConsoleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalCompatibility_PreservesPsReadLineKeysAndExposesAccessibilityMetadata()
    {
        var shellSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var terminalControlXaml = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml");

        Assert.Contains("if (TerminalConsole.IsKeyboardFocusWithin)", shellSource, StringComparison.Ordinal);
        Assert.Contains("screenReaderMode: true", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("minimumContrastRatio: 4.5", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("ctrl-shift-f6", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("command: 'leave_terminal'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("binaryInputBridge: false", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("mousePasteGesture: 'shift-right-click'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("if (!e.shiftKey) return", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Light: traditionalTerminalTheme", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("IseBlue: traditionalTerminalTheme", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("term.onData", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("command: 'find'", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("command: 'replace'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Interactive PowerShell terminal\"", terminalControlXaml, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigation.TabNavigation=\"Once\"", terminalControlXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalVisualThemes_KeepTraditionalBlackConsoleReadableInEveryApplicationTheme()
    {
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.Contains("background: '#000000', foreground: '#F2F2F2'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("cursor: '#00FF00', cursorAccent: '#000000'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("selectionBackground: 'rgba(88,166,255,0.35)', selectionForeground: '#FFFFFF'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Dark: traditionalTerminalTheme", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Light: traditionalTerminalTheme", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("IseBlue: traditionalTerminalTheme", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("red: '#FF5555', brightRed: '#FF7A7A'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("white: '#F2F2F2', brightWhite: '#FFFFFF'", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("background: '#EAF2FB'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("type: 'terminal_theme_applied'", terminalControlSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalStartup_UsesMeasuredHostBoundsInsteadOfPlaceholderGeometry()
    {
        var shellSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");

        Assert.Contains("TerminalConsole.ActualWidth", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.ActualHeight", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeTerminalHostAsync(IntPtr.Zero, 120, 30)", shellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedTerminalBootstrap_UsesOneWayRendererUnavailableStateWithoutPersistingTerminalContent()
    {
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var flowControllerSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalOutputBridge.cs");

        Assert.Contains("NavigationCompleted += OnNavigationCompleted", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("_webView2Available = false", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("_isReady = false", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("FallbackBanner.Visibility = Visibility.Visible", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("WebView.Visibility = Visibility.Collapsed", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("navigationStage\"] = \"TerminalHtmlBootstrap\"", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("navigationOrigin\"] = \"NavigateToString\"", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.HasShutdownStarted", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.HasShutdownFinished", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("MarkRendererUnavailable", terminalControlSource, StringComparison.Ordinal);
        var navigationHandlerIndex = terminalControlSource.IndexOf("private void OnNavigationCompleted", StringComparison.Ordinal);
        Assert.True(navigationHandlerIndex >= 0);
        Assert.DoesNotContain("[\"terminalHtml\"]", terminalControlSource[navigationHandlerIndex..], StringComparison.Ordinal);
        Assert.Contains("_rendererUnavailable", flowControllerSource, StringComparison.Ordinal);
        Assert.Contains("MarkRendererUnavailable", flowControllerSource, StringComparison.Ordinal);
        Assert.Contains("if (_rendererUnavailable)", flowControllerSource, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine([directory.FullName, .. relativeSegments]);
        Assert.True(File.Exists(path), $"Expected repository file was not found: {path}");
        return File.ReadAllText(path);
    }
}
