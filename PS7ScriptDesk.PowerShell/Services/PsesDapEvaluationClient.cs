using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Stages editor script code in PSES's authoritative runspace through its DAP
/// REPL evaluation handler. That handler does not echo private code to ConPTY;
/// the foreground language-service evaluation invokes the staged code.
/// </summary>
internal static class PsesDapEvaluationClient
{
    public static async Task<bool> RemoveStagedScriptAsync(
        string pipePath, Guid requestId, int sessionGeneration, string variableName)
    {
        // A canceled staging evaluation can finish just before its cancel response.
        // Remove its request-unique launcher through the same PSES runspace before
        // allowing another editor request to proceed.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var pipeName = pipePath.Replace("\\\\.\\pipe\\", string.Empty, StringComparison.OrdinalIgnoreCase);
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var initialized = await SendAsync(pipe, 1, "initialize", new
            {
                adapterID = "PS7ScriptDesk", clientID = "PS7ScriptDesk",
                clientName = "PS7 ScriptDesk staged execution cleanup",
                linesStartAt1 = true, columnsStartAt1 = true, pathFormat = "path",
                supportsVariablePaging = false, supportsRunInTerminalRequest = false
            }, timeout.Token).ConfigureAwait(false);
            if (!initialized) return false;
            var ack = "PS7SD_CLEANUP_ACK_" + requestId.ToString("N");
            var expression = $"Remove-Variable -Name '{variableName}' -Scope Global -ErrorAction SilentlyContinue; " +
                $"if (Get-Variable -Name '{variableName}' -Scope Global -ErrorAction SilentlyContinue) {{ 'PS7SD_CLEANUP_PRESENT_{requestId:N}' }} else {{ '{ack}' }}";
            var response = await SendForResponseAsync(pipe, 2, "evaluate", new { expression, context = "repl" }, timeout.Token).ConfigureAwait(false);
            var removed = IsSuccessful(response) && response.TryGetProperty("body", out var body) &&
                body.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String &&
                string.Equals(result.GetString()?.Trim(), ack, StringComparison.Ordinal);
            if (removed)
            {
                try
                {
                    await SendAsync(pipe, 3, "disconnect", new { terminateDebuggee = false, restart = false }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
                {
                    // The request-correlated evaluate response already proved the
                    // launcher absent. A best-effort client disconnect must not
                    // invalidate that cleanup acknowledgment.
                    DeveloperDiagnostics.LogWarning("PSES", "Staged launcher cleanup was acknowledged; DAP disconnect did not complete before the bounded deadline.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["exceptionType"] = ex.GetType().Name
                    });
                }
            }
            DeveloperDiagnostics.LogInfo("PSES", "Canceled staged launcher cleanup completed.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId, ["sessionGeneration"] = sessionGeneration,
                ["cleanupAcknowledged"] = removed, ["contentOmitted"] = true
            });
            return removed;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogWarning("PSES", "Canceled staged launcher cleanup could not be confirmed.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId, ["sessionGeneration"] = sessionGeneration,
                ["exceptionType"] = ex.GetType().Name, ["contentOmitted"] = true
            });
            return false;
        }
    }

    public static async Task<PsesStructuredEvaluationResult> EvaluateAsync(
        string pipePath, Guid requestId, int sessionGeneration, string expression, CancellationToken cancellationToken,
        Action? onEvaluationSubmitted = null, Action<bool>? onEvaluationSettled = null)
    {
        DeveloperDiagnostics.LogInfo("PSES", "Private DAP evaluation connecting to authoritative debug endpoint.", new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionGeneration"] = sessionGeneration,
            ["expressionLength"] = expression.Length,
            ["contentOmitted"] = true
        });
        var pipeName = pipePath.Replace("\\\\.\\pipe\\", string.Empty, StringComparison.OrdinalIgnoreCase);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

        using var initializeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        initializeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var initialized = await SendAsync(pipe, 1, "initialize", new
        {
            adapterID = "PS7ScriptDesk",
            clientID = "PS7ScriptDesk",
            clientName = "PS7 ScriptDesk editor execution",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            pathFormat = "path",
            supportsVariablePaging = false,
            supportsRunInTerminalRequest = false
        }, initializeTimeout.Token).ConfigureAwait(false);
        DeveloperDiagnostics.LogInfo("PSES", "Private DAP evaluation initialization completed.", new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionGeneration"] = sessionGeneration,
            ["accepted"] = initialized
        });
        if (!initialized)
            return new(EditorExecutionStatus.Unknown, false, false, "DAP initialize failed", "PSES rejected the private execution connection.");

        bool evaluated;
        try
        {
            onEvaluationSubmitted?.Invoke();
            evaluated = await SendAsync(pipe, 2, "evaluate", new { expression, context = "repl" }, cancellationToken).ConfigureAwait(false);
            onEvaluationSettled?.Invoke(evaluated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // DAP cancellation must reach PSES before the pipe is disposed.
            // Closing the client alone leaves the foreground pipeline running.
            using var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var responseReceived = false;
            var settled = false;
            try
            {
                await WriteRequestAsync(pipe, 3, "cancel", new { requestId = 2 }, settleTimeout.Token).ConfigureAwait(false);
                var response = await WaitForResponseMessageAsync(pipe, 2, settleTimeout.Token).ConfigureAwait(false);
                responseReceived = true;
                settled = IsSuccessful(response);
                onEvaluationSettled?.Invoke(settled);
                DeveloperDiagnostics.LogInfo("PSES", "Private DAP evaluation cancellation settled.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["responseSuccess"] = settled
                });
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
            {
                DeveloperDiagnostics.LogWarning("PSES", "Private DAP evaluation cancellation did not receive a settled response.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["exceptionType"] = ex.GetType().Name
                });
            }
            if (!responseReceived)
            {
                // No terminal response means staging may still complete later. The
                // caller must retain conservative readiness and must not race cleanup
                // against this outstanding operation.
                throw new OperationCanceledException(cancellationToken);
            }
            return settled
                ? new(EditorExecutionStatus.Completed, true, true, null, null)
                : new(EditorExecutionStatus.Unknown, true, true, "DAP evaluate failed", "PSES rejected the private execution request.");
        }
        try
        {
            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(pipe, 3, "disconnect", new { terminateDebuggee = false, restart = false }, disconnectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            DeveloperDiagnostics.LogWarning("PSES", "Private DAP evaluation disconnect did not complete cleanly; pipe disposal follows.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["exceptionType"] = ex.GetType().Name
            });
            // Closing the pipe below still releases this short-lived DAP client.
        }
        DeveloperDiagnostics.LogInfo("PSES", "Private DAP evaluation response received.", new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionGeneration"] = sessionGeneration,
            ["accepted"] = evaluated
        });
        return evaluated
            ? new(EditorExecutionStatus.Completed, true, true, null, null)
            : new(EditorExecutionStatus.Unknown, true, true, "DAP evaluate failed", "PSES rejected the private execution request.");
    }

    private static async Task<bool> SendAsync(Stream stream, int sequence, string command, object arguments, CancellationToken cancellationToken)
    {
        await WriteRequestAsync(stream, sequence, command, arguments, cancellationToken).ConfigureAwait(false);
        return await WaitForResponseAsync(stream, sequence, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRequestAsync(Stream stream, int sequence, string command, object arguments, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { seq = sequence, type = "request", command, arguments });
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForResponseAsync(Stream stream, int sequence, CancellationToken cancellationToken)
        => IsSuccessful(await WaitForResponseMessageAsync(stream, sequence, cancellationToken).ConfigureAwait(false));

    private static async Task<JsonElement> SendForResponseAsync(Stream stream, int sequence, string command, object arguments, CancellationToken cancellationToken)
    {
        await WriteRequestAsync(stream, sequence, command, arguments, cancellationToken).ConfigureAwait(false);
        return await WaitForResponseMessageAsync(stream, sequence, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSuccessful(JsonElement response)
        => response.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

    private static async Task<JsonElement> WaitForResponseMessageAsync(Stream stream, int sequence, CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.TryGetProperty("type", out var type) || type.GetString() != "response" ||
                !response.TryGetProperty("request_seq", out var requestSequence) || requestSequence.GetInt32() != sequence)
                continue;
            return response;
        }
    }

    private static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < 16 * 1024)
        {
            await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
            header.Add(one[0]);
            var n = header.Count;
            if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n')
                break;
        }
        if (header.Count >= 16 * 1024) throw new InvalidDataException("PSES DAP header exceeded the limit.");
        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var contentLengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLengthLine is null || !int.TryParse(contentLengthLine["Content-Length:".Length..].Trim(), out var length) ||
            length < 0 || length > 8 * 1024 * 1024)
            throw new InvalidDataException("PSES DAP content length was invalid.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }
}
