using System.Net;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.Hosting;

namespace PS7ScriptDesk.Tests;

public sealed class SseRuntimeTests : IDisposable
{
    private const string ApiKeyValue = "PHASE13_SSE_API_KEY";
    private static readonly string[] SseEndpointIds =
    [
        "poc-get-systeminfo",
        "poc-live-timing",
        "poc-live-streams",
        "poc-live-pressure",
        "poc-live-cancellation",
        "poc-test-failure",
        "poc-phase4-timeout"
    ];

    private readonly string _apiKeyVariableName = $"PS7API_PHASE13_SSE_{Guid.NewGuid():N}";
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk SSE Runtime {Guid.NewGuid():N}");

    public SseRuntimeTests()
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
    public async Task SseEndpoint_ReturnsOkTextEventStreamAndNormalizedEvents()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            "/sse/poc-get-systeminfo?computerName=SERVER01",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

        using var restAttempt = await client.GetAsync("/api/systeminfo?computerName=SERVER01");
        Assert.NotEqual(HttpStatusCode.OK, restAttempt.StatusCode);

        using var discovery = await client.GetAsync("/api/endpoints");
        var discoveryText = await discovery.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.Contains("/sse/poc-get-systeminfo", discoveryText, StringComparison.Ordinal);
        Assert.Contains(ApiTransport.ServerSentEvents.ToString(), discoveryText, StringComparison.Ordinal);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = await ReadEventsUntilTerminalAsync(reader);

