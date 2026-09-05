using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalInputCoordinatorTests
{
    [Theory]
    [InlineData("\u001b[I")]
    [InlineData("\u001b[O")]
    public async Task FocusProtocolInput_IsForwardedWithoutClaimingEditableOwnership(string input)
    {
        var writer = new RecordingTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(6, writer);
        using var coordinator = new TerminalInputCoordinator(router);
        coordinator.BeginSession(6);
        coordinator.ObservePromptReady(6);

        coordinator.ObserveUserInput(input, 6);
        Assert.True(coordinator.CanAcceptInternalDispatch(6, out var reason), reason);
        await coordinator.WriteAsync(6, input, TerminalInputOrigin.UserInteractive);

        Assert.Equal(input, writer.Text);
        Assert.True(await router.DeactivateAsync(6, TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("\u001b[I", "FocusIn")]
    [InlineData("\u001b[O", "FocusOut")]
    [InlineData("Get-Date", "PrintableText")]
    [InlineData("\u001b[A", "ArrowUp")]
    public void ClassifierUsesExplicitSemanticCategories(string input, string expected)
    {
        Assert.Equal(expected, TerminalInputClassifier.Classify(input));
    }

    [Fact]
    public async Task UserInteractiveInput_IsForwardedUnchangedAfterPromptReady()
    {
        var writer = new RecordingTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(7, writer);
        using var coordinator = new TerminalInputCoordinator(router);
        coordinator.BeginSession(7);
        coordinator.ObservePromptReady(7);

        await coordinator.WriteAsync(7, "Get-Date\r", TerminalInputOrigin.UserInteractive);

        Assert.Equal("Get-Date\r", writer.Text);
        Assert.True(await router.DeactivateAsync(7, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task InternalDispatch_IsRejectedWhenUserOwnsAnUnsubmittedLine()
    {
        var writer = new RecordingTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(8, writer);
        using var coordinator = new TerminalInputCoordinator(router);
        coordinator.BeginSession(8);
        coordinator.ObservePromptReady(8);
        coordinator.ObserveUserInput("Get-Pro", 8);

        Assert.False(coordinator.CanAcceptInternalDispatch(8, out var reason));
        Assert.Contains("unfinished", reason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WriteAsync(
            8,
            "& 'dispatch.ps1' # PS7ScriptDesk.InternalDispatch\r",
            TerminalInputOrigin.InternalDispatch));

        Assert.Equal(string.Empty, writer.Text);
        Assert.True(await router.DeactivateAsync(8, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task InternalSubmission_IsAtomicBeforeUserInputArrivingDuringTheWrite()
    {
        var writer = new BlockingFirstWriteTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(9, writer);
        using var coordinator = new TerminalInputCoordinator(router);
        coordinator.BeginSession(9);
        coordinator.ObservePromptReady(9);

        var internalWrite = coordinator.WriteAsync(
            9,
            "& 'dispatch.ps1' # PS7ScriptDesk.InternalDispatch\r",
            TerminalInputOrigin.InternalDispatch);
        await writer.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var userWrite = coordinator.WriteAsync(9, "Get-Date\r", TerminalInputOrigin.UserInteractive);

        Assert.False(userWrite.IsCompleted);
        writer.ReleaseFirstWrite.TrySetResult();
        await Task.WhenAll(internalWrite, userWrite).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("& 'dispatch.ps1' # PS7ScriptDesk.InternalDispatch\rGet-Date\r", writer.Text);
        Assert.True(await router.DeactivateAsync(9, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SessionRestart_ClearsConservativeEditOwnership()
    {
        var router = new TerminalInputRouter();
        using var coordinator = new TerminalInputCoordinator(router);
        coordinator.BeginSession(10);
        coordinator.ObservePromptReady(10);
        coordinator.ObserveUserInput("Get-Pro", 10);
        Assert.False(coordinator.CanAcceptInternalDispatch(10, out _));

        coordinator.EndSession(10);
        coordinator.BeginSession(11);
        coordinator.ObservePromptReady(11);

        Assert.True(coordinator.CanAcceptInternalDispatch(11, out var reason), reason);
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text => _text.ToString();

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            _text.Append(buffer.Span);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingFirstWriteTextWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        private int _writeCount;

        public override Encoding Encoding => Encoding.UTF8;

        public TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                FirstWriteStarted.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
            }

            lock (_text)
            {
                _text.Append(buffer.Span);
            }
        }
    }
}
