using System.Globalization;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.WebSockets;

public static class WebSocketProtocolV1
{
    public const string ProtocolName = "ps7scriptdesk.websocket";
    public const int ProtocolVersion = 1;
    public const int MaximumRequestIdLength = 128;
    public const int MaximumEndpointIdLength = 256;
    public const int MaximumCancelReasonLength = 128;
}

public static class WebSocketMessageTypes
{
    public const string Invoke = "invoke";
    public const string Cancel = "cancel";
    public const string Accepted = "accepted";
    public const string Event = "event";
    public const string Result = "result";
    public const string Error = "error";
    public const string Canceled = "canceled";
    public const string ProtocolError = "protocolError";

    public static bool IsSupportedClientMessageType(string type)
        => string.Equals(type, Invoke, StringComparison.Ordinal) ||
           string.Equals(type, Cancel, StringComparison.Ordinal);
}

public static class WebSocketProtocolErrorCodes
{
    public const string MalformedJson = "malformedJson";
    public const string EmptyMessage = "emptyMessage";
    public const string EnvelopeNotObject = "envelopeNotObject";
    public const string BinaryNotSupported = "binaryNotSupported";
    public const string UnsupportedProtocolVersion = "unsupportedProtocolVersion";
    public const string InvalidProtocol = "invalidProtocol";
    public const string MissingField = "missingField";
    public const string InvalidFieldType = "invalidFieldType";
    public const string InvalidRequestId = "invalidRequestId";
    public const string UnknownMessageType = "unknownMessageType";
    public const string MessageTooLarge = "messageTooLarge";
    public const string ExecutableFieldNotAllowed = "executableFieldNotAllowed";
    public const string RequestValidationFailure = "requestValidationFailure";
}

public static class WebSocketErrorCategories
{
    public const string Request = "request";
    public const string Endpoint = "endpoint";
    public const string Parameter = "parameter";
    public const string Admission = "admission";
    public const string Execution = "execution";
    public const string Normalization = "normalization";
    public const string Authentication = "authentication";
    public const string Internal = "internal";
}

public sealed class WebSocketProtocolParser
{
    public static WebSocketProtocolParser Shared { get; } = new();

