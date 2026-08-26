using System.Text.Json;
using PS7ScriptDesk.RestApiProofHost.PowerShell;
using PS7ScriptDesk.RestApiProofHost.WebSockets;

namespace PS7ScriptDesk.Tests;

public sealed class WebSocketProtocolTests
{
    [Fact]
    public void ParseTextMessage_AcceptsValidInvokeEnvelopeByEndpointId()
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(
            """
            {
              "protocol": "ps7scriptdesk.websocket",
              "protocolVersion": 1,
              "type": "invoke",
              "requestId": "req-001",
              "timestamp": "2026-08-26T14:30:00Z",
              "ignored": "safe",
              "payload": {
                "endpointId": "poc-get-systeminfo",
                "parameters": {
                  "computerName": "SRV-01"
                },
                "clientMetadata": {
                  "view": "local",
                  "attempt": 1,
                  "dryRun": false,
                  "note": null
                }
              }
            }
            """);

        Assert.True(result.IsValid, result.Failure?.Detail);
        Assert.NotNull(result.Message);
        Assert.Equal(WebSocketMessageTypes.Invoke, result.Message.Type);
        Assert.Equal("req-001", result.Message.RequestId);
        Assert.NotNull(result.Message.Timestamp);
        Assert.NotNull(result.Message.Invoke);
        Assert.Null(result.Message.Cancel);
        Assert.Equal("poc-get-systeminfo", result.Message.Invoke.EndpointId);
        Assert.Equal("SRV-01", result.Message.Invoke.Parameters["computerName"].GetString());
        Assert.False(result.Message.Invoke.Parameters.ContainsKey("functionName"));
        Assert.Equal(4, result.Message.Invoke.ClientMetadata.Count);
    }

    [Theory]
    [InlineData("", WebSocketProtocolErrorCodes.EmptyMessage)]
    [InlineData("   ", WebSocketProtocolErrorCodes.EmptyMessage)]
    [InlineData("{", WebSocketProtocolErrorCodes.MalformedJson)]
    [InlineData("[]", WebSocketProtocolErrorCodes.EnvelopeNotObject)]
    [InlineData("null", WebSocketProtocolErrorCodes.EnvelopeNotObject)]
    public void ParseTextMessage_RejectsMalformedEmptyOrNonObjectJson(string message, string expectedCode)
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(message);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(WebSocketMessageTypes.ProtocolError, result.Failure.MessageType);
        Assert.Equal(expectedCode, result.Failure.Code);
        Assert.Null(result.Failure.RequestId);
    }

    [Theory]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 2,
          "type": "invoke",
          "requestId": "req-001",
          "payload": { "endpointId": "poc", "parameters": {} }
        }
        """, WebSocketProtocolErrorCodes.UnsupportedProtocolVersion)]
    [InlineData("""
        {
          "protocol": "other.protocol",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": { "endpointId": "poc", "parameters": {} }
        }
        """, WebSocketProtocolErrorCodes.InvalidProtocol)]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "runFunction",
          "requestId": "req-001",
          "payload": { "endpointId": "poc", "parameters": {} }
        }
        """, WebSocketProtocolErrorCodes.UnknownMessageType)]
    public void ParseTextMessage_RejectsUnsupportedProtocolNameVersionOrType(string message, string expectedCode)
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(message);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(WebSocketMessageTypes.ProtocolError, result.Failure.MessageType);
        Assert.Equal(expectedCode, result.Failure.Code);
    }

    [Theory]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": "1",
          "type": "invoke",
          "requestId": "req-001",
          "payload": { "endpointId": "poc", "parameters": {} }
        }
        """, WebSocketMessageTypes.ProtocolError, WebSocketProtocolErrorCodes.InvalidFieldType)]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": []
        }
        """, WebSocketMessageTypes.Error, WebSocketProtocolErrorCodes.RequestValidationFailure)]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": { "endpointId": "poc", "parameters": [] }
        }
        """, WebSocketMessageTypes.Error, WebSocketProtocolErrorCodes.RequestValidationFailure)]
    public void ParseTextMessage_RejectsInvalidFieldTypes(string message, string expectedMessageType, string expectedCode)
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(message);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(expectedMessageType, result.Failure.MessageType);
        Assert.Equal(expectedCode, result.Failure.Code);
    }

    [Theory]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": { "parameters": {} }
        }
        """, WebSocketErrorCategories.Endpoint)]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": { "endpointId": "poc" }
        }
        """, WebSocketErrorCategories.Parameter)]
    public void ParseTextMessage_RejectsMissingInvokeRequiredFields(string message, string expectedCategory)
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(message);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(WebSocketMessageTypes.Error, result.Failure.MessageType);
        Assert.Equal("req-001", result.Failure.RequestId);
        Assert.Equal(WebSocketProtocolErrorCodes.RequestValidationFailure, result.Failure.Code);
        Assert.Equal(expectedCategory, result.Failure.Category);
        Assert.True(result.Failure.TerminalRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/not/allowed")]
    [InlineData("brace{not-allowed}")]
    public void ParseTextMessage_RejectsInvalidRequestIds(string requestId)
    {
        var message = $$"""
            {
              "protocol": "ps7scriptdesk.websocket",
              "protocolVersion": 1,
              "type": "invoke",
              "requestId": "{{requestId}}",
              "payload": { "endpointId": "poc", "parameters": {} }
            }
            """;

        var result = WebSocketProtocolParser.Shared.ParseTextMessage(message);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(WebSocketMessageTypes.ProtocolError, result.Failure.MessageType);
        Assert.Equal(WebSocketProtocolErrorCodes.InvalidRequestId, result.Failure.Code);
    }

    [Theory]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "functionName": "Get-SystemInfo",
          "payload": { "endpointId": "poc", "parameters": {} }
        }
        """)]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": {
            "endpointId": "poc",
            "command": "Get-Process",
            "parameters": {}
          }
        }
        """)]
    [InlineData("""
        {
          "protocol": "ps7scriptdesk.websocket",
          "protocolVersion": 1,
          "type": "invoke",
          "requestId": "req-001",
          "payload": {
            "endpointId": "poc",
            "parameters": {
              "scriptBlock": "Get-ChildItem"
            }
          }
        }
        """)]
    public void ParseTextMessage_RejectsExecutableLookingFields(string message)
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(message);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(WebSocketMessageTypes.Error, result.Failure.MessageType);
        Assert.Equal("req-001", result.Failure.RequestId);
        Assert.Equal(WebSocketProtocolErrorCodes.RequestValidationFailure, result.Failure.Code);
        Assert.Equal(WebSocketErrorCategories.Request, result.Failure.Category);
        Assert.DoesNotContain("Get-SystemInfo", result.Failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-ChildItem", result.Failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTextMessage_AcceptsCancelEnvelopeWithoutParameters()
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(
            """
            {
              "protocol": "ps7scriptdesk.websocket",
              "protocolVersion": 1,
              "type": "cancel",
              "requestId": "req-001",
              "payload": {
                "reason": "clientRequested"
              }
            }
            """);

        Assert.True(result.IsValid, result.Failure?.Detail);
        Assert.NotNull(result.Message);
        Assert.Equal(WebSocketMessageTypes.Cancel, result.Message.Type);
        Assert.Null(result.Message.Invoke);
        Assert.NotNull(result.Message.Cancel);
        Assert.Equal("clientRequested", result.Message.Cancel.Reason);
    }

    [Fact]
    public void ParseTextMessage_RejectsCancelEnvelopeWithParameterData()
    {
        var result = WebSocketProtocolParser.Shared.ParseTextMessage(
            """
            {
              "protocol": "ps7scriptdesk.websocket",
              "protocolVersion": 1,
              "type": "cancel",
              "requestId": "req-001",
              "payload": {
                "parameters": { "name": "value" }
              }
            }
            """);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Failure);
        Assert.Equal(WebSocketMessageTypes.Error, result.Failure.MessageType);
        Assert.Equal(WebSocketProtocolErrorCodes.RequestValidationFailure, result.Failure.Code);
        Assert.Contains("parameter", result.Failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBinaryMessageFailure_ConstructsSanitizedProtocolErrorPayload()
    {
        var failure = WebSocketProtocolParser.Shared.CreateBinaryMessageFailure();
        var envelope = WebSocketProtocolMessageFactory.CreateProtocolError(
            failure,
            DateTimeOffset.Parse("2026-08-26T14:30:00Z"));

        Assert.Equal(WebSocketProtocolV1.ProtocolName, envelope.Protocol);
        Assert.Equal(WebSocketProtocolV1.ProtocolVersion, envelope.ProtocolVersion);
        Assert.Equal(WebSocketMessageTypes.ProtocolError, envelope.Type);
        Assert.Null(envelope.RequestId);
        Assert.Equal(WebSocketProtocolErrorCodes.BinaryNotSupported, envelope.Payload.Code);
        Assert.True(envelope.Payload.TerminalConnection);
        Assert.DoesNotContain("secret", envelope.Payload.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", envelope.Payload.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateRequestError_ConstructsSanitizedValidationErrorPayload()
    {
        var parseResult = WebSocketProtocolParser.Shared.ParseTextMessage(
            """
            {
              "protocol": "ps7scriptdesk.websocket",
              "protocolVersion": 1,
              "type": "invoke",
              "requestId": "req-001",
              "payload": { "endpointId": "poc" }
            }
            """);

        Assert.False(parseResult.IsValid);
        Assert.NotNull(parseResult.Failure);

        var envelope = WebSocketProtocolMessageFactory.CreateRequestError(
            parseResult.Failure,
            DateTimeOffset.Parse("2026-08-26T14:30:00Z"));

        Assert.Equal(WebSocketMessageTypes.Error, envelope.Type);
        Assert.Equal("req-001", envelope.RequestId);
        Assert.Equal(WebSocketProtocolErrorCodes.RequestValidationFailure, envelope.Payload.Code);
        Assert.Equal(WebSocketErrorCategories.Parameter, envelope.Payload.Category);
        Assert.True(envelope.Payload.Terminal);
        Assert.DoesNotContain("secret", envelope.Payload.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", envelope.Payload.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateRequestError_UsesSharedSanitizedInvocationDescriptors()
    {
        var envelope = WebSocketProtocolMessageFactory.CreateRequestError(
            "req-001",
            ApiInvocationStatus.PowerShellValidationFailure,
            WebSocketErrorCategories.Execution,
            elapsedMilliseconds: 42,
            timestamp: DateTimeOffset.Parse("2026-08-26T14:30:03Z"));

        Assert.Equal(WebSocketMessageTypes.Error, envelope.Type);
        Assert.Equal("req-001", envelope.RequestId);
        Assert.Equal("powershell-validation-failure", envelope.Payload.Code);
        Assert.Equal("PowerShell validation failed.", envelope.Payload.Title);
        Assert.Equal("The PowerShell invocation parameters failed validation.", envelope.Payload.Detail);
        Assert.Equal(WebSocketErrorCategories.Execution, envelope.Payload.Category);
        Assert.True(envelope.Payload.Terminal);
        Assert.Equal(42, envelope.Payload.ElapsedMilliseconds);
        Assert.DoesNotContain("secret", envelope.Payload.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", envelope.Payload.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
