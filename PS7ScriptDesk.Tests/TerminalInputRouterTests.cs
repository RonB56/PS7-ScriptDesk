using System.Text;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalInputRouterTests
{
    [Fact]
    public async Task ConcurrentPayloads_AreWrittenInEnqueueOrderWithoutInterleaving()
    {
        var writer = new BlockingFirstWriteTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(7, writer);

        var first = router.WriteAsync(7, "first");
        await writer.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = router.WriteAsync(7, "second");

        Assert.False(second.IsCompleted);
        Assert.Equal(string.Empty, writer.Text);

        writer.ReleaseFirstWrite.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("firstsecond", writer.Text);
        Assert.True(await router.DeactivateAsync(7, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Deactivate_CancelsQueuedWritesAndRejectsStaleGeneration()
    {
        var oldWriter = new BlockingFirstWriteTextWriter();
        var newWriter = new RecordingTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(11, oldWriter);

        var oldWrite = router.WriteAsync(11, "old-session");
        await oldWriter.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await router.DeactivateAsync(11, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWrite);

        router.Activate(12, newWriter);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.WriteAsync(11, "stale"));
        await router.WriteAsync(12, "new-session");

        Assert.Equal("new-session", newWriter.Text);
        Assert.DoesNotContain("old-session", newWriter.Text, StringComparison.Ordinal);
        Assert.True(await router.DeactivateAsync(12, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task WriteFailure_IsSurfacedAndDoesNotPoisonLaterPayloads()
    {
        var writer = new FailFirstWriteTextWriter();
        var router = new TerminalInputRouter();
        router.Activate(19, writer);

        var failed = router.WriteAsync(19, "fail");
        var succeeded = router.WriteAsync(19, "after");

        var exception = await Assert.ThrowsAsync<IOException>(() => failed);
        Assert.Contains("Terminal input write", exception.Message, StringComparison.Ordinal);
        await succeeded.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("after", writer.Text);
        Assert.True(await router.DeactivateAsync(19, TimeSpan.FromSeconds(2)));
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

        public override async Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
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

    private sealed class RecordingTextWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text => _text.ToString();

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            _text.Append(buffer.Span);
            return Task.CompletedTask;
        }
    }

    private sealed class FailFirstWriteTextWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        private int _writeCount;

        public override Encoding Encoding => Encoding.UTF8;

        public string Text => _text.ToString();

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                throw new IOException("Injected writer failure.");
            }

            _text.Append(buffer.Span);
            return Task.CompletedTask;
        }
    }
}
