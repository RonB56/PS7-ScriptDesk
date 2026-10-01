using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Tests.Dap;

namespace PS7ScriptDesk.Tests;

public sealed class B1DapFramingTests
{
    [Fact]
    public void EncodeUsesUtf8ByteCount()
    {
        var body = new { seq = 1, type = "event", @event = "output", body = new { output = "Café 東京 🚀" } };
        var encoded = DapMessageFramer.Encode(body);
        var header = Encoding.ASCII.GetString(encoded).Split("\r\n\r\n", 2, StringSplitOptions.None)[0];
        var expected = JsonSerializer.SerializeToUtf8Bytes(body).Length;
        Assert.Equal($"Content-Length: {expected}", header);
    }

    [Fact]
    public void ParserHandlesByteByByteHeaderAndBody()
    {
        var encoded = DapMessageFramer.Encode(new { seq = 2, type = "event", @event = "output", body = new { output = "é東京" } });
        var framer = new DapMessageFramer();
        DapMessage? message = null;
        foreach (var value in encoded)
        {
            framer.Append(new[] { value });
            if (framer.TryRead(out var parsed)) message = parsed;
        }

        Assert.NotNull(message);
        Assert.Equal("event", message!.Type);
        Assert.Equal("output", message.Event);
        Assert.Equal("é東京", message.Body!.Value.GetProperty("output").GetString());
    }

    [Fact]
    public void ParserHandlesDelimiterSplitAndMultipleMessages()
    {
        var first = DapMessageFramer.Encode(new { seq = 1, type = "event", @event = "one" });
        var second = DapMessageFramer.Encode(new { seq = 2, type = "event", @event = "two" });
        var combined = first.Concat(second).ToArray();
        var framer = new DapMessageFramer();
        framer.Append(combined[..7]);
        Assert.False(framer.TryRead(out _));
        framer.Append(combined[7..]);
        Assert.True(framer.TryRead(out var firstMessage));
        Assert.True(framer.TryRead(out var secondMessage));
        Assert.False(framer.TryRead(out _));
        Assert.Equal("one", firstMessage!.Event);
        Assert.Equal("two", secondMessage!.Event);
    }

    [Theory]
    [InlineData("Content-Length: nope\r\n\r\n{}")] 
    [InlineData("Content-Length: -1\r\n\r\n{}")] 
    [InlineData("X-Test: 1\r\n\r\n{}")] 
    public void ParserRejectsMalformedHeaders(string text)
    {
        var framer = new DapMessageFramer();
        framer.Append(Encoding.ASCII.GetBytes(text));
        Assert.Throws<InvalidDataException>(() => framer.TryRead(out _));
    }

    [Fact]
    public void ParserRejectsMalformedJson()
    {
        var framer = new DapMessageFramer();
        framer.Append(DapMessageFramer.Encode(Encoding.UTF8.GetBytes("{bad")));
        Assert.Throws<InvalidDataException>(() => framer.TryRead(out _));
    }

    [Fact]
    public void ParserRejectsOversizedPayload()
    {
        var framer = new DapMessageFramer(8);
        framer.Append(Encoding.ASCII.GetBytes("Content-Length: 9\r\n\r\n123456789"));
        Assert.Throws<InvalidDataException>(() => framer.TryRead(out _));
    }
}

public sealed class B1DapRequestTrackerTests
{
    [Fact]
    public async Task ResponsesCorrelateByRequestSequenceAndUnknownResponsesDoNotCompleteAnything()
    {
        var tracker = new DapRequestTracker();
        using var cancellation = new CancellationTokenSource();
        var pending = tracker.Add(11, cancellation.Token);
        var unknown = tracker.Complete(new DapMessage(2, "response", "other", 99, true, null, null, null, null));
        Assert.False(unknown);
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.Complete(new DapMessage(3, "response", "initialize", 11, true, null, null, null, null)));
        Assert.Equal("initialize", (await pending).Command);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public async Task DisconnectFailsAllPendingRequests()
    {
        var tracker = new DapRequestTracker();
        var pending = tracker.Add(1, CancellationToken.None);
        tracker.FailAll(new IOException("closed"));
        var exception = await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Equal("closed", exception.Message);
        Assert.Equal(0, tracker.Count);
    }
}

public sealed class B1PsesHostIntegrationTests
{
    [Fact(Timeout = 180_000)]
    public async Task StartsPsesValidatesSessionDetailsConnectsAndInitializesDap()
    {
        PsesHostProcess host;
        try
        {
            host = await PsesHostProcess.StartAsync(
                Environment.GetEnvironmentVariable("PS7SD_B1_PSES_ROOT"),
                TimeSpan.FromSeconds(90));
        }
        catch (FileNotFoundException ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip(ex.Message);
        }

        await using (host)
        await using (var connection = await host.ConnectDebugPipeAsync(TimeSpan.FromSeconds(15)))
        {
            var response = await connection.SendRequestAsync(
                "initialize",
                new
                {
                    adapterID = "PS7ScriptDesk-B1",
                    clientID = "PS7ScriptDesk-B1",
                    clientName = "PS7 ScriptDesk B1 Harness",
                    linesStartAt1 = true,
                    columnsStartAt1 = true,
                    pathFormat = "path",
                    supportsVariablePaging = false,
                    supportsRunInTerminalRequest = false
                },
                TimeSpan.FromSeconds(15));

            Assert.True(response.Success);
            Assert.Equal("initialize", response.Command);
            Assert.Equal("response", response.Type);
            Assert.True(response.Body.HasValue);

            var disconnect = await connection.SendRequestAsync(
                "disconnect",
                new { terminateDebuggee = true, restart = false },
                TimeSpan.FromSeconds(15));
            Assert.True(disconnect.Success);
            Assert.Equal("disconnect", disconnect.Command);
        }
    }
}
