using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.WebSockets;

public static class WebSocketEndpointMapper
{
    public const string RouteTemplate = "/ws/{endpointId}";
    private const int ReceiveBufferSize = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void MapWebSocketEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(RouteTemplate, HandleWebSocketAsync);
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiProofHost.WebSocket");
        var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
        var endpointId = Convert.ToString(context.Request.RouteValues["endpointId"], System.Globalization.CultureInfo.InvariantCulture);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            logger.LogWarning("Rejected non-WebSocket request for WebSocket endpoint route {EndpointId}.", endpointId);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket upgrade required.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var resolved = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, endpointId, ApiTransport.WebSocket);
        if (!resolved.IsSuccess || resolved.Endpoint is null)
        {
            logger.LogWarning(
                "Rejected WebSocket upgrade for endpoint route {EndpointId}: {ErrorCode}.",
                endpointId,
                resolved.ErrorCode);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var endpoint = resolved.Endpoint;
        var authentication = context.RequestServices.GetRequiredService<ApiKeyAuthenticationService>();
        var authenticationResult = authentication.AuthenticateEndpoint(context, endpoint);
        if (!authenticationResult.IsSuccess)
        {
            logger.LogWarning(
                "Rejected unauthorized WebSocket upgrade for endpoint {EndpointId}: status {Status}.",
                endpoint.EndpointId,
                authenticationResult.StatusCode);
            await authenticationResult.ToResult(context).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connectionId = $"ws-{Guid.NewGuid():N}";
        logger.LogInformation(
            "Accepted WebSocket connection {ConnectionId} for endpoint {EndpointId}.",
            connectionId,
            endpoint.EndpointId);

        try
        {
            await ProcessConnectionAsync(context, socket, endpoint, connectionId, logger).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "WebSocket connection {ConnectionId} for endpoint {EndpointId} canceled by request abort.",
                connectionId,
                endpoint.EndpointId);
        }
        catch (WebSocketException exception)
        {
            logger.LogWarning(
                exception,
                "WebSocket transport failure for connection {ConnectionId} endpoint {EndpointId}.",
                connectionId,
                endpoint.EndpointId);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Internal WebSocket transport failure for connection {ConnectionId} endpoint {EndpointId}.",
                connectionId,
                endpoint.EndpointId);
            await TryCloseAsync(socket, WebSocketCloseStatus.InternalServerError, "Server failure.", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            logger.LogInformation(
                "Closed WebSocket connection {ConnectionId} for endpoint {EndpointId} with state {State}.",
                connectionId,
                endpoint.EndpointId,
                socket.State);
        }
    }

    private static async Task ProcessConnectionAsync(
        HttpContext context,
        WebSocket socket,
        ApiEndpointConfiguration endpoint,
        string connectionId,
        ILogger logger)
    {
        var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
        var coordinator = context.RequestServices.GetRequiredService<PowerShellInvocationCoordinator>();
        var binder = context.RequestServices.GetRequiredService<ApiEndpointParameterBinder>();
        var maxMessageBytes = Math.Max(1, configuration.Runtime.WebSocketMessageSizeLimitBytes);
        using var sendLock = new SemaphoreSlim(1, 1);
        var incoming = Channel.CreateBounded<WebSocketReceiveResultData>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var receivePump = ReceiveConnectionLoopAsync(socket, incoming.Writer, maxMessageBytes, context.RequestAborted);

        try
        {
            while (socket.State == WebSocketState.Open &&
                   !context.RequestAborted.IsCancellationRequested &&
                   await incoming.Reader.WaitToReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                var received = await incoming.Reader.ReadAsync(context.RequestAborted).ConfigureAwait(false);
                if (received.IsClose)
                {
                    logger.LogInformation(
                        "WebSocket connection {ConnectionId} received client close for endpoint {EndpointId}.",
                        connectionId,
                        endpoint.EndpointId);
                    await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Closing.", CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (received.Failure is not null)
                {
                    await SendProtocolFailureAndMaybeCloseAsync(socket, sendLock, received.Failure, logger, connectionId, endpoint.EndpointId, context.RequestAborted).ConfigureAwait(false);
                    if (received.Failure.TerminalConnection)
                    {
                        return;
                    }

                    continue;
                }

                var parseResult = WebSocketProtocolParser.Shared.ParseTextMessage(received.Message);
                if (!parseResult.IsValid || parseResult.Message is null)
                {
                    await SendProtocolFailureAndMaybeCloseAsync(socket, sendLock, parseResult.Failure!, logger, connectionId, endpoint.EndpointId, context.RequestAborted).ConfigureAwait(false);
                    if (parseResult.Failure!.TerminalConnection ||
                        string.Equals(parseResult.Failure.MessageType, WebSocketMessageTypes.ProtocolError, StringComparison.Ordinal))
                    {
                        await TryCloseAsync(socket, WebSocketCloseStatus.ProtocolError, "Protocol error.", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                if (!string.Equals(parseResult.Message.Type, WebSocketMessageTypes.Invoke, StringComparison.Ordinal))
                {
                    var failure = WebSocketProtocolValidationFailure.RequestError(
                        parseResult.Message.RequestId,
                        WebSocketProtocolErrorCodes.RequestValidationFailure,
                        "Invalid cancellation request.",
                        "No invocation is active for this request ID.",
                        WebSocketErrorCategories.Request);
                    await SendRequestFailureAsync(socket, sendLock, failure, context.RequestAborted).ConfigureAwait(false);
                    continue;
                }

                await InvokeAndStreamAsync(
                    context,
                    socket,
                    incoming.Reader,
                    sendLock,
                    coordinator,
                    binder,
                    endpoint,
                    parseResult.Message,
                    connectionId,
                    logger).ConfigureAwait(false);
            }
        }
        finally
        {
            incoming.Writer.TryComplete();
            if (receivePump.IsCompleted)
            {
                await receivePump.ConfigureAwait(false);
            }
        }
    }

    private static async Task InvokeAndStreamAsync(
        HttpContext context,
        WebSocket socket,
        ChannelReader<WebSocketReceiveResultData> incoming,
        SemaphoreSlim sendLock,
        PowerShellInvocationCoordinator coordinator,
        ApiEndpointParameterBinder binder,
        ApiEndpointConfiguration endpoint,
        WebSocketClientMessage message,
        string connectionId,
        ILogger logger)
    {
        var invoke = message.Invoke ?? throw new InvalidOperationException("The WebSocket invocation payload is missing.");
        if (!string.Equals(invoke.EndpointId, endpoint.EndpointId, StringComparison.OrdinalIgnoreCase))
        {
            var failure = WebSocketProtocolValidationFailure.RequestError(
                message.RequestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invalid endpoint identifier.",
                "The requested endpoint does not match the WebSocket route.",
                WebSocketErrorCategories.Endpoint);
            await SendRequestFailureAsync(socket, sendLock, failure, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var bindResult = binder.Bind(endpoint, binding => ResolveWebSocketBindingValue(context, invoke.Parameters, binding));
        if (!bindResult.IsValid)
        {
            logger.LogWarning(
                "Rejected WebSocket invocation request {RequestId} on connection {ConnectionId} endpoint {EndpointId}: {ErrorCode}.",
                message.RequestId,
                connectionId,
                endpoint.EndpointId,
                bindResult.ErrorCode);
            var failure = WebSocketProtocolValidationFailure.RequestError(
                message.RequestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invalid request.",
                bindResult.ErrorMessage ?? "The request could not be bound.",
                WebSocketErrorCategories.Parameter);
            await SendRequestFailureAsync(socket, sendLock, failure, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await using var session = await coordinator.StartStreamingInvocationAsync(
            new ApiStreamingInvocationRequest
            {
                InvocationId = Guid.NewGuid().ToString("N"),
                EndpointId = endpoint.EndpointId,
                FunctionName = endpoint.PowerShellFunctionName,
                Parameters = bindResult.Parameters,
                ConnectionId = connectionId,
                SessionId = connectionId,
                Timeout = endpoint.TimeoutOverride
            },
            context.RequestAborted).ConfigureAwait(false);

        logger.LogInformation(
            "Started WebSocket invocation {InvocationId} request {RequestId} on connection {ConnectionId} endpoint {EndpointId}.",
            session.Request.InvocationId,
            message.RequestId,
            connectionId,
            endpoint.EndpointId);

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var receiveTask = WatchActiveInvocationAsync(
            socket,
            sendLock,
            session,
            incoming,
            message.RequestId,
            endpoint.EndpointId,
            connectionId,
            logger,
            receiveCancellation.Token);

        try
        {
            await foreach (var item in session.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                if (!await TrySendEnvelopeAsync(
                        socket,
                        sendLock,
                        WebSocketProtocolMessageFactory.CreateStreamingEvent(message.RequestId, item),
                        context.RequestAborted).ConfigureAwait(false))
                {
                    session.Cancel();
                    return;
                }

                if (item.IsTerminal)
                {
                    logger.LogInformation(
                        "Completed WebSocket invocation {InvocationId} request {RequestId} on connection {ConnectionId} endpoint {EndpointId} with terminal event {EventKind}.",
                        item.InvocationId,
                        message.RequestId,
                        connectionId,
                        endpoint.EndpointId,
                        item.Kind);
                }
            }
        }
        finally
        {
            receiveCancellation.Cancel();
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task WatchActiveInvocationAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        ApiStreamingInvocationSession session,
        ChannelReader<WebSocketReceiveResultData> incoming,
        string activeRequestId,
        string endpointId,
        string connectionId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested &&
               await incoming.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var received = await incoming.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (received.IsClose)
            {
                logger.LogInformation(
                    "WebSocket disconnect canceled active invocation {InvocationId} request {RequestId} connection {ConnectionId} endpoint {EndpointId}.",
                    session.Request.InvocationId,
                    activeRequestId,
                    connectionId,
                    endpointId);
                session.Cancel();
                return;
            }

            if (received.Failure is not null)
            {
                logger.LogWarning(
                    "WebSocket protocol violation canceled active invocation {InvocationId} request {RequestId} connection {ConnectionId} endpoint {EndpointId}: {ErrorCode}.",
                    session.Request.InvocationId,
                    activeRequestId,
                    connectionId,
                    endpointId,
                    received.Failure.Code);
                session.Cancel();
                await SendProtocolFailureAndMaybeCloseAsync(socket, sendLock, received.Failure, logger, connectionId, endpointId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var parseResult = WebSocketProtocolParser.Shared.ParseTextMessage(received.Message);
            if (!parseResult.IsValid || parseResult.Message is null)
            {
                logger.LogWarning(
                    "WebSocket protocol message canceled active invocation {InvocationId} request {RequestId} connection {ConnectionId} endpoint {EndpointId}: {ErrorCode}.",
                    session.Request.InvocationId,
                    activeRequestId,
                    connectionId,
                    endpointId,
                    parseResult.Failure?.Code);
                session.Cancel();
                if (parseResult.Failure is not null)
                {
                    await SendProtocolFailureAndMaybeCloseAsync(socket, sendLock, parseResult.Failure, logger, connectionId, endpointId, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (string.Equals(parseResult.Message.Type, WebSocketMessageTypes.Cancel, StringComparison.Ordinal) &&
                string.Equals(parseResult.Message.RequestId, activeRequestId, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Client canceled WebSocket invocation {InvocationId} request {RequestId} connection {ConnectionId} endpoint {EndpointId}.",
                    session.Request.InvocationId,
                    activeRequestId,
                    connectionId,
                    endpointId);
                session.Cancel();
                return;
            }

            var failure = WebSocketProtocolValidationFailure.RequestError(
                parseResult.Message.RequestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invocation already active.",
                "This WebSocket connection supports one active invocation at a time.",
                WebSocketErrorCategories.Request);
            await SendRequestFailureAsync(socket, sendLock, failure, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveConnectionLoopAsync(
        WebSocket socket,
        ChannelWriter<WebSocketReceiveResultData> incoming,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var received = await ReceiveTextMessageAsync(socket, maxMessageBytes, cancellationToken).ConfigureAwait(false);
                await incoming.WriteAsync(received, cancellationToken).ConfigureAwait(false);
                if (received.IsClose || received.Failure?.TerminalConnection == true)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            incoming.TryComplete(exception);
            return;
        }
        finally
        {
            incoming.TryComplete();
        }
    }

    private static ApiParameterBindingValue ResolveWebSocketBindingValue(
        HttpContext context,
        IReadOnlyDictionary<string, JsonElement> parameters,
        ApiParameterBindingConfiguration binding)
    {
        if (binding.Source == ApiParameterSource.ServerDefined)
        {
            return ApiEndpointParameterBinder.ResolveServerDefinedValue(
                binding.ServerValue,
                context.TraceIdentifier,
                context.User?.Identity?.Name);
        }

        if (binding.Source == ApiParameterSource.Header)
        {
            return context.Request.Headers.TryGetValue(binding.Name, out var headerValue) && headerValue.Count > 0
                ? ApiParameterBindingValue.Present(headerValue[0])
                : ApiParameterBindingValue.Missing;
        }

        return parameters.TryGetValue(binding.Name, out var value)
            ? ApiParameterBindingValue.Present(value)
            : ApiParameterBindingValue.Missing;
    }

    private static async Task<WebSocketReceiveResultData> ReceiveTextMessageAsync(
        WebSocket socket,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using var stream = new MemoryStream(Math.Min(maxMessageBytes, ReceiveBufferSize));
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(0, ReceiveBufferSize), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return WebSocketReceiveResultData.Close();
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    return WebSocketReceiveResultData.Invalid(WebSocketProtocolParser.Shared.CreateBinaryMessageFailure());
                }

                if (stream.Length + result.Count > maxMessageBytes)
                {
                    return WebSocketReceiveResultData.Invalid(WebSocketProtocolParser.Shared.CreateMessageTooLargeFailure());
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    try
                    {
                        return WebSocketReceiveResultData.Text(StrictUtf8.GetString(stream.ToArray()));
                    }
                    catch (DecoderFallbackException)
                    {
                        return WebSocketReceiveResultData.Invalid(
                            WebSocketProtocolValidationFailure.ProtocolError(
                                WebSocketProtocolErrorCodes.MalformedJson,
                                "Malformed JSON.",
                                "The WebSocket message must be valid UTF-8 JSON.",
                                terminalConnection: true));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task SendProtocolFailureAndMaybeCloseAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WebSocketProtocolValidationFailure failure,
        ILogger logger,
        string connectionId,
        string endpointId,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "WebSocket protocol violation on connection {ConnectionId} endpoint {EndpointId}: {ErrorCode}.",
            connectionId,
            endpointId,
            failure.Code);

        if (string.Equals(failure.MessageType, WebSocketMessageTypes.ProtocolError, StringComparison.Ordinal))
        {
            await TrySendEnvelopeAsync(
                socket,
                sendLock,
                WebSocketProtocolMessageFactory.CreateProtocolError(failure),
                cancellationToken).ConfigureAwait(false);
            if (failure.TerminalConnection)
            {
                await TryCloseAsync(socket, WebSocketCloseStatus.ProtocolError, CloseReasonFor(failure), CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        await SendRequestFailureAsync(socket, sendLock, failure, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendRequestFailureAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WebSocketProtocolValidationFailure failure,
        CancellationToken cancellationToken)
        => await TrySendEnvelopeAsync(
            socket,
            sendLock,
            WebSocketProtocolMessageFactory.CreateRequestError(failure),
            cancellationToken).ConfigureAwait(false);

    private static async Task<bool> TrySendEnvelopeAsync<TPayload>(
        WebSocket socket,
        SemaphoreSlim sendLock,
        WebSocketProtocolEnvelope<TPayload> envelope,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return false;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ApiJsonOptions.Shared);
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task TryCloseAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static string CloseReasonFor(WebSocketProtocolValidationFailure failure)
        => failure.Code == WebSocketProtocolErrorCodes.MessageTooLarge
            ? "Message too large."
            : "Protocol error.";

    private sealed record WebSocketReceiveResultData(
        bool IsClose,
        string? Message,
        WebSocketProtocolValidationFailure? Failure)
    {
        public static WebSocketReceiveResultData Close() => new(true, null, null);
        public static WebSocketReceiveResultData Text(string message) => new(false, message, null);
        public static WebSocketReceiveResultData Invalid(WebSocketProtocolValidationFailure failure) => new(false, null, failure);
    }
}
