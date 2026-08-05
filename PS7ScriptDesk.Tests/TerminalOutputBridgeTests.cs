using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputBridgeTests
{
    [Fact]
    public void BatchBuffer_CoalescesChunksInArrivalOrder_WithOneScheduledFlush()
    {
        var buffer = new TerminalOutputBatchBuffer();

        Assert.True(buffer.Enqueue("first"));
        Assert.False(buffer.Enqueue("\x1b[32msecond\x1b[0m"));
        Assert.False(buffer.Enqueue("\r\nthird"));

        Assert.Equal("first\x1b[32msecond\x1b[0m\r\nthird", buffer.Drain());
    }

    [Fact]
    public void BatchBuffer_DrainAllowsNextBatchToScheduleAndDoesNotReplayOldOutput()
    {
        var buffer = new TerminalOutputBatchBuffer();

        Assert.False(buffer.Enqueue(string.Empty));
        Assert.True(buffer.Enqueue("one"));
        Assert.Equal("one", buffer.Drain());
        Assert.Equal(string.Empty, buffer.Drain());

        Assert.True(buffer.Enqueue("two"));
        Assert.Equal("two", buffer.Drain());
    }

    [Fact]
    public void WebMessageSerializer_Base64RoundTripsUnicodeAndTerminalControlData()
    {
        const string terminalData = "αβ\r\n\x1b[31mred\x1b[0m";

        var json = TerminalWebMessageSerializer.Serialize("output", terminalData);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("output_b64", root.GetProperty("type").GetString());
        var encoded = root.GetProperty("data").GetString();
        Assert.NotNull(encoded);
        Assert.Equal(
            terminalData,
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }
}
