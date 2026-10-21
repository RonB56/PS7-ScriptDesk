using System.Collections.Concurrent;
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
    private readonly object _stateGate = new();
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
