using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalArchitecturePolicyTests
{
    [Theory]
    [InlineData(true, true, TerminalCtrlCDisposition.CopySelection)]
    [InlineData(true, false, TerminalCtrlCDisposition.CopySelection)]
    [InlineData(false, true, TerminalCtrlCDisposition.Interrupt)]
    [InlineData(false, false, TerminalCtrlCDisposition.PassThrough)]
    public void CtrlCOwnership_IsSelectionAwareAndDoesNotInterruptCopy(
        bool selectionPresent,
        bool terminalSessionRunning,
        TerminalCtrlCDisposition expected)
    {
        Assert.Equal(
            expected,
            TerminalKeyboardOwnershipPolicy.ResolveCtrlC(selectionPresent, terminalSessionRunning));
    }

    [Fact]
    public void TerminalCopyPath_UsesXtermSelectionAndHostClipboardWithoutWpfTextReconstruction()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.Contains("term.hasSelection()", source, StringComparison.Ordinal);
        Assert.Contains("term.getSelection()", source, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'copy', text: term.getSelection() })", source, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Clipboard.SetText(copyText, System.Windows.TextDataFormat.UnicodeText)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalCtrlCJavaScript_SuppressesCopyButPassesInterruptThrough()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var handlerStart = source.IndexOf("term.attachCustomKeyEventHandler", StringComparison.Ordinal);
        var handlerEnd = source.IndexOf("// Do not turn right-click into paste", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        var handler = source[handlerStart..handlerEnd];

        Assert.Contains("if (term.hasSelection())", handler, StringComparison.Ordinal);
        Assert.Contains("return false;", handler, StringComparison.Ordinal);
        Assert.Contains("return true; // no selection → pass through as \\x03 (SIGINT)", handler, StringComparison.Ordinal);
        Assert.Contains("term.clearSelection()", handler, StringComparison.Ordinal);
    }

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
        Assert.Contains("private MainWindowViewModel? ViewModel => Volatile.Read(ref _viewModel)", shellSource, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SubscribeRawOutput(OnRawTerminalOutputReceived)", shellSource, StringComparison.Ordinal);
        Assert.Contains("viewModel.PublishInteractiveTerminalOutput(generation, rawOutput)", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalOutputPublished += EnqueueTerminalOutputForRenderer", shellSource, StringComparison.Ordinal);
        Assert.Contains("DrainTerminalOutputForRenderer", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.WriteRaw(envelope.InteractiveTerminalSessionGeneration, envelope.Payload)", shellSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.WriteStructuredOutput(envelope.RendererGeneration, envelope.Payload)", shellSource, StringComparison.Ordinal);
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
    public void TerminalCompatibility_DeclaresConPtyHistoryPreservationForXtermResize()
    {
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.Contains("xtermVersion: '__PS7_XTERM_VERSION__'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("windowsPty: {", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("backend: '__PS7_WINDOWS_PTY_BACKEND__'", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("buildNumber: __PS7_WINDOWS_PTY_BUILD_NUMBER__", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("private static string GetWindowsPtyBuildNumber()", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Environment.OSVersion.Version.Build", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("filterEraseLine", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PreserveVisibleTranscript", terminalControlSource, StringComparison.Ordinal);
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
        Assert.Contains("RetireWebView2Renderer", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("WebViewHost.Children.Remove(retiredRenderer)", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView.Visibility", terminalControlSource, StringComparison.Ordinal);
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

    [Fact]
    public void WebView2Renderer_IsDynamicallyOwnedAndRetiredBeforeDisposal()
    {
        var terminalControlSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var terminalControlXaml = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml");
        var shellSource = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");

        Assert.DoesNotContain("x:Name=\"WebView\"", terminalControlXaml, StringComparison.Ordinal);
        Assert.Contains("new WebView2", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("_webView = null", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("retiredLifecycle.TryBeginDisposal()", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("Keyboard.ClearFocus()", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ContextIdle", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("TryGetCurrentRenderer", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("lifecycle.CanUseRenderer", terminalControlSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView.CoreWebView2", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("ResetRendererForRetry", terminalControlSource, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.ResetRendererForRetry()", shellSource, StringComparison.Ordinal);
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