        Assert.Equal("InvocationStarted", events[0].EventType);
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationStarted.ToString(), events[0].Data.GetProperty("kind").GetString());
        Assert.Equal("InvocationCompleted", events[^1].EventType);
        Assert.Equal("success", events[^1].Data.GetProperty("statusCode").GetString());
        Assert.Single(events, item => item.Data.GetProperty("isTerminal").GetBoolean());

        var output = Assert.Single(events, item => item.EventType == "Output");
        Assert.Equal("SERVER01", output.Data.GetProperty("payload").GetProperty("ComputerName").GetString());
        Assert.Equal(output.Id, output.Data.GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task SseInvocation_ReceivesOutputBeforePowerShellCompletesAndPreservesSequence()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/sse/poc-live-timing", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var started = await ReadNextEventAsync(reader);
        var first = await ReadNextMatchingEventAsync(reader, item => item.EventType == "Output");

        Assert.Equal("InvocationStarted", started.EventType);
        Assert.Equal("first", first.Data.GetProperty("payload").GetString());
        Assert.False(first.Data.GetProperty("isTerminal").GetBoolean());
        Assert.True(stopwatch.ElapsedMilliseconds < 1000);
        Assert.True(host.Metrics.ActiveInvocationCount > 0);

        var events = new List<SseEvent> { started, first };
        events.AddRange(await ReadEventsUntilTerminalAsync(reader));

        Assert.Contains(events, item => item.EventType == "Output" && item.Data.GetProperty("payload").GetString() == "second");
        Assert.Contains(events, item => item.EventType == "Output" && item.Data.GetProperty("payload").GetString() == "third");
        Assert.Equal("InvocationCompleted", events[^1].EventType);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Id));
        Assert.All(events, item => Assert.Equal(item.Id, item.Data.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    public async Task SseInvocation_StreamsEverySupportedPowerShellStreamAndFailureTerminal()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/sse/poc-live-streams", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var events = await ReadEventsUntilTerminalAsync(reader);

        Assert.Contains(events, item => item.EventType == "Output" && item.Data.GetProperty("payload").GetString() == "out-1");
        Assert.Contains(events, item => item.EventType == "Warning" && item.Data.GetProperty("message").GetString() == "warn-1");
        Assert.Contains(events, item => item.EventType == "Verbose" && item.Data.GetProperty("message").GetString() == "verbose-1");
        Assert.Contains(events, item => item.EventType == "Debug" && item.Data.GetProperty("message").GetString() == "debug-1");
        Assert.Contains(events, item => item.EventType == "Information" && item.Data.GetProperty("message").GetString() == "info-1");
        Assert.Contains(events, item => item.EventType == "Error" && item.Data.GetProperty("message").GetString()!.Contains("LiveStreamingNonTerminatingError", StringComparison.Ordinal));
        Assert.Equal("InvocationFailed", events[^1].EventType);
        Assert.Equal("powershell-nonterminating-error", events[^1].Data.GetProperty("statusCode").GetString());
        Assert.Single(events, item => item.Data.GetProperty("isTerminal").GetBoolean());
    }

    [Fact]
    public async Task SseInvocation_FailedAndTimedOutInvocationsEmitSanitizedTerminalEvents()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var failureResponse = await client.GetAsync("/sse/poc-test-failure", HttpCompletionOption.ResponseHeadersRead);
        var failureEvents = await ReadSseResponseUntilTerminalAsync(failureResponse);
        Assert.Equal("InvocationFailed", failureEvents[^1].EventType);
        Assert.Equal("powershell-terminating-failure", failureEvents[^1].Data.GetProperty("statusCode").GetString());
        Assert.DoesNotContain("Intentional test failure", failureEvents[^1].Data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Management.Automation", failureEvents[^1].Data.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var timeoutResponse = await client.GetAsync(
            "/sse/poc-phase4-timeout?requestId=timeout-me&milliseconds=1000",
            HttpCompletionOption.ResponseHeadersRead);
        var timeoutEvents = await ReadSseResponseUntilTerminalAsync(timeoutResponse);
        Assert.Equal("InvocationFailed", timeoutEvents[^1].EventType);
        Assert.Equal("invocation-timeout", timeoutEvents[^1].Data.GetProperty("statusCode").GetString());
    }

    [Fact]
    public async Task SseInvocation_ClientDisconnectCancelsActiveInvocation()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/sse/poc-live-cancellation", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        _ = await ReadNextMatchingEventAsync(reader, item => item.EventType == "Output");
        Assert.True(host.Metrics.ActiveInvocationCount > 0);

        response.Dispose();
        client.Dispose();

        await WaitForMetricAsync(() => host.Metrics.ActiveInvocationCount == 0 && host.Metrics.CallerCanceledCount > 0);
    }

    [Fact]
    public async Task SseInvocation_EmitsExactlyOneTerminalAndNoDuplicateOutputInTerminal()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/sse/poc-live-timing", HttpCompletionOption.ResponseHeadersRead);

        var events = await ReadSseResponseUntilTerminalAsync(response);

        Assert.Equal(3, events.Count(item => item.EventType == "Output"));
        var terminal = Assert.Single(events, item => item.Data.GetProperty("isTerminal").GetBoolean());
        Assert.Equal("InvocationCompleted", terminal.EventType);
        Assert.Equal(JsonValueKind.Null, terminal.Data.GetProperty("payload").ValueKind);
        Assert.Same(events[^1], terminal);
    }

    [Fact]
    public async Task SseInvocation_SlowConsumerDrainsBoundedStreamInOrder()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/sse/poc-live-pressure?count=40", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = new List<SseEvent>();

        while (true)
        {
            var item = await ReadNextEventAsync(reader);
            events.Add(item);
            await Task.Delay(10);
            if (item.Data.GetProperty("isTerminal").GetBoolean())
            {
                break;
            }
        }

        Assert.Equal(40, events.Count(item => item.EventType == "Output"));
        Assert.Equal("InvocationCompleted", events[^1].EventType);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Id));
    }

    [Fact]
    public async Task SseEndpoint_RequiresExistingApiAuthentication()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = host.CreateClient();

        using var missing = await client.GetAsync("/sse/poc-get-systeminfo?computerName=SERVER01");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/sse/poc-get-systeminfo?computerName=SERVER01");
        request.Headers.Add(ApiKeyAuthenticationService.ApiKeyHeaderName, ApiKeyValue);
        using var authorized = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal("text/event-stream", authorized.Content.Headers.ContentType?.MediaType);
    }

    private async Task<RunningRestApiProofHost> StartHostAsync()
        => await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions
        {
            Url = "http://127.0.0.1:0",
            ContentRootPath = CreateTransportContentRoot(ApiTransport.ServerSentEvents),
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
        var root = CreateTransportContentRoot(ApiTransport.ServerSentEvents);
        var configuration = JsonSerializer.Deserialize<ApiPublishConfiguration>(
            File.ReadAllText(Path.Combine(root, "Config", "api.ps7api.json")),
            RestApiProofHostFactory.JsonOptions)!;
        configuration.Security.Mode = ApiSecurityMode.ApiKey;
        configuration.Security.ApiKeyEnvironmentVariableName = _apiKeyVariableName;
        configuration.Endpoints.First(endpoint => endpoint.EndpointId == "poc-get-systeminfo").RequiresAuthentication = true;
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
        foreach (var endpoint in configuration.Endpoints.Where(endpoint => SseEndpointIds.Contains(endpoint.EndpointId, StringComparer.OrdinalIgnoreCase)))
        {
            endpoint.Transport = transport;
        }

        File.WriteAllText(
            Path.Combine(configDirectory, "api.ps7api.json"),
            JsonSerializer.Serialize(configuration, RestApiProofHostFactory.JsonOptions),
            new UTF8Encoding(false));

        return root;
    }

    private static async Task<List<SseEvent>> ReadSseResponseUntilTerminalAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await ReadEventsUntilTerminalAsync(reader);
    }

    private static async Task<List<SseEvent>> ReadEventsUntilTerminalAsync(StreamReader reader)
    {
        var events = new List<SseEvent>();
        while (true)
        {
            var item = await ReadNextEventAsync(reader);
            events.Add(item);
            if (item.Data.GetProperty("isTerminal").GetBoolean())
            {
                return events;
            }
        }
    }

    private static async Task<SseEvent> ReadNextMatchingEventAsync(StreamReader reader, Func<SseEvent, bool> predicate)
    {
        while (true)
        {
            var item = await ReadNextEventAsync(reader);
            if (predicate(item))
            {
                return item;
            }
        }
    }

    private static async Task<SseEvent> ReadNextEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? eventType = null;
        long? id = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new EndOfStreamException("The SSE stream ended before a complete event was received.");
            }

            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(data.ToString());
                return new SseEvent(eventType, id ?? 0, document.RootElement.Clone());
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line["event: ".Length..];
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = long.Parse(line["id: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line["data: ".Length..]);
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

        throw new TimeoutException("Timed out waiting for SSE runtime metric condition.");
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

    private sealed record SseEvent(string? EventType, long Id, JsonElement Data);
}