    private static readonly HashSet<string> ForbiddenExecutableFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "functionName",
        "command",
        "script",
        "expression",
        "scriptBlock"
    };

    public WebSocketProtocolParseResult ParseTextMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.ProtocolError(
                    WebSocketProtocolErrorCodes.EmptyMessage,
                    "Empty message.",
                    "The WebSocket message must contain a JSON object."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.ProtocolError(
                    WebSocketProtocolErrorCodes.MalformedJson,
                    "Malformed JSON.",
                    "The WebSocket message must be valid JSON."));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return WebSocketProtocolParseResult.Invalid(
                    WebSocketProtocolValidationFailure.ProtocolError(
                        WebSocketProtocolErrorCodes.EnvelopeNotObject,
                        "Invalid message envelope.",
                        "The WebSocket message must be a JSON object."));
            }

            if (!TryGetStringProperty(root, "protocol", required: true, out var protocol, out var protocolFailure))
            {
                return WebSocketProtocolParseResult.Invalid(
                    protocolFailure with
                    {
                        Code = WebSocketProtocolErrorCodes.InvalidProtocol,
                        Title = "Invalid protocol.",
                        Detail = "The WebSocket protocol field is required and must be a string."
                    });
            }

            if (!string.Equals(protocol, WebSocketProtocolV1.ProtocolName, StringComparison.Ordinal))
            {
                return WebSocketProtocolParseResult.Invalid(
                    WebSocketProtocolValidationFailure.ProtocolError(
                        WebSocketProtocolErrorCodes.InvalidProtocol,
                        "Invalid protocol.",
                        "The WebSocket protocol is not supported."));
            }

            if (!TryGetRequiredInt32Property(root, "protocolVersion", out var protocolVersion, out var versionFailure))
            {
                return WebSocketProtocolParseResult.Invalid(versionFailure);
            }

            if (protocolVersion != WebSocketProtocolV1.ProtocolVersion)
            {
                return WebSocketProtocolParseResult.Invalid(
                    WebSocketProtocolValidationFailure.ProtocolError(
                        WebSocketProtocolErrorCodes.UnsupportedProtocolVersion,
                        "Unsupported protocol version.",
                        "The WebSocket protocol version is not supported.",
                        terminalConnection: true));
            }

            if (!TryGetStringProperty(root, "type", required: true, out var messageType, out var typeFailure))
            {
                return WebSocketProtocolParseResult.Invalid(typeFailure);
            }

            if (!WebSocketMessageTypes.IsSupportedClientMessageType(messageType!))
            {
                return WebSocketProtocolParseResult.Invalid(
                    WebSocketProtocolValidationFailure.ProtocolError(
                        WebSocketProtocolErrorCodes.UnknownMessageType,
                        "Unknown message type.",
                        "The WebSocket message type is not supported."));
            }

            var requestIdResult = ReadRequestId(root);
            if (!requestIdResult.IsValid)
            {
                return WebSocketProtocolParseResult.Invalid(requestIdResult.Failure!);
            }

            var requestId = requestIdResult.RequestId!;
            if (FindForbiddenExecutableField(root) is not null)
            {
                return WebSocketProtocolParseResult.Invalid(
                    WebSocketProtocolValidationFailure.RequestError(
                        requestId,
                        WebSocketProtocolErrorCodes.RequestValidationFailure,
                        "Invalid request.",
                        "Executable fields are not allowed in WebSocket messages.",
                        WebSocketErrorCategories.Request));
            }

            if (!TryReadTimestamp(root, requestId, out var timestamp, out var timestampFailure))
            {
                return WebSocketProtocolParseResult.Invalid(timestampFailure);
            }

            if (!TryGetRequiredObjectProperty(root, "payload", requestId, out var payload, out var payloadFailure))
            {
                return WebSocketProtocolParseResult.Invalid(payloadFailure);
            }

            return messageType switch
            {
                WebSocketMessageTypes.Invoke => ParseInvokeMessage(requestId, timestamp, payload),
                WebSocketMessageTypes.Cancel => ParseCancelMessage(requestId, timestamp, payload),
                _ => WebSocketProtocolParseResult.Invalid(
                    WebSocketProtocolValidationFailure.ProtocolError(
                        WebSocketProtocolErrorCodes.UnknownMessageType,
                        "Unknown message type.",
                        "The WebSocket message type is not supported."))
            };
        }
    }

    public WebSocketProtocolValidationFailure CreateBinaryMessageFailure()
        => WebSocketProtocolValidationFailure.ProtocolError(
            WebSocketProtocolErrorCodes.BinaryNotSupported,
            "Binary messages are not supported.",
            "The WebSocket protocol accepts UTF-8 JSON text messages only.",
            terminalConnection: true);

    public WebSocketProtocolValidationFailure CreateMessageTooLargeFailure()
        => WebSocketProtocolValidationFailure.ProtocolError(
            WebSocketProtocolErrorCodes.MessageTooLarge,
            "Message too large.",
            "The WebSocket message exceeds the configured size limit.",
            terminalConnection: true);

    private static WebSocketProtocolParseResult ParseInvokeMessage(
        string requestId,
        DateTimeOffset? timestamp,
        JsonElement payload)
    {
        if (!TryGetStringProperty(payload, "endpointId", required: true, out var endpointId, out var endpointFailure))
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.RequestError(
                    requestId,
                    WebSocketProtocolErrorCodes.RequestValidationFailure,
                    "Invalid request.",
                    endpointFailure.Detail,
                    WebSocketErrorCategories.Endpoint));
        }

        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > WebSocketProtocolV1.MaximumEndpointIdLength)
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.RequestError(
                    requestId,
                    WebSocketProtocolErrorCodes.RequestValidationFailure,
                    "Invalid endpoint identifier.",
                    "The endpoint ID must be a non-empty string of 256 characters or fewer.",
                    WebSocketErrorCategories.Endpoint));
        }

        if (!TryGetRequiredObjectProperty(payload, "parameters", requestId, out var parametersElement, out var parametersFailure))
        {
            return WebSocketProtocolParseResult.Invalid(parametersFailure with { Category = WebSocketErrorCategories.Parameter });
        }

        if (!TryReadClientMetadata(payload, requestId, out var clientMetadata, out var metadataFailure))
        {
            return WebSocketProtocolParseResult.Invalid(metadataFailure);
        }

        return WebSocketProtocolParseResult.Valid(
            new WebSocketClientMessage(
                WebSocketMessageTypes.Invoke,
                requestId,
                timestamp,
                new WebSocketInvokePayload(
                    endpointId.Trim(),
                    CloneProperties(parametersElement),
                    clientMetadata),
                null));
    }

    private static WebSocketProtocolParseResult ParseCancelMessage(
        string requestId,
        DateTimeOffset? timestamp,
        JsonElement payload)
    {
        if (payload.TryGetProperty("parameters", out _))
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.RequestError(
                    requestId,
                    WebSocketProtocolErrorCodes.RequestValidationFailure,
                    "Invalid cancellation request.",
                    "Cancellation messages cannot include parameter data.",
                    WebSocketErrorCategories.Request));
        }

        if (!TryGetStringProperty(payload, "reason", required: false, out var reason, out var reasonFailure))
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.RequestError(
                    requestId,
                    WebSocketProtocolErrorCodes.RequestValidationFailure,
                    "Invalid cancellation request.",
                    reasonFailure.Detail,
                    WebSocketErrorCategories.Request));
        }

        if (reason is { Length: > WebSocketProtocolV1.MaximumCancelReasonLength })
        {
            return WebSocketProtocolParseResult.Invalid(
                WebSocketProtocolValidationFailure.RequestError(
                    requestId,
                    WebSocketProtocolErrorCodes.RequestValidationFailure,
                    "Invalid cancellation request.",
                    "The cancellation reason must be 128 characters or fewer.",
                    WebSocketErrorCategories.Request));
        }

        return WebSocketProtocolParseResult.Valid(
            new WebSocketClientMessage(
                WebSocketMessageTypes.Cancel,
                requestId,
                timestamp,
                null,
                new WebSocketCancelPayload(reason)));
    }

    private static WebSocketRequestIdReadResult ReadRequestId(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var property))
        {
            return WebSocketRequestIdReadResult.Invalid(
                WebSocketProtocolValidationFailure.ProtocolError(
                    WebSocketProtocolErrorCodes.MissingField,
                    "Missing field.",
                    "The request ID field is required."));
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return WebSocketRequestIdReadResult.Invalid(
                WebSocketProtocolValidationFailure.ProtocolError(
                    WebSocketProtocolErrorCodes.InvalidFieldType,
                    "Invalid field type.",
                    "The request ID field must be a string."));
        }

        var requestId = property.GetString();
        if (!IsValidRequestId(requestId))
        {
            return WebSocketRequestIdReadResult.Invalid(
                WebSocketProtocolValidationFailure.ProtocolError(
                    WebSocketProtocolErrorCodes.InvalidRequestId,
                    "Invalid request ID.",
                    "The request ID must be 1 to 128 characters and contain only letters, digits, underscores, hyphens, periods, or colons."));
        }

        return WebSocketRequestIdReadResult.Valid(requestId!);
    }

    private static bool IsValidRequestId(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId) || requestId.Length > WebSocketProtocolV1.MaximumRequestIdLength)
        {
            return false;
        }

        foreach (var character in requestId)
        {
            if (character is >= 'A' and <= 'Z' ||
                character is >= 'a' and <= 'z' ||
                character is >= '0' and <= '9' ||
                character is '_' or '-' or '.' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryReadTimestamp(
        JsonElement root,
        string requestId,
        out DateTimeOffset? timestamp,
        out WebSocketProtocolValidationFailure failure)
    {
        timestamp = null;
        failure = WebSocketProtocolValidationFailure.None;
        if (!root.TryGetProperty("timestamp", out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            failure = WebSocketProtocolValidationFailure.RequestError(
                requestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invalid request.",
                "The timestamp field must be an ISO 8601 UTC string.",
                WebSocketErrorCategories.Request);
            return false;
        }

        timestamp = parsed;
        return true;
    }

    private static bool TryGetStringProperty(
        JsonElement parent,
        string propertyName,
        bool required,
        out string? value,
        out WebSocketProtocolValidationFailure failure)
    {
        value = null;
        failure = WebSocketProtocolValidationFailure.None;
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            if (!required)
            {
                return true;
            }

            failure = WebSocketProtocolValidationFailure.ProtocolError(
                WebSocketProtocolErrorCodes.MissingField,
                "Missing field.",
                $"The '{propertyName}' field is required.");
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            failure = WebSocketProtocolValidationFailure.ProtocolError(
                WebSocketProtocolErrorCodes.InvalidFieldType,
                "Invalid field type.",
                $"The '{propertyName}' field must be a string.");
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryGetRequiredInt32Property(
        JsonElement parent,
        string propertyName,
        out int value,
        out WebSocketProtocolValidationFailure failure)
    {
        value = 0;
        failure = WebSocketProtocolValidationFailure.None;
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            failure = WebSocketProtocolValidationFailure.ProtocolError(
                WebSocketProtocolErrorCodes.MissingField,
                "Missing field.",
                $"The '{propertyName}' field is required.");
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            failure = WebSocketProtocolValidationFailure.ProtocolError(
                WebSocketProtocolErrorCodes.InvalidFieldType,
                "Invalid field type.",
                $"The '{propertyName}' field must be an integer.");
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredObjectProperty(
        JsonElement parent,
        string propertyName,
        string requestId,
        out JsonElement value,
        out WebSocketProtocolValidationFailure failure)
    {
        value = default;
        failure = WebSocketProtocolValidationFailure.None;
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            failure = WebSocketProtocolValidationFailure.RequestError(
                requestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invalid request.",
                $"The '{propertyName}' field is required.",
                WebSocketErrorCategories.Request);
            return false;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            failure = WebSocketProtocolValidationFailure.RequestError(
                requestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invalid request.",
                $"The '{propertyName}' field must be an object.",
                WebSocketErrorCategories.Request);
            return false;
        }

        value = property;
        return true;
    }

    private static bool TryReadClientMetadata(
        JsonElement payload,
        string requestId,
        out IReadOnlyDictionary<string, JsonElement> clientMetadata,
        out WebSocketProtocolValidationFailure failure)
    {
        clientMetadata = new Dictionary<string, JsonElement>();
        failure = WebSocketProtocolValidationFailure.None;

        if (!payload.TryGetProperty("clientMetadata", out var metadata))
        {
            return true;
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            failure = WebSocketProtocolValidationFailure.RequestError(
                requestId,
                WebSocketProtocolErrorCodes.RequestValidationFailure,
                "Invalid request.",
                "The client metadata field must be an object.",
                WebSocketErrorCategories.Request);
            return false;
        }

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                failure = WebSocketProtocolValidationFailure.RequestError(
                    requestId,
                    WebSocketProtocolErrorCodes.RequestValidationFailure,
                    "Invalid request.",
                    "Client metadata values must be JSON scalars.",
                    WebSocketErrorCategories.Request);
                return false;
            }
        }

        clientMetadata = CloneProperties(metadata);
        return true;
    }

    private static string? FindForbiddenExecutableField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenExecutableFieldNames.Contains(property.Name))
                {
                    return property.Name;
                }

                var nested = FindForbiddenExecutableField(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindForbiddenExecutableField(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, JsonElement> CloneProperties(JsonElement element)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            properties[property.Name] = property.Value.Clone();
        }

        return properties;
    }

    private sealed record WebSocketRequestIdReadResult(
        bool IsValid,
        string? RequestId,
        WebSocketProtocolValidationFailure? Failure)
    {
        public static WebSocketRequestIdReadResult Valid(string requestId)
            => new(true, requestId, null);

        public static WebSocketRequestIdReadResult Invalid(WebSocketProtocolValidationFailure failure)
            => new(false, null, failure);
    }
}

