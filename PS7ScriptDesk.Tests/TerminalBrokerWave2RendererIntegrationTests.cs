using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalBrokerWave2RendererIntegrationTests
{
    private static EditorExecutionAdmissionFacts AdmissionFacts(InteractiveTerminalState state) => new(
        state, true, true, true, null, null);

    [Theory]
    [InlineData(InteractiveTerminalState.InteractiveIdleAtPrompt, true)]
    [InlineData(InteractiveTerminalState.InteractiveInputEditing, false)]
    [InlineData(InteractiveTerminalState.InteractiveCommandRunning, false)]
    [InlineData(InteractiveTerminalState.Unavailable, false)]
    [InlineData(InteractiveTerminalState.Starting, false)]
    [InlineData(InteractiveTerminalState.Stopping, false)]
    public void PromptAdmission_IsExplicitAndConservative(InteractiveTerminalState state, bool admitted)
    {
        var coordinator = new InteractiveTerminalCoordinator();

        coordinator.SetState(state, "test");

        Assert.Equal(admitted, coordinator.CanStartEditorExecution);
        Assert.Equal(state, coordinator.Snapshot.State);
        Assert.Equal("test", coordinator.Snapshot.Reason);
    }

    [Fact]
    public void InteractiveState_RejectsStaleGenerationReplacement()
    {
        var coordinator = new InteractiveTerminalCoordinator();

        Assert.True(coordinator.TryReplaceGeneration(4, InteractiveTerminalState.InteractiveIdleAtPrompt, "current"));
        Assert.False(coordinator.TryReplaceGeneration(3, InteractiveTerminalState.Unavailable, "stale"));

        Assert.Equal(4, coordinator.Snapshot.Generation);
        Assert.Equal(InteractiveTerminalState.InteractiveIdleAtPrompt, coordinator.State);
    }

    [Fact]
    public void Multiplexer_AdmissionRejectsInteractiveInputEditing()
    {
        var multiplexer = new TerminalOutputMultiplexer();
        multiplexer.SetInteractiveState(InteractiveTerminalState.InteractiveInputEditing);

        Assert.False(multiplexer.TryBeginEditorExecution(Guid.NewGuid(), AdmissionFacts(multiplexer.InteractiveState), out var reason));
        Assert.Contains("unfinished input line", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Multiplexer_AdmissionRejectsActiveInteractiveCommand()
    {
        var multiplexer = new TerminalOutputMultiplexer();
        multiplexer.SetInteractiveState(InteractiveTerminalState.InteractiveCommandRunning);

        Assert.False(multiplexer.TryBeginEditorExecution(Guid.NewGuid(), AdmissionFacts(multiplexer.InteractiveState), out var reason));
        Assert.Contains("interactive terminal command", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(InteractiveTerminalState.Unavailable, "unavailable")]
    [InlineData(InteractiveTerminalState.Starting, "starting")]
    [InlineData(InteractiveTerminalState.InteractiveInputEditing, "unfinished input line")]
    [InlineData(InteractiveTerminalState.InteractiveCommandRunning, "interactive terminal command")]
    [InlineData(InteractiveTerminalState.Stopping, "stopping")]
    public void AdmissionPolicy_ExplainsEveryRejectedInteractiveState(InteractiveTerminalState state, string expectedReasonFragment)
    {
        var reason = EditorExecutionAdmissionPolicy.ExplainRejection(state);

        Assert.Contains(expectedReasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("\u001b[32mgreen\u001b[0m")]
    [InlineData("1%\r2%")]
    [InlineData("日本語")]
    [InlineData("line1\nline2\n")]
    public void RendererBridge_PreservesVisiblePayloadVariants(string payload)
    {
        var bridge = new TerminalRendererBridge(
            new TerminalOutputFlowController(maximumPendingCharacters: 128, maximumBatchCharacters: 128));
        bridge.StartRenderer(2);
        bridge.MarkRendererReady(2);

        bridge.Submit(Envelope(1, TerminalOutputSource.StructuredEditor, Guid.NewGuid(), 1, 2, 2, payload));
        var batch = Assert.IsType<TerminalOutputBatch>(bridge.TryBeginDelivery());

        Assert.Equal(payload, batch.Data);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void RendererBridge_ReadySignalIsGenerationScoped(int readyGeneration, bool expectedReady)
    {
        var bridge = new TerminalRendererBridge();
        bridge.StartRenderer(1);

        var scheduled = bridge.MarkRendererReady(readyGeneration);

        Assert.False(scheduled);
        Assert.Equal(expectedReady ? TerminalRendererLifecycle.Ready : TerminalRendererLifecycle.Starting, bridge.Lifecycle);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("normal user text", false)]
    [InlineData("##PSSTUDIO_EXEC_START_1", true)]
    [InlineData("hidden-helper.ps1", true)]
    public void PrivateProtocolDetection_IsExplicit(string? payload, bool expected)
    {
        Assert.Equal(expected, TerminalOutputMultiplexer.ContainsPrivateProtocol(payload));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    public void FeatureGate_DefaultsOnAndCanBeDisabledOnlyExplicitly(string? value, bool expected)
    {
        var previous = Environment.GetEnvironmentVariable(EditorExecutionFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(EditorExecutionFeatureGate.EnvironmentVariableName, value);

            Assert.Equal(expected, EditorExecutionFeatureGate.FromEnvironment().IsStructuredExecutionEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EditorExecutionFeatureGate.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void RendererBridge_StartReadySubmitAcknowledgeUsesGenerationScopedFlow()
    {
        var bridge = new TerminalRendererBridge(
            new TerminalOutputFlowController(maximumPendingCharacters: 32, maximumBatchCharacters: 16));
        var requestId = Guid.NewGuid();

        bridge.StartRenderer(11);
        Assert.False(bridge.MarkRendererReady(11));
        Assert.Equal(TerminalRendererLifecycle.Ready, bridge.Lifecycle);
        var enqueue = bridge.Submit(Envelope(1, TerminalOutputSource.StructuredEditor, requestId, 3, 7, 11, "hello"));
        var batch = Assert.IsType<TerminalOutputBatch>(bridge.TryBeginDelivery());

        Assert.True(enqueue.ScheduleFlush);
        Assert.Equal(11, batch.Generation);
        Assert.Equal("hello", batch.Data);
        Assert.False(bridge.Acknowledge(10, batch.Sequence));
        Assert.False(bridge.Acknowledge(11, batch.Sequence));
    }

    [Fact]
    public void RendererBridge_RejectsStaleRendererOutputAndAcknowledgement()
    {
        var bridge = new TerminalRendererBridge(
            new TerminalOutputFlowController(maximumPendingCharacters: 32, maximumBatchCharacters: 16));

        bridge.StartRenderer(4);
        bridge.MarkRendererReady(4);
        bridge.StartRenderer(5);
        bridge.MarkRendererReady(5);

        var stale = bridge.Submit(Envelope(1, TerminalOutputSource.InteractiveTerminal, null, 0, 4, 4, "old"));
        var current = bridge.Submit(Envelope(2, TerminalOutputSource.InteractiveTerminal, null, 0, 5, 5, "new"));
        var batch = Assert.IsType<TerminalOutputBatch>(bridge.TryBeginDelivery());

        Assert.Equal(3, stale.RejectedStaleCharacters);
        Assert.Equal(3, current.AcceptedCharacters);
        Assert.Equal("new", batch.Data);
        Assert.False(bridge.Acknowledge(4, batch.Sequence));
    }

    [Fact]
    public void RendererBridge_RendererFailureDropsOutputWithoutMutatingBrokerIdentity()
    {
        var bridge = new TerminalRendererBridge(
            new TerminalOutputFlowController(maximumPendingCharacters: 16, maximumBatchCharacters: 8));

        bridge.StartRenderer(8);
        bridge.MarkRendererReady(8);
        bridge.Submit(Envelope(1, TerminalOutputSource.StructuredEditor, Guid.NewGuid(), 12, 8, 8, "queued"));
        var failed = bridge.MarkRendererUnavailable(8);
        var later = bridge.Submit(Envelope(2, TerminalOutputSource.StructuredEditor, Guid.NewGuid(), 12, 8, 8, "later"));

        Assert.True(failed.DiscardedCharacters > 0);
        Assert.Equal(TerminalRendererLifecycle.Failed, bridge.Lifecycle);
        Assert.Equal(5, later.DroppedCharacters);
    }

    [Fact]
    public void RendererBridge_BoundsLargeCombinedOutput()
    {
        var bridge = new TerminalRendererBridge(
            new TerminalOutputFlowController(maximumPendingCharacters: 10, maximumBatchCharacters: 5));

        bridge.StartRenderer(1);
        bridge.MarkRendererReady(1);
        var first = bridge.Submit(Envelope(1, TerminalOutputSource.InteractiveTerminal, null, 0, 1, 1, "12345"));
        var second = bridge.Submit(Envelope(2, TerminalOutputSource.StructuredEditor, Guid.NewGuid(), 1, 1, 1, "67890"));
        var dropped = bridge.Submit(Envelope(3, TerminalOutputSource.StructuredEditor, Guid.NewGuid(), 1, 1, 1, "x"));

        Assert.True(first.ScheduleFlush);
        Assert.Equal(5, second.AcceptedCharacters);
        Assert.Equal(1, dropped.DroppedCharacters);
    }

    [Fact]
    public void RendererBridge_RejectsPrivateStructuredProtocolBeforeFlowControl()
    {
        var bridge = new TerminalRendererBridge();
        bridge.StartRenderer(1);
        bridge.MarkRendererReady(1);

        Assert.Throws<InvalidOperationException>(() =>
            bridge.Submit(Envelope(1, TerminalOutputSource.StructuredEditor, Guid.NewGuid(), 1, 1, 1, "##PSSTUDIO_EXEC_DONE_x")));
    }

    [Fact]
    public void MainWindowViewModel_SourceRoutesStructuredRunBehindFeatureGateOnly()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("_editorExecutionFeatureGate.IsStructuredExecutionEnabled", source, StringComparison.Ordinal);
        Assert.Contains("DispatchStructuredEditorExecutionAsync", source, StringComparison.Ordinal);
        Assert.Contains("No legacy fallback was attempted", source, StringComparison.Ordinal);
        Assert.Contains("fallbackAttempted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_UsesBoundedPsesAsTheOnlyTerminalPresentationSubscriber()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("EnableBoundedPsesTerminalIngress();", source, StringComparison.Ordinal);
        Assert.Contains("awaitableSource.VisibleOutputPublishingAsync += OnBoundedPsesTerminalOutputAsync;", source, StringComparison.Ordinal);
        Assert.Contains("TerminalConsole.WriteRawAwaitableAsync(sessionGeneration, text)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubscribeRawOutput", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalOutputPublished", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DrainTerminalOutputForRenderer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectedTerminalOutputOwner", source, StringComparison.Ordinal);
    }
    [Fact]
    public void AppBootstrapper_AttachesViewModelThroughMainWindowOwnershipMethod()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "Composition", "AppBootstrapper.cs");

        Assert.Contains("window.AttachViewModel(viewModel)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext = viewModel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowViewModel_LeavesInteractiveAdmissionUntouchedWhenRendererBecomesReady()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var rendererReadyIndex = source.IndexOf("public void NotifyTerminalRendererReady()", StringComparison.Ordinal);
        var rendererReadyBlock = source[rendererReadyIndex..Math.Min(source.Length, rendererReadyIndex + 1800)];

        Assert.Contains("presentation-only", rendererReadyBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReplaceGeneration", rendererReadyBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("InteractiveTerminalState.InteractiveIdleAtPrompt,", rendererReadyBlock, StringComparison.Ordinal);
        Assert.Contains("OnTerminalPromptReadyObserved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalControl_SourceKeepsResizeOutputIndependent()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");
        var resizeIndex = source.IndexOf("TerminalResized?.Invoke", StringComparison.Ordinal);

        Assert.True(resizeIndex >= 0);
        Assert.DoesNotContain("WriteStructuredOutput", source[resizeIndex..Math.Min(source.Length, resizeIndex + 600)], StringComparison.Ordinal);
        Assert.DoesNotContain("WriteVisibleOutput", source[resizeIndex..Math.Min(source.Length, resizeIndex + 600)], StringComparison.Ordinal);
    }

    private static TerminalOutputEnvelope Envelope(
        long sequence,
        TerminalOutputSource source,
        Guid? requestId,
        int brokerGeneration,
        int terminalGeneration,
        int rendererGeneration,
        string payload) => new(
            sequence,
            source,
            requestId,
            brokerGeneration,
            terminalGeneration,
            rendererGeneration,
            sequence,
            EditorOutputStreamKind.VirtualTerminal,
            payload,
            DateTimeOffset.UtcNow);

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
