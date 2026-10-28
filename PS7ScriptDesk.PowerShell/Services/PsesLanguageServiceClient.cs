using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Request-capable client for the pinned VS Code/PSES language-service contract.</summary>
public sealed class PsesLanguageServiceClient : IPsesStructuredExecutionAdapter
{
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, int> _requestIds = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, int> _openDocumentVersions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetime;
    private Stream? _pipe;
    private Task? _readerTask;
    private Func<string, CancellationToken, Task>? _sendSyntheticTerminalInputAsync;
    private bool _busy;
    private long _busyNotificationSequence;
    private long _lastIdleNotificationSequence;
    private int _nextRequestId;
    private int _disposed;

    public event Action<bool>? BusyChanged;
    public event Action<Exception>? SessionFaulted;
    public int SessionGeneration { get; private set; }
    public bool IsInitialized { get; private set; }
    internal int PendingRequestCount => _pending.Count;

    public async Task InitializeAsync(string languageServicePipePath, int processId, int sessionGeneration, Func<string, CancellationToken, Task> sendSyntheticTerminalInputAsync, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageServicePipePath);
        ArgumentNullException.ThrowIfNull(sendSyntheticTerminalInputAsync);
        ThrowIfDisposed();
        if (IsInitialized) throw new InvalidOperationException("The PSES language-service client is already initialized.");
        var pipeName = languageServicePipePath[(languageServicePipePath.LastIndexOf('\\') + 1)..];
        if (string.IsNullOrWhiteSpace(pipeName)) throw new InvalidOperationException("PSES language-service pipe name was empty.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await InitializeConnectedAsync(pipe, processId, sessionGeneration, sendSyntheticTerminalInputAsync, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The stream is still local when ConnectAsync or the handshake fails.
            // Dispose it here so failed startup cannot strand a connected pipe.
            pipe.Dispose();
            throw;
        }
    }

    internal async Task InitializeConnectedAsync(Stream transport, int processId, int sessionGeneration, Func<string, CancellationToken, Task> sendSyntheticTerminalInputAsync, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sendSyntheticTerminalInputAsync);
        ThrowIfDisposed();
        if (IsInitialized || _pipe is not null) throw new InvalidOperationException("The PSES language-service client is already initialized.");
        _sendSyntheticTerminalInputAsync = sendSyntheticTerminalInputAsync;
        SessionGeneration = sessionGeneration;
        _pipe = transport;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = Task.Run(() => ReadLoopAsync(_lifetime.Token), CancellationToken.None);
        try
        {
            var initialize = await SendRequestAsync("initialize", new
            {
                processId = (int?)null,
                clientInfo = new { name = "Visual Studio Code", version = "1.105.0" },
                locale = "en", rootPath = (string?)null, rootUri = (string?)null,
                capabilities = new { }, trace = "off", workspaceFolders = (object?)null,
                initializationOptions = new { enableProfileLoading = false, initialWorkingDirectory = Environment.CurrentDirectory, shellIntegrationScript = (string?)null }
            }, cancellationToken).ConfigureAwait(false);
            if (initialize.TryGetProperty("error", out var initializeError)) throw new InvalidOperationException($"PSES initialize failed: {initializeError.GetRawText()}");
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("workspace/didChangeConfiguration", new { settings = new { } }, cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
            DeveloperDiagnostics.LogInfo("PSES", "PSES language-service adapter initialized.", Metadata());
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PsesStructuredEvaluationResult> EvaluateAsync(Guid requestId, string expression, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression); ThrowIfDisposed();
        if (!IsInitialized) throw new InvalidOperationException("The PSES language-service client is not initialized.");
        await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var observedBusy = false;
        try
        {
            observedBusy = IsBusy;
            var evaluationStartNotificationSequence = BusyNotificationSequence;
            if (observedBusy) await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);

            var response = await SendRequestAsync("evaluate", new { expression }, cancellationToken, requestId).ConfigureAwait(false);
            if (response.TryGetProperty("error", out var protocolError)) return new(EditorExecutionStatus.Failed, true, observedBusy || IsBusy, protocolError.GetRawText(), "PSES returned a protocol error.");
            var completed = await WaitForPostEvaluateIdleAsync(evaluationStartNotificationSequence, cancellationToken).ConfigureAwait(false);
            if (!completed)
            {
                return new(EditorExecutionStatus.Failed, true, observedBusy || IsBusy, null,
                    "PSES returned an evaluate response without a post-evaluate idle transition; execution completion was not proven.");
            }
            return new(EditorExecutionStatus.Completed, true, observedBusy || IsBusy, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (TimeoutException ex)
        { return new(EditorExecutionStatus.Failed, false, observedBusy || IsBusy, null, ex.Message); }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        { return new(EditorExecutionStatus.Failed, false, observedBusy || IsBusy, null, "The PSES language-service session was lost."); }
        finally { _evaluationGate.Release(); }
    }

    public async Task<AuthoritativeCompletionResult?> GetLiveCompletionsAsync(
        string documentUri,
        string documentText,
        int cursorOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUri);
        ArgumentNullException.ThrowIfNull(documentText);
        ThrowIfDisposed();
        if (!IsInitialized)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        await _completionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        try
        {
            var version = _openDocumentVersions.TryGetValue(documentUri, out var priorVersion)
                ? priorVersion + 1
                : 1;
            var normalizedCursor = Math.Clamp(cursorOffset, 0, documentText.Length);
            var position = ToDocumentPosition(documentText, normalizedCursor);
            if (priorVersion == 0)
            {
                await SendNotificationAsync(
                    "textDocument/didOpen",
                    new { textDocument = new { uri = documentUri, languageId = "powershell", version, text = documentText } },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendNotificationAsync(
                    "textDocument/didChange",
                    new { textDocument = new { uri = documentUri, version }, contentChanges = new[] { new { text = documentText } } },
                    cancellationToken).ConfigureAwait(false);
            }

            _openDocumentVersions[documentUri] = version;
            DeveloperDiagnostics.LogInfo("PSES", "Authoritative live completion request sent.", Metadata(new()
            {
                ["requestId"] = requestId,
                ["documentUri"] = DeveloperDiagnostics.SanitizePath(documentUri),
                ["documentVersion"] = version,
                ["documentLength"] = documentText.Length,
                ["cursorOffset"] = normalizedCursor,
                ["line"] = position.Line,
                ["character"] = position.Character
            }));

            var response = await SendRequestAsync(
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = documentUri },
                    position = new { line = position.Line, character = position.Character },
                    context = new { triggerKind = 1 }
                },
                cancellationToken,
                requestId).ConfigureAwait(false);

            var result = ParseCompletionResult(response, documentText, normalizedCursor);
            DeveloperDiagnostics.LogInfo("PSES", "Authoritative live completion response received.", Metadata(new()
            {
                ["requestId"] = requestId,
                ["documentUri"] = DeveloperDiagnostics.SanitizePath(documentUri),
                ["documentVersion"] = version,
                ["itemCount"] = result?.Items.Count ?? 0,
                ["replacementIndex"] = result?.ReplacementIndex,
                ["replacementLength"] = result?.ReplacementLength,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["processId"] = Environment.ProcessId
            }));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeveloperDiagnostics.LogInfo("PSES", "Authoritative live completion request canceled.", Metadata(new() { ["requestId"] = requestId, ["elapsedMs"] = stopwatch.ElapsedMilliseconds }));
            throw;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            DeveloperDiagnostics.LogWarning("PSES", $"Authoritative live completion request failed: {ex.Message}", Metadata(new() { ["requestId"] = requestId, ["exceptionType"] = ex.GetType().Name, ["elapsedMs"] = stopwatch.ElapsedMilliseconds }));
            return null;
        }
        finally
        {
            _completionGate.Release();
        }
    }

    public async Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        if (_requestIds.TryGetValue(requestId, out var protocolId))
        {
            await SendNotificationAsync("$/cancelRequest", new { id = protocolId }, cancellationToken).ConfigureAwait(false);
            DeveloperDiagnostics.LogInfo("PSES", "PSES request cancellation notification sent; pipeline interruption remains unproven.", Metadata(new() { ["requestId"] = requestId, ["protocolId"] = protocolId }));
        }
    }

    private async Task CancelRequestBestEffortAsync(Guid requestId)
    {
        try
        {
            await CancelRequestAsync(requestId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
        {
            DeveloperDiagnostics.LogWarning("PSES", "PSES request cancellation could not be delivered because the language-service transport was already unavailable.", Metadata(new()
            {
                ["requestId"] = requestId,
                ["exceptionType"] = ex.GetType().Name
            }));
        }
    }

    private bool IsBusy { get { lock (_stateGate) return _busy; } }
    private long BusyNotificationSequence { get { lock (_stateGate) return _busyNotificationSequence; } }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken, Guid? requestId = null)
    {
        var protocolId = Interlocked.Increment(ref _nextRequestId); var pending = new PendingRequest();
        if (!_pending.TryAdd(protocolId, pending)) throw new InvalidOperationException("Duplicate PSES request identifier.");
        if (requestId is { } requestKey) _requestIds[requestKey] = protocolId;
        try
        {
            await SendMessageAsync(new { jsonrpc = "2.0", id = protocolId, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            try
            {
                return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (requestId is not null)
            {
                await CancelRequestBestEffortAsync(requestId.Value).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (requestId is not null)
            {
                // Keep the request correlation entry alive until the cancellation
                // notification has been attempted. A fire-and-forget registration
                // races the finally block and can otherwise remove the protocol id
                // before PSES receives $/cancelRequest.
                await CancelRequestBestEffortAsync(requestId.Value).ConfigureAwait(false);
                throw;
            }
        }
        finally { _pending.TryRemove(protocolId, out _); if (requestId is { } requestKey2) _requestIds.TryRemove(requestKey2, out _); }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) => SendMessageAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("PSES language-service pipe is not connected."); var body = JsonSerializer.Serialize(message); var payload = Encoding.UTF8.GetBytes(body);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await pipe.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), cancellationToken).ConfigureAwait(false); await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false); await pipe.FlushAsync(cancellationToken).ConfigureAwait(false); DeveloperDiagnostics.LogInfo("PSES", "PSES protocol message sent.", Metadata(new() { ["byteCount"] = payload.Length, ["contentOmitted"] = true })); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var body = await ReadFrameAsync(cancellationToken).ConfigureAwait(false); if (body is null) throw new EndOfStreamException("PSES language-service pipe closed.");
                using var document = JsonDocument.Parse(body); var root = document.RootElement;
                if (root.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && _pending.TryGetValue(responseId.GetInt32(), out var pending)) { pending.Completion.TrySetResult(root.Clone()); continue; }
                if (root.TryGetProperty("method", out var method))
                    _ = DispatchNotificationSafelyAsync(method.GetString() ?? string.Empty, root.TryGetProperty("params", out var parameters) ? parameters.Clone() : default, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FailPending(new OperationCanceledException("The PSES language-service lifetime was canceled.", cancellationToken));
            IsInitialized = false;
        }
        catch (Exception ex)
        {
            FailPending(ex);
            IsInitialized = false;
            DeveloperDiagnostics.LogException("PSES", ex, "PSES language-service reader terminated; session generation is no longer usable.", Metadata());
            try { SessionFaulted?.Invoke(ex); } catch (Exception subscriberException) { DeveloperDiagnostics.LogException("PSES", subscriberException, "PSES session-fault subscriber failed."); }
        }
    }

    private async Task DispatchNotificationSafelyAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        try { await DispatchNotificationAsync(method, parameters, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
        {
            FailPending(ex);
            IsInitialized = false;
            try { SessionFaulted?.Invoke(ex); } catch { }
            DeveloperDiagnostics.LogException("PSES", ex, "PSES notification dispatch failed; session generation is no longer usable.", Metadata());
        }
    }

    private async Task DispatchNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == "powerShell/executionBusyStatus" && parameters.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var busy = parameters.GetBoolean();
            lock (_stateGate)
            {
                _busy = busy;
                _busyNotificationSequence++;
                if (!busy) _lastIdleNotificationSequence = _busyNotificationSequence;
            }
            try { BusyChanged?.Invoke(busy); } catch (Exception ex) { DeveloperDiagnostics.LogException("PSES", ex, "PSES busy notification subscriber failed."); } return;
        }
        if (method == "powerShell/sendKeyPress" && _sendSyntheticTerminalInputAsync is not null)
        {
            DeveloperDiagnostics.LogInfo("PSES", "PSES requested the VS Code-compatible synthetic terminal key.", Metadata(new() { ["syntheticInput"] = "p" }));
            await _sendSyntheticTerminalInputAsync("p", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForIdleAsync(CancellationToken cancellationToken) { while (IsBusy) await Task.Delay(25, cancellationToken).ConfigureAwait(false); }

    private async Task<bool> WaitForPostEvaluateIdleAsync(long evaluationStartNotificationSequence, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (_stateGate)
            {
                if (!_busy && _lastIdleNotificationSequence > evaluationStartNotificationSequence)
                {
                    return true;
                }
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        DeveloperDiagnostics.LogWarning("PSES", "PSES evaluate response arrived without an observed post-evaluate idle transition; completion remains unproven and the evaluation is being reported as failed.", Metadata(new()
        {
            ["evaluationStartNotificationSequence"] = evaluationStartNotificationSequence,
            ["busyNotificationSequence"] = BusyNotificationSequence
        }));
        return false;
    }

    private async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("PSES language-service pipe is not connected."); var header = new List<byte>();
        while (true) { var one = new byte[1]; var count = await pipe.ReadAsync(one, cancellationToken).ConfigureAwait(false); if (count == 0) return null; header.Add(one[0]); if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break; if (header.Count >= 2 && header[^2] == 10 && header[^1] == 10) break; if (header.Count > 8192) throw new InvalidDataException("PSES protocol header exceeded the bounded limit."); }
        var headerText = Encoding.ASCII.GetString(header.ToArray()); var contentLine = headerText.Split('\n').FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLine is null || !int.TryParse(contentLine[15..].Trim(), out var length) || length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("PSES protocol content length was invalid.");
        var body = new byte[length]; for (var offset = 0; offset < length;) { var count = await pipe.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false); if (count == 0) return null; offset += count; } return Encoding.UTF8.GetString(body);
    }

    private void FailPending(Exception exception) { foreach (var pair in _pending) pair.Value.Completion.TrySetException(exception); _pending.Clear(); _requestIds.Clear(); }

    private static AuthoritativeCompletionResult? ParseCompletionResult(JsonElement response, string documentText, int cursorOffset)
    {
        if (!response.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var itemsElement = result.ValueKind == JsonValueKind.Array
            ? result
            : result.TryGetProperty("items", out var listedItems) ? listedItems : default;
        if (itemsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<AuthoritativeCompletionItem>();
        var replacementIndex = cursorOffset;
        var replacementLength = 0;
        var replacementResolved = false;
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("label", out var labelElement)) continue;
            var label = labelElement.GetString() ?? string.Empty;
            var completionText = item.TryGetProperty("insertText", out var insertTextElement)
                ? insertTextElement.GetString() ?? label
                : item.TryGetProperty("textEdit", out var textEditValue) && textEditValue.TryGetProperty("newText", out var newTextElement)
                    ? newTextElement.GetString() ?? label
                    : label;
            var tooltip = item.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() ?? string.Empty : string.Empty;
            var kind = item.TryGetProperty("kind", out var kindElement) && kindElement.TryGetInt32(out var numericKind) ? numericKind : 1;

            if (!replacementResolved && item.TryGetProperty("textEdit", out var textEdit) && textEdit.ValueKind == JsonValueKind.Object &&
                textEdit.TryGetProperty("range", out var range) && TryGetRangeOffsets(range, documentText, out var rangeStart, out var rangeEnd))
            {
                replacementIndex = rangeStart;
                replacementLength = Math.Max(0, rangeEnd - rangeStart);
                replacementResolved = true;
            }

            if (!string.IsNullOrWhiteSpace(completionText))
                items.Add(new AuthoritativeCompletionItem(completionText, string.IsNullOrWhiteSpace(label) ? completionText : label, kind, tooltip));
        }

        return items.Count == 0 ? null : new AuthoritativeCompletionResult(replacementIndex, replacementLength, items);
    }

    private static bool TryGetRangeOffsets(JsonElement range, string documentText, out int start, out int end)
    {
        start = end = 0;
        if (!TryGetPosition(range, "start", out var startPosition) || !TryGetPosition(range, "end", out var endPosition)) return false;
        start = PositionToOffset(documentText, startPosition.Line, startPosition.Character);
        end = PositionToOffset(documentText, endPosition.Line, endPosition.Character);
        return true;
    }

    private static bool TryGetPosition(JsonElement parent, string name, out DocumentPosition position)
    {
        position = default;
        if (!parent.TryGetProperty(name, out var element) || !element.TryGetProperty("line", out var lineElement) ||
            !element.TryGetProperty("character", out var characterElement) || !lineElement.TryGetInt32(out var line) ||
            !characterElement.TryGetInt32(out var character)) return false;
        position = new DocumentPosition(Math.Max(0, line), Math.Max(0, character));
        return true;
    }

    private static DocumentPosition ToDocumentPosition(string documentText, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (documentText[index] == '\n') { line++; lineStart = index + 1; }
        }
        return new DocumentPosition(line, offset - lineStart);
    }

    private static int PositionToOffset(string documentText, int line, int character)
    {
        var currentLine = 0;
        var offset = 0;
        while (offset < documentText.Length && currentLine < line) if (documentText[offset++] == '\n') currentLine++;
        return Math.Clamp(offset + character, 0, documentText.Length);
    }

    private readonly record struct DocumentPosition(int Line, int Character);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _lifetime?.Cancel(); _pipe?.Dispose(); if (_readerTask is not null) try { await _readerTask.ConfigureAwait(false); } catch { }
        FailPending(new ObjectDisposedException(nameof(PsesLanguageServiceClient))); _lifetime?.Dispose();
        // An evaluation may still be unwinding after lifetime cancellation. Keep
        // the semaphores valid so its finally path can release them safely.
        IsInitialized = false;
    }

    private Dictionary<string, object?> Metadata(Dictionary<string, object?>? extra = null) { var result = new Dictionary<string, object?> { ["sessionGeneration"] = SessionGeneration, ["initialized"] = IsInitialized, ["busy"] = IsBusy, ["pendingCount"] = _pending.Count }; if (extra is not null) foreach (var pair in extra) result[pair.Key] = pair.Value; return result; }
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PsesLanguageServiceClient)); }
    private sealed class PendingRequest { public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
}
