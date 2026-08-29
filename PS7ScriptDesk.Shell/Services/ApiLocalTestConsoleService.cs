using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Services;

public enum ApiLocalTestSessionState
{
    Idle,
    Connecting,
    Running,
    Canceling,
    Completed,
    Failed,
    Canceled
}

public sealed record ApiLocalTestRequest(
    ApiTransport Transport,
    Uri EndpointUri,
    HttpMethod Method,
    string? Payload,
    IReadOnlyDictionary<string, string> Headers,
    TimeSpan Timeout);

public sealed record ApiLocalTestConsoleResponse(
    bool Succeeded,
    Uri EndpointUri,
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    long ElapsedMilliseconds,
    string UserMessage,
    bool WasCanceled = false);

public sealed record ApiLocalTestEventRow(
    long Sequence,
    string EventKind,
    string Stream,
    string Value,
    DateTimeOffset Timestamp,
    string TerminalStatus,
    bool IsTerminal,
    string SerializedJson,
    string? StatusCode,
    long? ElapsedMilliseconds)
{
    public static ApiLocalTestEventRow FromEvent(ApiStreamingInvocationEvent item, int valueLimit = 4096)
    {
        var stream = item.Kind switch
        {
            ApiStreamingInvocationEventKind.Output => "Output",
            ApiStreamingInvocationEventKind.Warning => "Warning",
            ApiStreamingInvocationEventKind.Verbose => "Verbose",
            ApiStreamingInvocationEventKind.Debug => "Debug",
            ApiStreamingInvocationEventKind.Information => "Information",
            ApiStreamingInvocationEventKind.Error => "Error",
            _ => "Lifecycle"
        };

        var value = item.Message ?? FormatPayload(item.Payload);
        var serialized = JsonSerializer.Serialize(item, ConsoleJsonOptions.Shared);
        return new ApiLocalTestEventRow(
            item.Sequence,
            item.Kind.ToString(),
            stream,
            Truncate(value, valueLimit),
            item.Timestamp,
            item.IsTerminal ? item.StatusCode ?? item.Kind.ToString() : string.Empty,
            item.IsTerminal,
            serialized,
            item.StatusCode,
            item.ElapsedMilliseconds);
    }

    private static string FormatPayload(object? payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        if (payload is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
        }

        return payload is string text
            ? text
            : JsonSerializer.Serialize(payload, ConsoleJsonOptions.Shared);
    }

    internal static string Truncate(string value, int limit)
    {
        if (limit <= 0 || value.Length <= limit)
        {
            return value;
        }

        return value[..Math.Max(0, limit - 3)] + "...";
    }
}

public sealed class ApiLocalTestEventBuffer
{
    private readonly int _capacity;
    private readonly Queue<ApiLocalTestEventRow> _items = new();

    public ApiLocalTestEventBuffer(int capacity = ApiLocalTestConsoleService.DefaultEventRetentionLimit)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Capacity => _capacity;
    public int Count => _items.Count;
    public int TrimmedCount { get; private set; }
    public IReadOnlyList<ApiLocalTestEventRow> Items => _items.ToArray();

    public bool Add(ApiLocalTestEventRow item)
    {
        var trimmed = false;
        while (_items.Count >= _capacity)
        {
            _items.Dequeue();
            TrimmedCount++;
            trimmed = true;
        }

        _items.Enqueue(item);
        return trimmed;
    }

    public void Clear()
    {
        _items.Clear();
        TrimmedCount = 0;
    }
}

public sealed class ApiLocalTestSessionChangedEventArgs : EventArgs
{
    public ApiLocalTestSessionChangedEventArgs(Guid sessionId, ApiLocalTestSessionState state, ApiLocalTestConsoleResponse? response)
    {
        SessionId = sessionId;
        State = state;
        Response = response;
    }

    public Guid SessionId { get; }
    public ApiLocalTestSessionState State { get; }
    public ApiLocalTestConsoleResponse? Response { get; }
}

public sealed class ApiLocalTestEventReceivedEventArgs : EventArgs
{
    public ApiLocalTestEventReceivedEventArgs(Guid sessionId, ApiLocalTestEventRow item, bool wasTrimmed)
    {
        SessionId = sessionId;
        Item = item;
        WasTrimmed = wasTrimmed;
    }

    public Guid SessionId { get; }
    public ApiLocalTestEventRow Item { get; }
    public bool WasTrimmed { get; }
}

