using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputBridgeTests
{
    [Fact]
    public void FlowController_DefersOutputUntilRendererIsReady_ThenPreservesChunkOrder()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 64,
            maximumBatchCharacters: 16);

        Assert.False(controller.Enqueue("first").ScheduleFlush);
        Assert.False(controller.Enqueue("\x1b[32msecond\x1b[0m").ScheduleFlush);
        Assert.True(controller.SetRendererReady());

        var firstBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("first\x1b[32msecond", firstBatch.Data);
        Assert.Null(controller.TryBeginDelivery());
        Assert.True(controller.Acknowledge(firstBatch.Sequence));

        var secondBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("\x1b[0m", secondBatch.Data);
        Assert.False(controller.Acknowledge(secondBatch.Sequence));
        Assert.Null(controller.TryBeginDelivery());
    }

    [Fact]
    public void FlowController_BoundsPendingOutputAndDropsOnlyOverloadChunks()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 8,
            maximumBatchCharacters: 4);

        Assert.False(controller.Enqueue("abcd").ScheduleFlush);
        Assert.True(controller.SetRendererReady());
        var firstBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());

        var queued = controller.Enqueue("efgh");
        var dropped = controller.Enqueue("ij");

        Assert.False(queued.ScheduleFlush);
        Assert.Equal(0, queued.DroppedCharacters);
        Assert.Equal(2, dropped.DroppedCharacters);
        Assert.Equal(8, dropped.PendingCharacters);

        Assert.True(controller.Acknowledge(firstBatch.Sequence));
        var secondBatch = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("efgh", secondBatch.Data);
        Assert.False(controller.Acknowledge(firstBatch.Sequence));
        Assert.False(controller.Acknowledge(secondBatch.Sequence));
    }

    [Fact]
    public void FlowController_SplitsLargeChunkWithoutReorderingLaterChunks()
    {
        var controller = new TerminalOutputFlowController(
            maximumPendingCharacters: 32,
            maximumBatchCharacters: 4);

        controller.Enqueue("abcdef");
        controller.Enqueue("gh");
        Assert.True(controller.SetRendererReady());

        var first = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("abcd", first.Data);
        Assert.True(controller.Acknowledge(first.Sequence));

        var second = Assert.IsType<TerminalOutputBatch>(controller.TryBeginDelivery());
        Assert.Equal("efgh", second.Data);
        Assert.False(controller.Acknowledge(second.Sequence));
    }

    [Fact]
    public void WebMessageSerializer_Base64RoundTripsUnicodeAndIncludesAcknowledgementSequence()
    {
        const string terminalData = "αβ\r\n\x1b[31mred\x1b[0m";

        var json = TerminalWebMessageSerializer.SerializeOutput(42, terminalData);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("output_b64", root.GetProperty("type").GetString());
        Assert.Equal(42, root.GetProperty("sequence").GetInt64());
        var encoded = root.GetProperty("data").GetString();
        Assert.NotNull(encoded);
        Assert.Equal(
            terminalData,
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }
}
