using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesAvDeterministicTransportTests
{
    [Fact]
    public async Task IdlePipeCloseInvalidatesExactlyOneGeneration()
    {
        await using var fixture = await ConnectedFixture.CreateAsync();
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultCount = 0;
        fixture.Client.SessionFaulted += exception =>
        {
            Interlocked.Increment(ref faultCount);
            fault.TrySetResult(exception);
        };

        fixture.CloseServer();
        var observed = await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<EndOfStreamException>(observed);
        Assert.Equal(1, Volatile.Read(ref faultCount));
        Assert.False(fixture.Client.IsInitialized);
        Assert.Equal(17, fixture.Client.SessionGeneration);
    }

    [Fact]
    public async Task PendingRequestSettlesOnceWhenPipeClosesDuringEvaluation()
    {
        await using var fixture = await ConnectedFixture.CreateAsync();
        var faultCount = 0;
        fixture.Client.SessionFaulted += _ => Interlocked.Increment(ref faultCount);

        var evaluation = fixture.Client.EvaluateAsync(Guid.NewGuid(), "Start-Sleep -Seconds 30");
        await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Client.PendingRequestCount);

        fixture.CloseServer();
        var result = await evaluation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(EditorExecutionStatus.Failed, result.Status);
        Assert.False(result.ResponseReceived);
        Assert.Equal(0, fixture.Client.PendingRequestCount);
        Assert.Equal(1, Volatile.Read(ref faultCount));
    }

    [Fact]
    public async Task CancellationDeliversProtocolCancelBeforeRequestCorrelationIsRemoved()
    {
        await using var fixture = await ConnectedFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var requestId = Guid.NewGuid();
        var evaluation = fixture.Client.EvaluateAsync(requestId, "Start-Sleep -Seconds 30", cancellation.Token);
        using var request = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        using var cancel = await fixture.ReadNextServerMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("$/cancelRequest", cancel.RootElement.GetProperty("method").GetString());
        Assert.Equal(request.RootElement.GetProperty("id").GetInt32(), cancel.RootElement.GetProperty("params").GetProperty("id").GetInt32());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.Equal(0, fixture.Client.PendingRequestCount);
    }

    [Fact]
    public async Task LiveCompletionSynchronizesDocumentAndParsesLspItems()
    {
        await using var fixture = await ConnectedFixture.CreateAsync();
        var completionTask = fixture.Client.GetLiveCompletionsAsync(
            "untitled:ps7scriptdesk/probe.ps1",
            "$ISEP",
            5);

        using var didOpen = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("textDocument/didOpen", didOpen.RootElement.GetProperty("method").GetString());
        Assert.Equal("$ISEP", didOpen.RootElement.GetProperty("params").GetProperty("textDocument").GetProperty("text").GetString());

        using var completionRequest = await fixture.ReadNextServerMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("textDocument/completion", completionRequest.RootElement.GetProperty("method").GetString());
        Assert.Equal(0, completionRequest.RootElement.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(5, completionRequest.RootElement.GetProperty("params").GetProperty("position").GetProperty("character").GetInt32());

        await fixture.WriteCompletionResponseAsync(completionRequest.RootElement.GetProperty("id").GetInt32());
        var result = await completionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal(0, result!.ReplacementIndex);
        Assert.Equal(5, result.ReplacementLength);
        Assert.Contains(result.Items, item => item.CompletionText == "$ISEProbe");
    }

    private sealed class ConnectedFixture : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _clientStream;
        private bool _closed;

        private ConnectedFixture(NamedPipeServerStream server, NamedPipeClientStream clientStream, PsesLanguageServiceClient client, Task<JsonDocument> serverReadTask)
        {
            _server = server;
            _clientStream = clientStream;
            Client = client;
            ServerReadTask = serverReadTask;
        }

        public PsesLanguageServiceClient Client { get; }
        public Task<JsonDocument> ServerReadTask { get; }

        public Task<JsonDocument> ReadNextServerMessageAsync() => ReadFrameAsync(_server);

        public async Task WriteCompletionResponseAsync(int id)
        {
            var body = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"isIncomplete\":false,\"items\":[{{\"label\":\"$ISEProbe\",\"insertText\":\"$ISEProbe\",\"kind\":6,\"detail\":\"live variable\",\"textEdit\":{{\"range\":{{\"start\":{{\"line\":0,\"character\":0}},\"end\":{{\"line\":0,\"character\":5}}}},\"newText\":\"$ISEProbe\"}}}}]}}}}");
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _server.WriteAsync(header);
            await _server.WriteAsync(body);
            await _server.FlushAsync();
        }

        public async Task SendNotificationAsync(string method, object parameters)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters });
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _server.WriteAsync(header);
            await _server.WriteAsync(body);
            await _server.FlushAsync();
        }

        public static async Task<ConnectedFixture> CreateAsync()
        {
            var pipeName = $"ps7sd-av-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connect = clientStream.ConnectAsync(5000);
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await connect.WaitAsync(TimeSpan.FromSeconds(5));

            var client = new PsesLanguageServiceClient();
            var initialize = ReadFrameAsync(server);
            var clientInitialization = client.InitializeConnectedAsync(clientStream, 1234, 17, (_, _) => Task.CompletedTask);
            using var initializeMessage = await initialize.WaitAsync(TimeSpan.FromSeconds(5));
            await WriteResponseAsync(server, initializeMessage.RootElement.GetProperty("id").GetInt32());
            await ReadFrameAsync(server); // initialized notification
            await ReadFrameAsync(server); // workspace/didChangeConfiguration notification
            await clientInitialization.WaitAsync(TimeSpan.FromSeconds(5));
            return new ConnectedFixture(server, clientStream, client, ReadFrameAsync(server));
        }

        public void CloseServer()
        {
            if (_closed) return;
            _closed = true;
            _server.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            CloseServer();
            await Client.DisposeAsync();
            _clientStream.Dispose();
        }

        private static async Task<JsonDocument> ReadFrameAsync(Stream stream)
        {
            var header = new List<byte>();
            while (true)
            {
                var one = new byte[1];
                var count = await stream.ReadAsync(one);
                if (count == 0) throw new EndOfStreamException();
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4..].SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }

            var headerText = Encoding.ASCII.GetString(header.ToArray());
            var length = int.Parse(headerText.Split('\n').Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim());
            var body = new byte[length];
            for (var offset = 0; offset < body.Length;)
                offset += await stream.ReadAsync(body.AsMemory(offset));
            return JsonDocument.Parse(body);
        }

        private static async Task WriteResponseAsync(Stream stream, int id)
        {
            var body = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
    }
}