public interface IApiLocalTestTransportClient
{
    Task<ApiLocalTestConsoleResponse> ExecuteAsync(
        ApiLocalTestRequest request,
        Action<ApiLocalTestSessionState> stateChanged,
        Action<ApiStreamingInvocationEvent> eventReceived,
        CancellationToken cancellationToken);
}

public sealed class ApiLocalTestConsoleService : IAsyncDisposable
{
    public const int DefaultEventRetentionLimit = 500;

    private readonly object _gate = new();
    private readonly IApiLocalTestTransportClient _transportClient;
    private readonly ApiLocalTestEventBuffer _eventBuffer;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _activeSession;
    private Guid _sessionId;
    private ApiLocalTestSessionState _state = ApiLocalTestSessionState.Idle;
    private ApiLocalTestConsoleResponse? _lastResponse;
    private bool _terminalEventSeen;

    public ApiLocalTestConsoleService(
        IApiLocalTestTransportClient? transportClient = null,
        int eventRetentionLimit = DefaultEventRetentionLimit)
    {
        _transportClient = transportClient ?? new ApiLocalTestTransportClient();
        _eventBuffer = new ApiLocalTestEventBuffer(eventRetentionLimit);
    }

    public event EventHandler<ApiLocalTestSessionChangedEventArgs>? StateChanged;
    public event EventHandler<ApiLocalTestEventReceivedEventArgs>? EventReceived;

    public ApiLocalTestSessionState State => _state;
    public Guid SessionId => _sessionId;
    public bool IsActive => _state is ApiLocalTestSessionState.Connecting or ApiLocalTestSessionState.Running or ApiLocalTestSessionState.Canceling;
    public ApiLocalTestConsoleResponse? LastResponse => _lastResponse;
    public ApiLocalTestEventBuffer EventBuffer => _eventBuffer;

