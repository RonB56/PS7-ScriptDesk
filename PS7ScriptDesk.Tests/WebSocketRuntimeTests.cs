using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.Hosting;
using PS7ScriptDesk.RestApiProofHost.WebSockets;

namespace PS7ScriptDesk.Tests;

public sealed class WebSocketRuntimeTests : IDisposable
{
    private const string ApiKeyValue = "PHASE12_WEBSOCKET_API_KEY";
    private static readonly string[] WebSocketEndpointIds =
    [
        "poc-post-systeminfo",
        "poc-phase4-delay",
        "poc-live-timing",
        "poc-live-cancellation",
        "poc-test-failure",
        "poc-phase4-timeout"
    ];

    private readonly string _apiKeyVariableName = $"PS7API_PHASE12_WS_{Guid.NewGuid():N}";
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk WebSocket Runtime {Guid.NewGuid():N}");

    public WebSocketRuntimeTests()
    {
        Directory.CreateDirectory(_testDirectory);
        Environment.SetEnvironmentVariable(_apiKeyVariableName, ApiKeyValue);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_apiKeyVariableName, null);
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task WebSocketInvocation_StreamsContractEventsInOrderAndAllowsSequentialInvocations()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "poc-post-systeminfo");

        await SendInvokeAsync(socket, "req-001", "poc-post-systeminfo", new { computerName = "SERVER01" });
        var firstEvents = await ReadEventsUntilTerminalAsync(socket, "req-001");

        AssertCompletedSystemInfoEvents(firstEvents, "req-001", "SERVER01");

        using var client = host.CreateClient();
        using var restAttempt = await client.PostAsJsonAsync("/api/systeminfo", new { computerName = "SERVER01" });
        Assert.NotEqual(HttpStatusCode.OK, restAttempt.StatusCode);

        using var discovery = await client.GetAsync("/api/endpoints");
        var discoveryText = await discovery.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.Contains("/ws/poc-post-systeminfo", discoveryText, StringComparison.Ordinal);
        Assert.Contains(ApiTransport.WebSocket.ToString(), discoveryText, StringComparison.Ordinal);

        await SendInvokeAsync(socket, "req-002", "poc-post-systeminfo", new { computerName = "SERVER02" });
        var secondEvents = await ReadEventsUntilTerminalAsync(socket, "req-002");

        AssertCompletedSystemInfoEvents(secondEvents, "req-002", "SERVER02");
        Assert.NotEqual(
            firstEvents[0].Payload.GetProperty("invocationId").GetString(),
            secondEvents[0].Payload.GetProperty("invocationId").GetString());
    }

    [Fact]
    public async Task WebSocketInvocation_ReceivesPowerShellOutputBeforeInvocationCompletes()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "poc-live-timing");

        await SendInvokeAsync(socket, "live-before-terminal", "poc-live-timing", new { });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var events = new List<ReceivedWebSocketEnvelope>();
        ReceivedWebSocketEnvelope firstOutput;
        while (true)
        {
            var envelope = await ReceiveEnvelopeAsync(socket);
            Assert.Equal("live-before-terminal", envelope.RequestId);
            Assert.Equal(WebSocketMessageTypes.Event, envelope.Type);
            events.Add(envelope);
            if (envelope.Payload.GetProperty("eventType").GetString() == "Output")
            {
                firstOutput = envelope;
                break;
            }
        }

        Assert.Equal("first", firstOutput.Payload.GetProperty("payload").GetString());
        Assert.False(firstOutput.Payload.GetProperty("terminal").GetBoolean());
        Assert.True(stopwatch.ElapsedMilliseconds < 1000);
        Assert.True(host.Metrics.ActiveInvocationCount > 0);

        events.AddRange(await ReadEventsUntilTerminalAsync(socket, "live-before-terminal"));

        Assert.Contains(events, item => item.Payload.GetProperty("eventType").GetString() == "Output" &&
            item.Payload.GetProperty("payload").GetString() == "second");
        Assert.Contains(events, item => item.Payload.GetProperty("eventType").GetString() == "Output" &&
            item.Payload.GetProperty("payload").GetString() == "third");
        Assert.Equal("InvocationCompleted", events[^1].Payload.GetProperty("eventType").GetString());
        Assert.True(events.Select(item => item.Payload.GetProperty("sequence").GetInt64()).SequenceEqual(
            Enumerable.Range(1, events.Count).Select(index => (long)index)));
    }

    [Fact]
    public async Task WebSocketInvocation_InvalidParametersReturnSanitizedErrorAndConnectionCanContinue()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "poc-post-systeminfo");

        await SendInvokeAsync(socket, "bad-params", "poc-post-systeminfo", new { other = "SERVER01" });
        var error = await ReceiveEnvelopeAsync(socket);

        Assert.Equal(WebSocketMessageTypes.Error, error.Type);
        Assert.Equal("bad-params", error.RequestId);
        Assert.Equal(WebSocketErrorCategories.Parameter, error.Payload.GetProperty("category").GetString());
        Assert.Equal(WebSocketProtocolErrorCodes.RequestValidationFailure, error.Payload.GetProperty("code").GetString());
        Assert.DoesNotContain("System.Management.Automation", error.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);

        await SendInvokeAsync(socket, "after-error", "poc-post-systeminfo", new { computerName = "SERVER02" });
        var events = await ReadEventsUntilTerminalAsync(socket, "after-error");

        AssertCompletedSystemInfoEvents(events, "after-error", "SERVER02");
    }

    [Fact]
    public async Task WebSocketInvocation_ClientCancellationProducesSingleCanceledTerminalEvent()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "poc-phase4-delay");

        await SendInvokeAsync(socket, "cancel-me", "poc-phase4-delay", new { requestId = "cancel-me", milliseconds = 1000 });
        var started = await ReceiveEnvelopeAsync(socket);
        Assert.Equal("InvocationStarted", started.Payload.GetProperty("eventType").GetString());

        await SendCancelAsync(socket, "cancel-me");
        var events = new List<ReceivedWebSocketEnvelope> { started };
        events.AddRange(await ReadEventsUntilTerminalAsync(socket, "cancel-me"));

        var terminalEvents = events.Where(item => item.Payload.GetProperty("terminal").GetBoolean()).ToList();
        Assert.Single(terminalEvents);
        Assert.Same(events[^1], terminalEvents[0]);
        Assert.Equal("InvocationCanceled", events[^1].Payload.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task WebSocketInvocation_PowerShellFailureAndTimeoutReturnSanitizedTerminalEvents()
    {
        await using var host = await StartHostAsync();
        using var failureSocket = await ConnectAsync(host, "poc-test-failure");

        await SendInvokeAsync(failureSocket, "fail-me", "poc-test-failure", new { });
        var failureEvents = await ReadEventsUntilTerminalAsync(failureSocket, "fail-me");

        Assert.Equal("InvocationFailed", failureEvents[^1].Payload.GetProperty("eventType").GetString());
        Assert.Equal("powershell-terminating-failure", failureEvents[^1].Payload.GetProperty("statusCode").GetString());
        Assert.DoesNotContain("Intentional test failure", failureEvents[^1].Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Management.Automation", failureEvents[^1].Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var timeoutSocket = await ConnectAsync(host, "poc-phase4-timeout");
        await SendInvokeAsync(timeoutSocket, "timeout-me", "poc-phase4-timeout", new { requestId = "timeout-me", milliseconds = 1000 });
        var timeoutEvents = await ReadEventsUntilTerminalAsync(timeoutSocket, "timeout-me");

        Assert.Equal("InvocationFailed", timeoutEvents[^1].Payload.GetProperty("eventType").GetString());
        Assert.Equal("invocation-timeout", timeoutEvents[^1].Payload.GetProperty("statusCode").GetString());
    }

    [Fact]
    public async Task WebSocketInvocation_ClientDisconnectCancelsActiveInvocation()
    {
        await using var host = await StartHostAsync();
        using var socket = await ConnectAsync(host, "poc-phase4-delay");

        await SendInvokeAsync(socket, "disconnect-me", "poc-phase4-delay", new { requestId = "disconnect-me", milliseconds = 1000 });
        await WaitForMetricAsync(() => host.Metrics.ActiveInvocationCount > 0);

        socket.Abort();

        await WaitForMetricAsync(() => host.Metrics.ActiveInvocationCount == 0 && host.Metrics.CallerCanceledCount > 0);
    }

    [Fact]
    public async Task WebSocketMessageSizeLimit_RejectsOversizedMessageAndCloses()
    {
        await using var host = await StartHostAsync();
        host.Configuration.Runtime.WebSocketMessageSizeLimitBytes = 96;
        using var socket = await ConnectAsync(host, "poc-post-systeminfo");

        await SendInvokeAsync(socket, "too-large", "poc-post-systeminfo", new { computerName = new string('x', 200) });
        var error = await ReceiveEnvelopeAsync(socket);

        Assert.Equal(WebSocketMessageTypes.ProtocolError, error.Type);
        Assert.Equal(WebSocketProtocolErrorCodes.MessageTooLarge, error.Payload.GetProperty("code").GetString());
        Assert.True(error.Payload.GetProperty("terminalConnection").GetBoolean());

        var buffer = new byte[128];
        var close = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.CloseStatus);
    }

    [Fact]
    public async Task WebSocketUpgrade_RequiresExistingApiAuthenticationBeforeAccept()
    {
        await using var host = await StartSecurityHostAsync();
        using var missingKeySocket = new ClientWebSocket();

        await Assert.ThrowsAsync<WebSocketException>(() =>
            missingKeySocket.ConnectAsync(CreateWebSocketUri(host, "poc-post-systeminfo"), CancellationToken.None));

        using var authenticatedSocket = new ClientWebSocket();
        authenticatedSocket.Options.SetRequestHeader(ApiKeyAuthenticationService.ApiKeyHeaderName, ApiKeyValue);
        await authenticatedSocket.ConnectAsync(CreateWebSocketUri(host, "poc-post-systeminfo"), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, authenticatedSocket.State);
    }

    [Fact]
    public async Task WebSocketUpgrade_UnknownEndpointIsRejectedBeforeAccept()
    {
        await using var host = await StartHostAsync();
        using var socket = new ClientWebSocket();

        await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(CreateWebSocketUri(host, "missing-endpoint"), CancellationToken.None));
    }

    private static void AssertCompletedSystemInfoEvents(
        IReadOnlyList<ReceivedWebSocketEnvelope> events,
        string requestId,
        string computerName)
    {
        Assert.NotEmpty(events);
        Assert.All(events, item =>
        {
            Assert.Equal(WebSocketMessageTypes.Event, item.Type);
            Assert.Equal(requestId, item.RequestId);
            Assert.Equal("poc-post-systeminfo", item.Payload.GetProperty("endpointId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.Payload.GetProperty("invocationId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.Payload.GetProperty("connectionId").GetString()));
        });

        Assert.Equal("InvocationStarted", events[0].Payload.GetProperty("eventType").GetString());
        Assert.Equal("InvocationCompleted", events[^1].Payload.GetProperty("eventType").GetString());
        Assert.True(events[^1].Payload.GetProperty("terminal").GetBoolean());
        Assert.Equal(1, events.Count(item => item.Payload.GetProperty("terminal").GetBoolean()));
        Assert.True(events.Select(item => item.Payload.GetProperty("sequence").GetInt64()).SequenceEqual(Enumerable.Range(1, events.Count).Select(index => (long)index)));

        var output = Assert.Single(events, item => item.Payload.GetProperty("eventType").GetString() == "Output");
        Assert.Equal(computerName, output.Payload.GetProperty("payload").GetProperty("ComputerName").GetString());
    }

    private async Task<RunningRestApiProofHost> StartHostAsync()
        => await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions
        {
            Url = "http://127.0.0.1:0",
            ContentRootPath = CreateTransportContentRoot(ApiTransport.WebSocket),
            ConfigurationRelativePath = Path.Combine("Config", "api.ps7api.json")
        });

    private async Task<RunningRestApiProofHost> StartSecurityHostAsync()
    {
        var contentRoot = CreateSecurityContentRoot();
        return await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions
        {
            Url = "http://127.0.0.1:0",
            ContentRootPath = contentRoot,
            ConfigurationRelativePath = Path.Combine("Config", "api.ps7api.json")
        });
    }

    private string CreateSecurityContentRoot()
    {
        var root = CreateTransportContentRoot(ApiTransport.WebSocket);
        var configuration = JsonSerializer.Deserialize<ApiPublishConfiguration>(
            File.ReadAllText(Path.Combine(root, "Config", "api.ps7api.json")),
            RestApiProofHostFactory.JsonOptions)!;
        configuration.Security.Mode = ApiSecurityMode.ApiKey;
        configuration.Security.ApiKeyEnvironmentVariableName = _apiKeyVariableName;
        configuration.Endpoints.First(endpoint => endpoint.EndpointId == "poc-post-systeminfo").RequiresAuthentication = true;
        File.WriteAllText(
            Path.Combine(root, "Config", "api.ps7api.json"),
            JsonSerializer.Serialize(configuration, RestApiProofHostFactory.JsonOptions),
            new UTF8Encoding(false));

        return root;
    }

    private string CreateTransportContentRoot(ApiTransport transport)
    {
        var root = Path.Combine(_testDirectory, Guid.NewGuid().ToString("N"));
        var scriptDirectory = Path.Combine(root, "Scripts");
        var configDirectory = Path.Combine(root, "Config");
        Directory.CreateDirectory(scriptDirectory);
        Directory.CreateDirectory(configDirectory);

        File.Copy(ResolveProofHostFile("Scripts", "TestApi.ps1"), Path.Combine(scriptDirectory, "TestApi.ps1"));
        var configuration = JsonSerializer.Deserialize<ApiPublishConfiguration>(
            File.ReadAllText(ResolveProofHostFile("Config", "TestApi.ps7api.json")),
            RestApiProofHostFactory.JsonOptions)!;
        foreach (var endpoint in configuration.Endpoints.Where(endpoint => WebSocketEndpointIds.Contains(endpoint.EndpointId, StringComparer.OrdinalIgnoreCase)))
        {
            endpoint.Transport = transport;
        }

        File.WriteAllText(
            Path.Combine(configDirectory, "api.ps7api.json"),
            JsonSerializer.Serialize(configuration, RestApiProofHostFactory.JsonOptions),
            new UTF8Encoding(false));

        return root;
    }

    private static async Task<ClientWebSocket> ConnectAsync(RunningRestApiProofHost host, string endpointId)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(CreateWebSocketUri(host, endpointId), CancellationToken.None);
        return socket;
    }

    private static Uri CreateWebSocketUri(RunningRestApiProofHost host, string endpointId)
    {
        var builder = new UriBuilder(host.BaseAddress)
        {
            Scheme = "ws",
            Path = $"/ws/{endpointId}"
        };
        return builder.Uri;
    }

    private static async Task SendInvokeAsync(ClientWebSocket socket, string requestId, string endpointId, object parameters)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = WebSocketProtocolV1.ProtocolName,
            protocolVersion = WebSocketProtocolV1.ProtocolVersion,
            type = WebSocketMessageTypes.Invoke,
            requestId,
            payload = new
            {
                endpointId,
                parameters
            }
        }, RestApiProofHostFactory.JsonOptions);

        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task SendCancelAsync(ClientWebSocket socket, string requestId)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = WebSocketProtocolV1.ProtocolName,
            protocolVersion = WebSocketProtocolV1.ProtocolVersion,
            type = WebSocketMessageTypes.Cancel,
            requestId,
            payload = new
            {
                reason = "test"
            }
        }, RestApiProofHostFactory.JsonOptions);

        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<List<ReceivedWebSocketEnvelope>> ReadEventsUntilTerminalAsync(ClientWebSocket socket, string requestId)
    {
        var events = new List<ReceivedWebSocketEnvelope>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var envelope = await ReceiveEnvelopeAsync(socket);
            Assert.Equal(requestId, envelope.RequestId);
            if (envelope.Type == WebSocketMessageTypes.Event)
            {
                events.Add(envelope);
                if (envelope.Payload.GetProperty("terminal").GetBoolean())
                {
                    return events;
                }
            }
            else
            {
                Assert.Fail($"Expected event envelope but received {envelope.Type}: {envelope.Payload.GetRawText()}");
            }
        }

        throw new TimeoutException("Timed out waiting for terminal WebSocket event.");
    }

    private static async Task<ReceivedWebSocketEnvelope> ReceiveEnvelopeAsync(ClientWebSocket socket)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"The WebSocket closed before a JSON envelope was received: {socket.CloseStatus}.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                using var document = JsonDocument.Parse(stream.ToArray());
                var root = document.RootElement;
                return new ReceivedWebSocketEnvelope(
                    root.GetProperty("type").GetString()!,
                    root.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null,
                    root.GetProperty("payload").Clone());
            }
        }
    }

    private static async Task WaitForMetricAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for WebSocket runtime metric condition.");
    }

    private static string ResolveProofHostFile(string directory, string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost", directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate proof host file '{directory}/{fileName}'.");
    }

    private sealed record ReceivedWebSocketEnvelope(string Type, string? RequestId, JsonElement Payload);
}