public static class WebSocketProtocolMessageFactory
{
    public static WebSocketProtocolEnvelope<WebSocketProtocolErrorPayload> CreateProtocolError(
        WebSocketProtocolValidationFailure failure,
        DateTimeOffset? timestamp = null)
    {
        if (!string.Equals(failure.MessageType, WebSocketMessageTypes.ProtocolError, StringComparison.Ordinal))
        {
            throw new ArgumentException("The failure is not a protocol error.", nameof(failure));
        }

        return WebSocketProtocolEnvelope<WebSocketProtocolErrorPayload>.Create(
            WebSocketMessageTypes.ProtocolError,
            failure.RequestId,
            new WebSocketProtocolErrorPayload(
                failure.Code,
                failure.Title,
                failure.Detail,
                failure.TerminalConnection),
            timestamp);
    }

    public static WebSocketProtocolEnvelope<WebSocketErrorPayload> CreateRequestError(
        WebSocketProtocolValidationFailure failure,
        DateTimeOffset? timestamp = null)
    {
        if (!string.Equals(failure.MessageType, WebSocketMessageTypes.Error, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(failure.RequestId) ||
            string.IsNullOrWhiteSpace(failure.Category))
        {
            throw new ArgumentException("The failure is not a request error.", nameof(failure));
        }

        return WebSocketProtocolEnvelope<WebSocketErrorPayload>.Create(
            WebSocketMessageTypes.Error,
            failure.RequestId,
            new WebSocketErrorPayload(
                failure.Code,
                failure.Title,
                failure.Detail,
                failure.Category,
                failure.TerminalRequest,
                null,
                null),
            timestamp);
    }

    public static WebSocketProtocolEnvelope<WebSocketErrorPayload> CreateRequestError(
        string requestId,
        ApiInvocationStatus status,
        string category,
        bool terminal = true,
        NormalizationFailureKind failureKind = NormalizationFailureKind.None,
        long? elapsedMilliseconds = null,
        DateTimeOffset? timestamp = null)
    {
        var descriptor = ApiInvocationErrorDescriptorMapper.Describe(status);
        return WebSocketProtocolEnvelope<WebSocketErrorPayload>.Create(
            WebSocketMessageTypes.Error,
            requestId,
            new WebSocketErrorPayload(
                descriptor.Slug,
                descriptor.Title,
                descriptor.Detail,
                category,
                terminal,
                failureKind == NormalizationFailureKind.None ? null : failureKind.ToString(),
                elapsedMilliseconds),
            timestamp);
    }

    public static WebSocketProtocolEnvelope<WebSocketStreamingEventPayload> CreateStreamingEvent(
        string requestId,
        ApiStreamingInvocationEvent item)
        => WebSocketProtocolEnvelope<WebSocketStreamingEventPayload>.Create(
            WebSocketMessageTypes.Event,
            requestId,
            new WebSocketStreamingEventPayload(
                item.InvocationId,
                item.EndpointId,
                item.ConnectionId,
                item.SessionId,
                item.Sequence,
                item.Timestamp,
                item.Kind.ToString(),
                item.Payload,
                item.Message,
                item.StatusCode,
                item.ElapsedMilliseconds,
                item.IsTerminal),
            item.Timestamp);
}

public sealed record WebSocketProtocolEnvelope<TPayload>(
    string Protocol,
    int ProtocolVersion,
    string Type,
    string? RequestId,
    DateTimeOffset Timestamp,
    TPayload Payload)
{
    public static WebSocketProtocolEnvelope<TPayload> Create(
        string type,
        string? requestId,
        TPayload payload,
        DateTimeOffset? timestamp = null)
        => new(
            WebSocketProtocolV1.ProtocolName,
            WebSocketProtocolV1.ProtocolVersion,
            type,
            requestId,
            timestamp ?? DateTimeOffset.UtcNow,
            payload);
}

public sealed record WebSocketClientMessage(
    string Type,
    string RequestId,
    DateTimeOffset? Timestamp,
    WebSocketInvokePayload? Invoke,
    WebSocketCancelPayload? Cancel);

public sealed record WebSocketInvokePayload(
    string EndpointId,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlyDictionary<string, JsonElement> ClientMetadata);

public sealed record WebSocketCancelPayload(string? Reason);

public sealed record WebSocketAcceptedPayload(string EndpointId, string State = "accepted", int? QueuePosition = null);

public sealed record WebSocketEventPayload(string EventType, long Sequence, string? Message = null);

public sealed record WebSocketStreamingEventPayload(
    string InvocationId,
    string EndpointId,
    string? ConnectionId,
    string? SessionId,
    long Sequence,
    DateTimeOffset Timestamp,
    string EventType,
    object? Payload = null,
    string? Message = null,
    string? StatusCode = null,
    long? ElapsedMilliseconds = null,
    bool Terminal = false);

public sealed record WebSocketResultPayload(object? Result, long ElapsedMilliseconds, long? ResultBytes = null)
{
    public string Status { get; } = "success";
}

public sealed record WebSocketErrorPayload(
    string Code,
    string Title,
    string Detail,
    string Category,
    bool Terminal,
    string? FailureKind = null,
    long? ElapsedMilliseconds = null);

public sealed record WebSocketCanceledPayload(bool Accepted, string State);

public sealed record WebSocketProtocolErrorPayload(
    string Code,
    string Title,
    string Detail,
    bool TerminalConnection);

public sealed record WebSocketProtocolParseResult(
    bool IsValid,
    WebSocketClientMessage? Message,
    WebSocketProtocolValidationFailure? Failure)
{
    public static WebSocketProtocolParseResult Valid(WebSocketClientMessage message)
        => new(true, message, null);

    public static WebSocketProtocolParseResult Invalid(WebSocketProtocolValidationFailure failure)
        => new(false, null, failure);
}

public sealed record WebSocketProtocolValidationFailure(
    string MessageType,
    string? RequestId,
    string Code,
    string Title,
    string Detail,
    string? Category,
    bool TerminalRequest,
    bool TerminalConnection)
{
    public static WebSocketProtocolValidationFailure None { get; } =
        new(string.Empty, null, string.Empty, string.Empty, string.Empty, null, false, false);

    public static WebSocketProtocolValidationFailure ProtocolError(
        string code,
        string title,
        string detail,
        string? requestId = null,
        bool terminalConnection = false)
        => new(
            WebSocketMessageTypes.ProtocolError,
            requestId,
            code,
            title,
            detail,
            null,
            false,
            terminalConnection);

    public static WebSocketProtocolValidationFailure RequestError(
        string requestId,
        string code,
        string title,
        string detail,
        string category,
        bool terminalRequest = true)
        => new(
            WebSocketMessageTypes.Error,
            requestId,
            code,
            title,
            detail,
            category,
            terminalRequest,
            false);
}