    public Task<ApiLocalTestConsoleResponse> RunAsync(ApiLocalTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (IsActive)
            {
                DeveloperDiagnostics.LogDecision(
                    "ApiLocalTest",
                    "RunRejected",
                    "A local API test was rejected because another session is active.",
                    "RejectDuplicateRun",
                    new Dictionary<string, object?> { ["transport"] = request.Transport.ToString() });
                return Task.FromResult(new ApiLocalTestConsoleResponse(
                    false,
                    request.EndpointUri,
                    null,
                    null,
                    new Dictionary<string, string>(),
                    string.Empty,
                    0,
                    "A local test is already running."));
            }

            _sessionId = Guid.NewGuid();
            _eventBuffer.Clear();
            _terminalEventSeen = false;
            _lastResponse = null;
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            DeveloperDiagnostics.LogOperationStart(
                "ApiLocalTest",
                "RunStarted",
                "Starting a local API test session.",
                _sessionId.ToString("N"),
                new Dictionary<string, object?>
                {
                    ["transport"] = request.Transport.ToString(),
                    ["method"] = request.Method.Method,
                    ["payloadLength"] = request.Payload?.Length ?? 0,
                    ["headerCount"] = request.Headers.Count,
                    ["timeoutMilliseconds"] = request.Timeout.TotalMilliseconds
                });
            var activeSession = RunCoreAsync(request, _sessionId, _sessionCancellation);
            _activeSession = activeSession;
            return activeSession;
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (!IsActive || _sessionCancellation is null)
            {
                return;
            }

            cancellation = _sessionCancellation;
            DeveloperDiagnostics.LogUserAction(
                "ApiLocalTest",
                "CancelRequested",
                "Canceling the active local API test session.",
                new Dictionary<string, object?> { ["sessionId"] = _sessionId.ToString("N") });
            SetState(ApiLocalTestSessionState.Canceling, _sessionId, null);
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void ClearResults()
    {
        if (IsActive)
        {
            return;
        }

        _eventBuffer.Clear();
        _lastResponse = null;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        Task? active;
        lock (_gate)
        {
            cancellation = _sessionCancellation;
            active = _activeSession;
        }

        try
        {
            cancellation?.Cancel();
            if (active is not null)
            {
                await active.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task<ApiLocalTestConsoleResponse> RunCoreAsync(
        ApiLocalTestRequest request,
        Guid sessionId,
        CancellationTokenSource cancellation)
    {
        SetState(ApiLocalTestSessionState.Connecting, sessionId, null);
        ApiLocalTestConsoleResponse? response = null;
        try
        {
            response = await _transportClient.ExecuteAsync(
                request,
                state => SetState(state, sessionId, null),
                item => AddEvent(sessionId, item),
                cancellation.Token).ConfigureAwait(false);

            var finalState = response.WasCanceled || cancellation.IsCancellationRequested
                ? ApiLocalTestSessionState.Canceled
                : response.Succeeded
                    ? ApiLocalTestSessionState.Completed
                    : ApiLocalTestSessionState.Failed;
            response = response with { WasCanceled = finalState == ApiLocalTestSessionState.Canceled };
            SetState(finalState, sessionId, response);
            DeveloperDiagnostics.LogOperationStop(
                "ApiLocalTest",
                "RunCompleted",
                "Local API test session completed.",
                response.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId.ToString("N"),
                    ["transport"] = request.Transport.ToString(),
                    ["state"] = finalState.ToString(),
                    ["succeeded"] = response.Succeeded,
                    ["wasCanceled"] = response.WasCanceled,
                    ["eventCount"] = _eventBuffer.Count,
                    ["trimmedEventCount"] = _eventBuffer.TrimmedCount
                });
            return response;
        }
        catch (OperationCanceledException)
        {
            var wasCanceled = cancellation.IsCancellationRequested;
            response = new ApiLocalTestConsoleResponse(
                false,
                request.EndpointUri,
                null,
                null,
                new Dictionary<string, string>(),
                string.Empty,
                0,
                wasCanceled ? "The local test was canceled." : "The local test timed out.",
                WasCanceled: wasCanceled);
            var terminalState = wasCanceled ? ApiLocalTestSessionState.Canceled : ApiLocalTestSessionState.Failed;
            SetState(terminalState, sessionId, response);
            DeveloperDiagnostics.LogOperationStop(
                "ApiLocalTest",
                "RunCanceledOrTimedOut",
                wasCanceled ? "Local API test session was canceled." : "Local API test session timed out.",
                response.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId.ToString("N"),
                    ["transport"] = request.Transport.ToString(),
                    ["state"] = terminalState.ToString()
                });
            return response;
        }
        catch (Exception ex)
        {
            response = new ApiLocalTestConsoleResponse(
                false,
                request.EndpointUri,
                null,
                null,
                new Dictionary<string, string>(),
                string.Empty,
                0,
                ApiLocalTestTransportClient.DescribeException(ex));
            SetState(ApiLocalTestSessionState.Failed, sessionId, response);
            DeveloperDiagnostics.LogOperationFailure(
                "ApiLocalTest",
                "RunFailed",
                "Local API test session failed.",
                ex,
                response.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId.ToString("N"),
                    ["transport"] = request.Transport.ToString()
                });
            return response;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_sessionCancellation, cancellation))
                {
                    _activeSession = null;
                    _sessionCancellation = null;
                    if (response is not null)
                    {
                        _lastResponse = response;
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private void AddEvent(Guid sessionId, ApiStreamingInvocationEvent item)
    {
        lock (_gate)
        {
            if (item.IsTerminal)
            {
                if (_terminalEventSeen)
                {
                    return;
                }

                _terminalEventSeen = true;
            }
        }

        var row = ApiLocalTestEventRow.FromEvent(item);
        var trimmed = _eventBuffer.Add(row);
        EventReceived?.Invoke(this, new ApiLocalTestEventReceivedEventArgs(sessionId, row, trimmed));
    }

    private void SetState(ApiLocalTestSessionState state, Guid sessionId, ApiLocalTestConsoleResponse? response)
    {
        ApiLocalTestSessionState previousState;
        lock (_gate)
        {
            previousState = _state;
            _state = state;
            if (response is not null)
            {
                _lastResponse = response;
            }
        }

        DeveloperDiagnostics.LogStateTransition(
            "ApiLocalTest",
            "SessionStateChanged",
            previousState.ToString(),
            state.ToString(),
            "Local API test session state changed.",
            new Dictionary<string, object?> { ["sessionId"] = sessionId.ToString("N") });

        StateChanged?.Invoke(this, new ApiLocalTestSessionChangedEventArgs(sessionId, state, response));
    }
}

public sealed class ApiLocalTestTransportClient : IApiLocalTestTransportClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public Task<ApiLocalTestConsoleResponse> ExecuteAsync(
        ApiLocalTestRequest request,
        Action<ApiLocalTestSessionState> stateChanged,
        Action<ApiStreamingInvocationEvent> eventReceived,
        CancellationToken cancellationToken)
        => request.Transport switch
        {
            ApiTransport.WebSocket => ExecuteWebSocketAsync(request, stateChanged, eventReceived, cancellationToken),
            ApiTransport.ServerSentEvents => ExecuteSseAsync(request, stateChanged, eventReceived, cancellationToken),
            _ => ExecuteHttpAsync(request, stateChanged, cancellationToken)
        };

    private static async Task<ApiLocalTestConsoleResponse> ExecuteHttpAsync(
        ApiLocalTestRequest request,
        Action<ApiLocalTestSessionState> stateChanged,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(request.Method, request.EndpointUri);
        AddHeaders(message, request.Headers);
        if (request.Payload is not null)
        {
            message.Content = new StringContent(request.Payload, Encoding.UTF8, "application/json");
        }

        stateChanged(ApiLocalTestSessionState.Running);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return CreateResponse(request, response, FormatResponseBody(body), stopwatch.ElapsedMilliseconds, response.IsSuccessStatusCode ? "Request completed." : CreateHttpFailure(response));
    }

    private static async Task<ApiLocalTestConsoleResponse> ExecuteSseAsync(
        ApiLocalTestRequest request,
        Action<ApiLocalTestSessionState> stateChanged,
        Action<ApiStreamingInvocationEvent> eventReceived,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(HttpMethod.Get, request.EndpointUri);
        AddHeaders(message, request.Headers);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return CreateResponse(request, response, FormatResponseBody(body), stopwatch.ElapsedMilliseconds, CreateHttpFailure(response));
        }

        stateChanged(ApiLocalTestSessionState.Running);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataLines = new List<string>();
        var terminal = false;
        while (!timeout.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count > 0 && TryDeserializeEvent(string.Join("\n", dataLines), out var item))
                {
                    eventReceived(item);
                    terminal = item.IsTerminal;
                }

                dataLines.Clear();
                if (terminal)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
            }
        }

