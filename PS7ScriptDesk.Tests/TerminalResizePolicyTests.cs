using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalResizePolicyTests
{
    [Fact]
    public void InvalidGeometry_IsIgnoredWithoutAdvancingGeneration()
    {
        var policy = new TerminalResizePolicy();
        policy.Reset(rendererGeneration: 1);

        var zero = policy.Evaluate(0, 24, rendererGeneration: 1);
        var negative = policy.Evaluate(80, -1, rendererGeneration: 1);

        Assert.False(zero.Accepted);
        Assert.Equal("invalid-geometry", zero.Reason);
        Assert.False(negative.Accepted);
        Assert.Equal("invalid-geometry", negative.Reason);
        Assert.Equal(0, policy.ResizeGeneration);
    }

    [Fact]
    public void DuplicateGeometry_IsCoalescedAndChangedGeometryIsAccepted()
    {
        var policy = new TerminalResizePolicy();
        policy.Reset(rendererGeneration: 4);

        var first = policy.Evaluate(120, 30, rendererGeneration: 4);
        var duplicate = policy.Evaluate(120, 30, rendererGeneration: 4);
        var changed = policy.Evaluate(121, 30, rendererGeneration: 4);

        Assert.True(first.Accepted);
        Assert.Equal(1, first.ResizeGeneration);
        Assert.False(duplicate.Accepted);
        Assert.Equal("duplicate-geometry", duplicate.Reason);
        Assert.True(changed.Accepted);
        Assert.Equal(2, changed.ResizeGeneration);
    }

    [Fact]
    public void RendererRecreation_AllowsSameGeometryForReplacementRenderer()
    {
        var policy = new TerminalResizePolicy();
        policy.Reset(rendererGeneration: 1);
        Assert.True(policy.Evaluate(100, 25, rendererGeneration: 1).Accepted);

        var replacement = policy.Evaluate(100, 25, rendererGeneration: 2);

        Assert.True(replacement.Accepted);
        Assert.Equal(1, replacement.ResizeGeneration);
        Assert.Equal(100, replacement.Columns);
        Assert.Equal(25, replacement.Rows);
    }

    [Fact]
    public void GeometryChanges_DoNotFlushOrResetProtocolFilterCarry()
    {
        var policy = new TerminalResizePolicy();
        var filter = new TerminalProtocolOutputFilter();
        const string done = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";

        var first = filter.Process("output\r\n" + done[..20]);
        var resize = policy.Evaluate(100, 25, rendererGeneration: 1);
        var second = filter.Process(done[20..] + "\r\nPS C:\\> ");

        Assert.True(resize.Accepted);
        Assert.Equal("output\r\n", first.VisibleText);
        Assert.Equal("PS C:\\> ", second.VisibleText);
        Assert.Equal(string.Empty, filter.Flush().VisibleText);
    }

    [Fact]
    public void ResizePath_OnlyReportsGeometryAndDoesNotSubmitOrFlushOutput()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var resizeStart = source.IndexOf("private void HandleResizeRequest", StringComparison.Ordinal);
        var resizeEnd = source.IndexOf("private static Dictionary<string, object?> CreateResizeTraceMetadata", resizeStart, StringComparison.Ordinal);

        Assert.True(resizeStart >= 0);
        Assert.True(resizeEnd > resizeStart);
        var resizeBlock = source[resizeStart..resizeEnd];
        Assert.DoesNotContain("WriteRaw", resizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteStructuredOutput", resizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteVisibleOutput", resizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_terminalProtocolOutputFilter.Flush", resizeBlock, StringComparison.Ordinal);
        Assert.Contains("outputSubmissionOccurred", resizeBlock, StringComparison.Ordinal);
        Assert.Contains("filterFlushOccurred", resizeBlock, StringComparison.Ordinal);
        Assert.Contains("PostResizeCommitToWebView", resizeBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserResizePath_CoalescesAnimationFramesAndIgnoresZeroBounds()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.Contains("var resizeFramePending = false", source, StringComparison.Ordinal);
        Assert.Contains("var resizeRequested = false", source, StringComparison.Ordinal);
        Assert.Contains("scheduleResizeFit", source, StringComparison.Ordinal);
        Assert.Contains("terminalElement.clientWidth <= 0 || terminalElement.clientHeight <= 0", source, StringComparison.Ordinal);
        Assert.Contains("fitAddon.proposeDimensions", source, StringComparison.Ordinal);
        Assert.Contains("state.type = 'resize_request'", source, StringComparison.Ordinal);
        Assert.Contains("if (proposed.cols === lastRequestedCols && proposed.rows === lastRequestedRows) return", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsCurrentCoreWebView2(coreWebView2))", source, StringComparison.Ordinal);
        Assert.Contains("_terminalResizePolicy.Reset(_rendererInstanceGeneration)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserResizePath_RequestsHostBeforeCommittingXtermGrid()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var start = source.IndexOf("var scheduleResizeFit = function", StringComparison.Ordinal);
        var end = source.IndexOf("var ro = new ResizeObserver", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var resizeObserverBlock = source[start..end];

        Assert.Contains("fitAddon.proposeDimensions", resizeObserverBlock, StringComparison.Ordinal);
        Assert.Contains("state.type = 'resize_request'", resizeObserverBlock, StringComparison.Ordinal);
        Assert.Contains("fitCommitted: false", resizeObserverBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("fitAddon.fit()", resizeObserverBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("term.resize(", resizeObserverBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void HostResizeCommit_IsOnlyLiveResizePathThatCallsTermResize()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var start = source.IndexOf("msg.type === 'resize_commit'", StringComparison.Ordinal);
        var end = source.IndexOf("else if (msg.type === 'focus')", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var resizeCommitBlock = source[start..end];

        Assert.Contains("Xterm.BeforeHostResizeCommit", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("term.resize(commitCols, commitRows)", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("resize_commit_ack", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("actualCols", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("actualRows", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("cursorX", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("baseY", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("Xterm.AfterHostResizeCommit", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("pendingResizeCommit = commit", resizeCommitBlock, StringComparison.Ordinal);
        Assert.Contains("hostResizeCommit", source, StringComparison.Ordinal);
        Assert.Contains("Xterm.OnResize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostResizeCommit_AckHelpersRemainVisibleToWebViewMessageListener()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var initializationTry = source.IndexOf("try {\r\n                var term = new Terminal", StringComparison.Ordinal);
        if (initializationTry < 0)
        {
            initializationTry = source.IndexOf("try {\n                var term = new Terminal", StringComparison.Ordinal);
        }

        var initializationCatch = source.IndexOf("} catch (initErr)", initializationTry, StringComparison.Ordinal);
        var messageListener = source.IndexOf("window.chrome.webview.addEventListener('message'", StringComparison.Ordinal);

        Assert.True(initializationTry >= 0);
        Assert.True(initializationCatch > initializationTry);
        Assert.True(messageListener > initializationCatch);

        var initializationBlock = source[initializationTry..initializationCatch];
        var messageListenerBlock = source[messageListener..];

        Assert.Contains("var terminalState = function ()", initializationBlock, StringComparison.Ordinal);
        Assert.Contains("var postTerminalState = function (stage, source, extra)", initializationBlock, StringComparison.Ordinal);
        Assert.Contains("var tryPostTerminalState = function (stage, source, extra)", initializationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("function terminalState()", initializationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("function postTerminalState", initializationBlock, StringComparison.Ordinal);
        Assert.Contains("tryPostTerminalState('Xterm.BeforeHostResizeCommit'", messageListenerBlock, StringComparison.Ordinal);
        Assert.Contains("var acknowledgedState = terminalState()", messageListenerBlock, StringComparison.Ordinal);
        Assert.Contains("type: 'resize_commit_ack'", messageListenerBlock, StringComparison.Ordinal);
        Assert.Contains("case \"xterm_resize_trace_error\":", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAdjacentOutputDiagnostics_TraceXtermBeforeAndAfterActualWrite()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var start = source.IndexOf("msg.type === 'output_b64'", StringComparison.Ordinal);
        var end = source.IndexOf("else if (msg.type === 'output')", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var outputBlock = source[start..end];
        var beforeIndex = outputBlock.IndexOf("Xterm.OutputBeforeWrite", StringComparison.Ordinal);
        var writeIndex = outputBlock.IndexOf("termApi.write(decodedOutput", StringComparison.Ordinal);
        var afterIndex = outputBlock.IndexOf("Xterm.OutputAfterWrite", StringComparison.Ordinal);
        var acknowledgementIndex = outputBlock.IndexOf("post({ type: 'output_ack'", StringComparison.Ordinal);

        Assert.True(beforeIndex >= 0);
        Assert.True(writeIndex > beforeIndex);
        Assert.True(afterIndex > writeIndex);
        Assert.True(acknowledgementIndex > afterIndex);
        Assert.Contains("deltaCursorY", source, StringComparison.Ordinal);
        Assert.Contains("deltaAbsoluteCursorY", source, StringComparison.Ordinal);
        Assert.Contains("xterm_output_cursor_trace", source, StringComparison.Ordinal);
        Assert.Contains("xterm_output_cursor_trace_error", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAdjacentOutputDiagnostics_AreBestEffortAndContentOmitting()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var start = source.IndexOf("msg.type === 'output_b64'", StringComparison.Ordinal);
        var end = source.IndexOf("else if (msg.type === 'output')", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var outputBlock = source[start..end];

        Assert.Contains("try", outputBlock, StringComparison.Ordinal);
        Assert.Contains("catch (traceBeforeErr)", outputBlock, StringComparison.Ordinal);
        Assert.Contains("catch (traceAfterErr)", outputBlock, StringComparison.Ordinal);
        Assert.Contains("tryPostOutputCursorTraceError", outputBlock, StringComparison.Ordinal);
        Assert.Contains("finally", outputBlock, StringComparison.Ordinal);
        Assert.Contains("contentOmitted: true", source, StringComparison.Ordinal);
        Assert.Contains("hostControlSummary", source, StringComparison.Ordinal);
        Assert.Contains("classificationSummary", source, StringComparison.Ordinal);
        Assert.Contains("classifyTerminalControls", source, StringComparison.Ordinal);
        Assert.Contains("TerminalControl.ProtocolFilter.Characterized", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAdjacentOutputDiagnostics_HelpersRemainVisibleToWebViewMessageListenerUnderStrictMode()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var initializationTry = source.IndexOf("try {\r\n                var term = new Terminal", StringComparison.Ordinal);
        if (initializationTry < 0)
        {
            initializationTry = source.IndexOf("try {\n                var term = new Terminal", StringComparison.Ordinal);
        }

        var initializationCatch = source.IndexOf("} catch (initErr)", initializationTry, StringComparison.Ordinal);
        var messageListener = source.IndexOf("window.chrome.webview.addEventListener('message'", StringComparison.Ordinal);

        Assert.True(initializationTry >= 0);
        Assert.True(initializationCatch > initializationTry);
        Assert.True(messageListener > initializationCatch);
        Assert.Contains("'use strict';", source, StringComparison.Ordinal);

        var initializationBlock = source[initializationTry..initializationCatch];
        var messageListenerBlock = source[messageListener..];

        Assert.Contains("var classifyTerminalControls = function (data)", initializationBlock, StringComparison.Ordinal);
        Assert.Contains("var postOutputCursorTraceError = function (stage, message)", initializationBlock, StringComparison.Ordinal);
        Assert.Contains("var postOutputCursorTrace = function (stage, msg, beforeState, classification)", initializationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("function classifyTerminalControls", initializationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("function postOutputCursorTraceError", initializationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("function postOutputCursorTrace", initializationBlock, StringComparison.Ordinal);
        Assert.Contains("classifyTerminalControls(decodedOutput)", messageListenerBlock, StringComparison.Ordinal);
        Assert.Contains("postOutputCursorTrace('Xterm.OutputBeforeWrite'", messageListenerBlock, StringComparison.Ordinal);
        Assert.Contains("postOutputCursorTrace('Xterm.OutputAfterWrite'", messageListenerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAdjacentOutputDiagnostics_DiagnosticFailuresCannotBypassOutputWriteOrAck()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var start = source.IndexOf("msg.type === 'output_b64'", StringComparison.Ordinal);
        var end = source.IndexOf("else if (msg.type === 'output')", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var outputBlock = source[start..end];
        var beforeCatchIndex = outputBlock.IndexOf("catch (traceBeforeErr)", StringComparison.Ordinal);
        var writeIndex = outputBlock.IndexOf("termApi.write(decodedOutput", StringComparison.Ordinal);
        var afterCatchIndex = outputBlock.IndexOf("catch (traceAfterErr)", StringComparison.Ordinal);
        var finallyIndex = outputBlock.IndexOf("finally", StringComparison.Ordinal);
        var acknowledgementIndex = outputBlock.IndexOf("post({ type: 'output_ack'", StringComparison.Ordinal);

        Assert.True(beforeCatchIndex >= 0);
        Assert.True(writeIndex > beforeCatchIndex);
        Assert.True(afterCatchIndex > writeIndex);
        Assert.True(finallyIndex > afterCatchIndex);
        Assert.True(acknowledgementIndex > finallyIndex);
        Assert.Contains("tryPostOutputCursorTraceError('Xterm.OutputBeforeWrite', traceBeforeErr)", outputBlock, StringComparison.Ordinal);
        Assert.Contains("tryPostOutputCursorTraceError('Xterm.OutputAfterWrite', traceAfterErr)", outputBlock, StringComparison.Ordinal);
        Assert.Contains("catch (traceErrorReportErr)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAdjacentOutputDiagnostics_DoNotPostRawTerminalContentInTracePayloads()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var traceStart = source.IndexOf("var postOutputCursorTrace = function", StringComparison.Ordinal);
        var traceEnd = source.IndexOf("function reportLayout", traceStart, StringComparison.Ordinal);
        var traceErrorStart = source.IndexOf("var postOutputCursorTraceError = function", StringComparison.Ordinal);
        var traceErrorEnd = source.IndexOf("var tryPostOutputCursorTraceError = function", traceErrorStart, StringComparison.Ordinal);

        Assert.True(traceStart >= 0);
        Assert.True(traceEnd > traceStart);
        Assert.True(traceErrorStart >= 0);
        Assert.True(traceErrorEnd > traceErrorStart);

        var traceBlock = source[traceStart..traceEnd];
        var traceErrorBlock = source[traceErrorStart..traceErrorEnd];

        Assert.Contains("contentOmitted: true", traceBlock, StringComparison.Ordinal);
        Assert.Contains("outputCharacterLength", traceBlock, StringComparison.Ordinal);
        Assert.Contains("hostControlSummary", traceBlock, StringComparison.Ordinal);
        Assert.Contains("classificationSummary", traceBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("decodedOutput", traceBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("msg.data", traceBlock, StringComparison.Ordinal);
        Assert.Contains("contentOmitted: true", traceErrorBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeInstrumentation_CapturesGeometryAndCursorMetadataWithoutContent()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.Contains("cursorX", source, StringComparison.Ordinal);
        Assert.Contains("cursorY", source, StringComparison.Ordinal);
        Assert.Contains("baseY", source, StringComparison.Ordinal);
        Assert.Contains("viewportY", source, StringComparison.Ordinal);
        Assert.Contains("absoluteCursorY", source, StringComparison.Ordinal);
        Assert.Contains("scrollbackLength", source, StringComparison.Ordinal);
        Assert.Contains("contentOmitted", source, StringComparison.Ordinal);
        Assert.Contains("ResizeObserver.Observed", source, StringComparison.Ordinal);
        Assert.Contains("ResizeMessage.Received", source, StringComparison.Ordinal);
        Assert.Contains("ResizePseudoConsole.CompletedBeforeXtermCommit", source, StringComparison.Ordinal);
        Assert.Contains("ResizeCommit.Posted", source, StringComparison.Ordinal);
        Assert.Contains("ResizeTransaction.OutputBuffered", source, StringComparison.Ordinal);
        Assert.Contains("ResizeTransaction.RendererDeliveryBlocked", source, StringComparison.Ordinal);
        Assert.Contains("ResizeTransaction.Coalesced", source, StringComparison.Ordinal);
        Assert.Contains("ResizeTransaction.DeferredUntilRendererIdle", source, StringComparison.Ordinal);
        Assert.Contains("ResizeOutputBarrierTimeout", source, StringComparison.Ordinal);
        Assert.Contains("ResizeOutputBarrierLimitExceeded", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPixelResize_IsNotWiredToMainWindowLiveResizePath()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "MainWindow.xaml.cs");
        var start = source.IndexOf("TerminalConsole.TerminalResized +=", StringComparison.Ordinal);
        var end = source.IndexOf("ViewModel.InitializeTerminalHostAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var terminalLoadedBlock = source[start..end];

        Assert.Contains("ViewModel?.ResizeConsole(cols, rows)", terminalLoadedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ResizeTerminalHost", terminalLoadedBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveConsoleService_ResizeInstrumentationRecordsPseudoConsoleAndOutputWindow()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.PowerShell",
            "Services",
            "LiveConsoleService.cs");

        Assert.Contains("ResizePseudoConsole.Begin", source, StringComparison.Ordinal);
        Assert.Contains("ResizePseudoConsole.End", source, StringComparison.Ordinal);
        Assert.Contains("ConPTY.OutputDuringResize", source, StringComparison.Ordinal);
        Assert.Contains("ResizeObservation.FromRequest", source, StringComparison.Ordinal);
        Assert.Contains("ResizeOutputObservationWindow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserResizePath_DoesNotClearResetOrInjectSyntheticInput()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var start = source.IndexOf("var scheduleResizeFit = function", StringComparison.Ordinal);
        var end = source.IndexOf("var ro = new ResizeObserver", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var resizeRequestBlock = source[start..end];

        var commitStart = source.IndexOf("msg.type === 'resize_commit'", StringComparison.Ordinal);
        var commitEnd = source.IndexOf("else if (msg.type === 'focus')", commitStart, StringComparison.Ordinal);

        Assert.True(commitStart >= 0);
        Assert.True(commitEnd > commitStart);
        var resizeCommitBlock = source[commitStart..commitEnd];
        var liveResizeBlock = resizeRequestBlock + resizeCommitBlock;

        Assert.DoesNotContain("term.clear", liveResizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("term.reset", liveResizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\\x1b[2J", liveResizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("post({ type: 'input'", liveResizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("type = 'input'", liveResizeBlock, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var path = Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray());
        return File.ReadAllText(path);
    }
}