        return CreateResponse(request, response, terminal ? "Streaming invocation completed." : "Streaming connection ended before a terminal event was received.", stopwatch.ElapsedMilliseconds, terminal ? "Streaming invocation completed." : "The SSE connection ended before completion.") with
        {
            Succeeded = terminal
        };
    }

    private static async Task<ApiLocalTestConsoleResponse> ExecuteWebSocketAsync(
        ApiLocalTestRequest request,
        Action<ApiLocalTestSessionState> stateChanged,
        Action<ApiStreamingInvocationEvent> eventReceived,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var socket = new ClientWebSocket();
        foreach (var header in request.Headers)
        {
            socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        await socket.ConnectAsync(request.EndpointUri, timeout.Token).ConfigureAwait(false);
        stateChanged(ApiLocalTestSessionState.Running);
        var payload = Encoding.UTF8.GetBytes(request.Payload ?? "{}");
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

        var terminal = false;
        var succeeded = false;
        while (!timeout.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(socket, timeout.Token).ConfigureAwait(false);
            if (!TryDeserializeWebSocketEvent(message, out var item, out var messageSucceeded, out var messageTerminal))
            {
                continue;
            }

            eventReceived(item);
            terminal = messageTerminal;
            succeeded = messageSucceeded;
            if (terminal)
            {
                break;
            }
        }

        return new ApiLocalTestConsoleResponse(
            succeeded,
            request.EndpointUri,
            101,
            "Switching Protocols",
            new Dictionary<string, string>(),
            terminal ? "Streaming invocation completed." : "WebSocket connection ended before a terminal event was received.",
            stopwatch.ElapsedMilliseconds,
            terminal && succeeded ? "Streaming invocation completed." : "The WebSocket invocation did not complete.");
    }

    private static bool TryDeserializeEvent(string json, out ApiStreamingInvocationEvent item)
    {
        try
        {
            item = JsonSerializer.Deserialize<ApiStreamingInvocationEvent>(json, JsonOptions)!;
            return item is not null;
        }
        catch (JsonException)
        {
            item = default!;
            return false;
        }
    }

    private static bool TryDeserializeWebSocketEvent(
        string json,
        out ApiStreamingInvocationEvent item,
        out bool succeeded,
        out bool terminal)
    {
        succeeded = false;
        terminal = false;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var requestId = root.TryGetProperty("requestId", out var requestIdElement) ? requestIdElement.GetString() : null;
        var timestamp = root.TryGetProperty("timestamp", out var timestampElement) && timestampElement.TryGetDateTimeOffset(out var parsedTimestamp)
            ? parsedTimestamp
            : DateTimeOffset.UtcNow;
        if (string.Equals(type, "event", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("payload", out var payload))
        {
            var eventType = payload.TryGetProperty("eventType", out var eventTypeElement) ? eventTypeElement.GetString() : null;
            var kind = ParseEventKind(eventType);
            var sequence = payload.TryGetProperty("sequence", out var sequenceElement) ? sequenceElement.GetInt64() : 0;
            var invocationId = payload.TryGetProperty("invocationId", out var invocationElement) ? invocationElement.GetString() : requestId ?? string.Empty;
            var endpointId = payload.TryGetProperty("endpointId", out var endpointElement) ? endpointElement.GetString() : string.Empty;
            var message = payload.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            var statusCode = payload.TryGetProperty("statusCode", out var statusElement) ? statusElement.GetString() : null;
            object? payloadValue = payload.TryGetProperty("payload", out var valueElement) ? valueElement.Clone() : null;
            var elapsed = payload.TryGetProperty("elapsedMilliseconds", out var elapsedElement) && elapsedElement.ValueKind == JsonValueKind.Number
                ? elapsedElement.GetInt64()
                : (long?)null;
            terminal = payload.TryGetProperty("terminal", out var terminalElement) && terminalElement.ValueKind == JsonValueKind.True;
            succeeded = terminal && kind == ApiStreamingInvocationEventKind.InvocationCompleted;
            item = new ApiStreamingInvocationEvent(invocationId ?? string.Empty, endpointId ?? string.Empty, null, null, sequence, kind, timestamp, payloadValue, message, statusCode, elapsed);
            return true;
        }

        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("payload", out var errorPayload))
        {
            var detail = errorPayload.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : "The WebSocket invocation failed.";
            var code = errorPayload.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : "websocket-error";
            item = new ApiStreamingInvocationEvent(requestId ?? string.Empty, string.Empty, null, null, 0, ApiStreamingInvocationEventKind.InvocationFailed, timestamp, Message: detail, StatusCode: code);
            terminal = true;
            return true;
        }

        if (string.Equals(type, "protocolError", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("payload", out var protocolPayload))
        {
            var detail = protocolPayload.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : "The WebSocket protocol rejected the request.";
            var code = protocolPayload.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : "websocket-protocol-error";
            item = new ApiStreamingInvocationEvent(requestId ?? string.Empty, string.Empty, null, null, 0, ApiStreamingInvocationEventKind.InvocationFailed, timestamp, Message: detail, StatusCode: code);
            terminal = true;
            return true;
        }

        item = default!;
        return false;
    }

    private static ApiStreamingInvocationEventKind ParseEventKind(string? value)
        => Enum.TryParse<ApiStreamingInvocationEventKind>(value, ignoreCase: true, out var kind)
            ? kind
            : ApiStreamingInvocationEventKind.Error;

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The WebSocket closed before a complete response was received.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static ApiLocalTestConsoleResponse CreateResponse(
        ApiLocalTestRequest request,
        HttpResponseMessage response,
        string body,
        long elapsedMilliseconds,
        string userMessage)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.SelectMany(item => item.Value)), StringComparer.OrdinalIgnoreCase);
        return new ApiLocalTestConsoleResponse(response.IsSuccessStatusCode, request.EndpointUri, (int)response.StatusCode, response.ReasonPhrase, headers, body, elapsedMilliseconds, userMessage);
    }

    public static string FormatResponseBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        }
        catch (JsonException)
        {
            return ApiLocalTestEventRow.Truncate(text, 64 * 1024);
        }
    }

    public static string DescribeException(Exception exception)
        => exception switch
        {
            HttpRequestException => "The local API could not be reached. Check that the host is running.",
            WebSocketException => "The WebSocket connection failed. Check the endpoint and local host.",
            _ => "The local API test failed. See Developer Diagnostics for details."
        };

    private static string CreateHttpFailure(HttpResponseMessage response)
        => response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Authentication failed. Check the API key.",
            System.Net.HttpStatusCode.NotFound => "Endpoint not found. Verify the selected endpoint and local host.",
            System.Net.HttpStatusCode.BadRequest => "The request was rejected. Check the parameter values and payload.",
            _ => $"The API returned {(int)response.StatusCode} {response.ReasonPhrase}."
        };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal static class ConsoleJsonOptions
{
    public static JsonSerializerOptions Shared { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
