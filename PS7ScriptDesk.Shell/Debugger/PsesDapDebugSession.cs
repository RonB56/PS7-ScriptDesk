using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Debug;

/// <summary>
/// DAP client for the debug endpoint published by the authoritative PSES host.
/// This class owns only the protocol connection; PSES remains the PowerShell host.
/// </summary>
internal sealed class PsesDapDebugSession : IDebugSession
{
    private readonly IAuthoritativePsesDebugEndpoint _endpoint;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private NamedPipeClientStream? _pipe;
    private Task? _readerTask;
    private int _nextSequence;
    private int _threadId = 1;
    private int _sessionGeneration;
    private long _pauseGeneration;
    private bool _disposed;
    private bool _stopped;
    private IReadOnlyList<DebugCallStackFrame> _frames = Array.Empty<DebugCallStackFrame>();

    public PsesDapDebugSession(IAuthoritativePsesDebugEndpoint endpoint)
        => _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public DebugSessionState CurrentState { get; private set; } = DebugSessionState.Stopped;
    public Guid SessionId => _sessionId;
    public long PauseGeneration => Interlocked.Read(ref _pauseGeneration);
    public DebugTerminationInfo? TerminationInfo { get; private set; }
    public DebuggerPauseReason CurrentPauseReason { get; private set; } = DebuggerPauseReason.Unknown;

    public event Action<DebugSessionState>? StateChanged;
    public event Action<string?, int>? BreakpointHit;
    public event Action? SessionEnded;
    public event Action<DebugTerminationInfo>? Terminated;
    public event Action<string>? OutputReceived;
    public event Action<DebuggerEvent>? TypedEventReceived;

    public async Task StartAsync(PowerShellRuntimeInfo runtime, string launchScriptPath, IReadOnlyList<DebugBreakpointInfo> breakpoints)
    {
        ThrowIfDisposed();
        if (!_endpoint.IsSessionRunning || string.IsNullOrWhiteSpace(_endpoint.DebugServicePipeName))
            throw new InvalidOperationException("The authoritative PSES session has no active debug-service endpoint.");

        var pipeName = _endpoint.DebugServicePipeName.Replace("\\\\.\\pipe\\", string.Empty, StringComparison.OrdinalIgnoreCase);
        _sessionGeneration = _endpoint.SessionGeneration;
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await _pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            _readerTask = Task.Run(ReadLoopAsync);
            SetState(DebugSessionState.Starting);

        var initialize = await SendRequestAsync("initialize", new
        {
            adapterID = "PS7ScriptDesk",
            clientID = "PS7ScriptDesk",
            clientName = "PS7 ScriptDesk",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            pathFormat = "path",
            supportsVariablePaging = false,
            supportsRunInTerminalRequest = false
        }, connectTimeout.Token).ConfigureAwait(false);
        EnsureSuccess(initialize, "initialize");

        var launch = await SendRequestAsync("launch", new
        {
            type = "PowerShell",
            request = "launch",
            name = "PS7 ScriptDesk authoritative session",
            program = launchScriptPath,
            script = launchScriptPath,
            cwd = Path.GetDirectoryName(launchScriptPath),
            workingDirectory = Path.GetDirectoryName(launchScriptPath),
            args = Array.Empty<string>(),
            stopOnEntry = false,
            terminateDebuggee = false,
            __sessionId = _sessionId.ToString("N")
        }, connectTimeout.Token).ConfigureAwait(false);
        EnsureSuccess(launch, "launch");
        await _initialized.Task.WaitAsync(connectTimeout.Token).ConfigureAwait(false);

        var grouped = breakpoints.Where(item => string.Equals(Path.GetFullPath(item.ScriptPath), Path.GetFullPath(launchScriptPath), StringComparison.OrdinalIgnoreCase)).ToArray();
        var setBreakpoints = await SendRequestAsync("setBreakpoints", new
        {
            source = new { name = Path.GetFileName(launchScriptPath), path = launchScriptPath },
            breakpoints = grouped.Select(item => new { line = item.LineNumber }).ToArray(),
            lines = grouped.Select(item => item.LineNumber).ToArray(),
            sourceModified = false
        }, connectTimeout.Token).ConfigureAwait(false);
        EnsureSuccess(setBreakpoints, "setBreakpoints");
        var configured = await SendRequestAsync("configurationDone", new { }, connectTimeout.Token).ConfigureAwait(false);
        EnsureSuccess(configured, "configurationDone");
        SetState(DebugSessionState.Running);
            Log("Authoritative PSES DAP debugger started.", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["generation"] = _endpoint.SessionGeneration,
                ["processId"] = _endpoint.ProcessId,
                ["processStartTimeUtc"] = _endpoint.ProcessStartTimeUtc,
                ["debugPipe"] = pipeName,
                ["targetPath"] = launchScriptPath
            });
        }
        catch (Exception ex)
        {
            Log("Authoritative PSES DAP startup failed; closing the transport and settling pending requests.", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["generation"] = _sessionGeneration,
                ["processId"] = _endpoint.ProcessId,
                ["exceptionType"] = ex.GetType().Name
            });
            FailPending(ex);
            await CloseTransportAsync().ConfigureAwait(false);
            Finish(DebugTerminationReason.StartupFailure, "Authoritative PSES DAP startup failed.", details: ex.GetType().Name);
            throw;
        }
    }

    public Task ContinueAsync() => SendControlAsync("continue", new { threadId = _threadId });
    public Task StepIntoAsync() => SendControlAsync("stepIn", new { threadId = _threadId });
    public Task StepOverAsync() => SendControlAsync("next", new { threadId = _threadId });
    public Task StepOutAsync() => SendControlAsync("stepOut", new { threadId = _threadId });

    public async Task<IReadOnlyList<DebugCallStackFrame>> GetCallStackAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("stackTrace", new { threadId = _threadId, levels = 50 }, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "stackTrace");
        if (!TryGetArray(response.Body, "stackFrames", out var frames)) return Array.Empty<DebugCallStackFrame>();
        var pause = PauseGeneration;
        _frames = frames.EnumerateArray().Select((frame, index) =>
        {
            var source = GetObject(frame, "source");
            var path = GetString(source, "path") ?? GetString(source, "name") ?? string.Empty;
            var providerId = GetInt(frame, "id")?.ToString() ?? index.ToString();
            return new DebugCallStackFrame(GetString(frame, "name") ?? "<unknown>", path, GetInt(frame, "line") ?? 0)
            {
                SessionId = _sessionId,
                PauseGeneration = pause,
                ThreadId = _threadId,
                ProviderFrameId = providerId,
                FrameIndex = index,
                IsCurrentFrame = index == 0,
                IsSelectedInspectionFrame = index == 0,
                IsNavigable = !string.IsNullOrWhiteSpace(path) && (GetInt(frame, "line") ?? 0) > 0
            };
        }).ToArray();
        Log("PSES DAP call stack mapped from provider response.", new Dictionary<string, object?>
        {
            ["threadId"] = _threadId,
            ["requestedLevels"] = 50,
            ["providerFrameCount"] = frames.GetArrayLength(),
            ["mappedFrameCount"] = _frames.Count,
            ["pauseGeneration"] = pause,
            ["frameNamePreview"] = DeveloperDiagnostics.SanitizePreview(string.Join(" | ", _frames.Take(8).Select(frame => frame.FunctionName)))
        });
        return _frames;
    }

    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync()
        => GetVariablesForFrameAsync(_frames.FirstOrDefault()?.ProviderFrameId, _threadId, CancellationToken.None);

    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(DebuggerFrameInspectionIdentity frameIdentity, CancellationToken cancellationToken = default)
        => GetVariablesForFrameAsync(frameIdentity.ProviderFrameId, frameIdentity.ThreadId == 0 ? _threadId : frameIdentity.ThreadId, cancellationToken);

    public async Task<DebuggerVariableInspectionResult> GetFrameVariablesAsync(DebuggerFrameInspectionIdentity frameIdentity, CancellationToken cancellationToken = default)
    {
        try
        {
            var variables = await GetVariablesAsync(frameIdentity, cancellationToken).ConfigureAwait(false);
            return new(DebuggerVariableAvailability.Available, DebuggerVariableScopeKind.CurrentFrameScopes, variables, frameIdentity);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            return DebuggerVariableInspectionResult.UnavailableFor(frameIdentity, ex.Message);
        }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped) return true;
        _stopped = true;
        try
        {
            if (_pipe is { IsConnected: true })
            {
                var response = await SendRequestAsync("disconnect", new { terminateDebuggee = false, restart = false }, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response, "disconnect");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or InvalidOperationException)
        {
            Log("Authoritative PSES DAP disconnect failed; transport will still be closed.", new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name });
        }
        finally
        {
            await CloseTransportAsync().ConfigureAwait(false);
            Finish(DebugTerminationReason.UserStop, "Authoritative PSES DAP session disconnected.", wasUserRequested: true);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _pipe?.Dispose();
        FailPending(new ObjectDisposedException(nameof(PsesDapDebugSession)));
        // A writer or reader may still be unwinding. Keep these synchronization
        // objects alive until those continuations have completed; disposing
        // them here races their finally paths.
    }

    private async Task<IReadOnlyList<DebugVariableInfo>> GetVariablesForFrameAsync(string? providerFrameId, long threadId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(providerFrameId, out var frameId)) return Array.Empty<DebugVariableInfo>();
        var scopesResponse = await SendRequestAsync("scopes", new { frameId }, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(scopesResponse, "scopes");
        if (!TryGetArray(scopesResponse.Body, "scopes", out var scopes)) return Array.Empty<DebugVariableInfo>();
        var result = new List<DebugVariableInfo>();
        var seenVariables = new HashSet<string>(StringComparer.Ordinal);
        var rawVariableCount = 0;
        foreach (var scope in scopes.EnumerateArray())
        {
            var reference = GetInt(scope, "variablesReference") ?? 0;
            if (reference == 0) continue;
            var variablesResponse = await SendRequestAsync("variables", new { variablesReference = reference }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(variablesResponse, "variables");
            if (!TryGetArray(variablesResponse.Body, "variables", out var variables)) continue;
            foreach (var variable in variables.EnumerateArray())
            {
                rawVariableCount++;
                var name = GetString(variable, "name") ?? string.Empty;
                var type = GetString(variable, "type") ?? string.Empty;
                var value = GetString(variable, "value") ?? string.Empty;
                var scopeName = GetString(scope, "name") ?? "Current";
                var deduplicationKey = $"{scopeName}\u001f{name}\u001f{type}\u001f{value}";
                if (!seenVariables.Add(deduplicationKey))
                {
                    continue;
                }

                result.Add(new DebugVariableInfo(name, type, value)
                {
                    SessionId = _sessionId,
                    PauseGeneration = PauseGeneration,
                    ThreadId = threadId,
                    ProviderFrameId = providerFrameId ?? string.Empty,
                    Scope = scopeName,
                    FrameId = DebugFrameIdentity.BuildFrameId(_sessionId, PauseGeneration, threadId, providerFrameId ?? string.Empty),
                    IsExpandable = (GetInt(variable, "variablesReference") ?? 0) != 0,
                    HasChildren = (GetInt(variable, "variablesReference") ?? 0) != 0
                });
            }
        }
        Log("PSES DAP variables aggregated for the selected frame.", new Dictionary<string, object?>
        {
            ["providerFrameId"] = providerFrameId,
            ["scopeCount"] = scopes.GetArrayLength(),
            ["rawVariableCount"] = rawVariableCount,
            ["deduplicatedVariableCount"] = result.Count,
            ["duplicateCount"] = rawVariableCount - result.Count,
            ["pauseGeneration"] = PauseGeneration
        });
        return result;
    }

    private async Task SendControlAsync(string command, object arguments)
    {
        var pauseGenerationBeforeRequest = PauseGeneration;
        var response = await SendRequestAsync(command, arguments, CancellationToken.None).ConfigureAwait(false);
        EnsureSuccess(response, command);
        // PSES may publish the stopped event before or after the control response.
        // Only suppress the response-side Running transition when a newer pause
        // was already observed; otherwise the successful response must advance the
        // state machine so a later continued/terminated event can complete it.
        if (CurrentState != DebugSessionState.Stopped && PauseGeneration == pauseGenerationBeforeRequest)
            SetState(DebugSessionState.Running);
    }

    private async Task<DapResponse> SendRequestAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        if (!_endpoint.IsSessionRunning || _endpoint.SessionGeneration != _sessionGeneration)
            throw new InvalidOperationException("The authoritative PSES session generation is no longer current.");
        var pipe = _pipe ?? throw new InvalidOperationException("The PSES DAP pipe is not connected.");
        var sequence = Interlocked.Increment(ref _nextSequence);
        var completion = new TaskCompletionSource<DapResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion)) throw new InvalidOperationException("Duplicate DAP sequence.");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { seq = sequence, type = "request", command, arguments });
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false); await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false); await pipe.FlushAsync(cancellationToken).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!_endpoint.IsSessionRunning || _endpoint.SessionGeneration != _sessionGeneration)
                throw new InvalidOperationException("The authoritative PSES session generation changed while the DAP request was pending.");
            return response;
        }
        finally { _pending.TryRemove(sequence, out _); }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        var pendingBytes = new List<byte>();
        try
        {
            while (!_lifetime.IsCancellationRequested && _pipe is { IsConnected: true } pipe)
            {
                var read = await pipe.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The PSES DAP pipe closed.");
                pendingBytes.AddRange(buffer.AsSpan(0, read).ToArray());
                while (TryReadFrame(pendingBytes, out var message))
                {
                    if (message is null) continue;
                    if (string.Equals(GetString(message.Value, "type"), "response", StringComparison.OrdinalIgnoreCase))
                    {
                        var requestSequence = GetInt(message.Value, "request_seq");
                        if (requestSequence is int sequence && _pending.TryGetValue(sequence, out var completion)) completion.TrySetResult(ParseResponse(message.Value));
                    }
                    else if (string.Equals(GetString(message.Value, "type"), "event", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleEvent(message.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try { if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false); } catch { }
            FailPending(ex);
            _initialized.TrySetException(new InvalidOperationException("The authoritative PSES DAP session ended before initialization completed.", ex));
            if (!_stopped) Finish(DebugTerminationReason.TransportFailure, "The authoritative PSES DAP transport failed.", details: ex.GetType().Name);
        }
    }

    private void HandleEvent(JsonElement message)
    {
        if (!_endpoint.IsSessionRunning || _endpoint.SessionGeneration != _sessionGeneration)
        {
            Log("Ignored DAP event from a stale authoritative session generation.", new Dictionary<string, object?>
            {
                ["sessionGeneration"] = _sessionGeneration,
                ["currentGeneration"] = _endpoint.SessionGeneration,
                ["event"] = GetString(message, "event")
            });
            return;
        }
        var name = GetString(message, "event") ?? string.Empty;
        var body = GetObject(message, "body");
        switch (name)
        {
            case "initialized": _initialized.TrySetResult(true); break;
            case "stopped":
                _threadId = GetInt(body, "threadId") ?? _threadId;
                var pauseGeneration = Interlocked.Increment(ref _pauseGeneration);
                CurrentPauseReason = (GetString(body, "reason") ?? string.Empty).Equals("breakpoint", StringComparison.OrdinalIgnoreCase) ? DebuggerPauseReason.Breakpoint : DebuggerPauseReason.Step;
                SetState(DebugSessionState.Paused);
                _ = PublishPauseAsync(pauseGeneration, CurrentPauseReason);
                break;
            case "continued": SetState(DebugSessionState.Running); break;
            case "output":
                var text = GetString(body, "output");
                if (!string.IsNullOrEmpty(text)) { SafeInvoke(OutputReceived, text); PublishEvent(DebuggerEventCategory.ScriptOutput, text); }
                break;
            case "terminated":
            case "exited":
                Finish(DebugTerminationReason.NormalCompletion, "The authoritative PSES debugger reported completion.");
                break;
        }
    }

    private async Task PublishPauseAsync(long pauseGeneration, DebuggerPauseReason pauseReason)
    {
        try
        {
            var frames = await GetCallStackAsync(CancellationToken.None).ConfigureAwait(false);
            if (CurrentState != DebugSessionState.Paused || PauseGeneration != pauseGeneration)
            {
                Log("Ignored stale PSES DAP pause inspection result.", new Dictionary<string, object?>
                {
                    ["inspectionPauseGeneration"] = pauseGeneration,
                    ["currentPauseGeneration"] = PauseGeneration,
                    ["currentState"] = CurrentState.ToString(),
                    ["pauseReason"] = pauseReason.ToString()
                });
                return;
            }
            var frame = frames.FirstOrDefault();
            var scriptPath = frame?.ScriptName;
            var lineNumber = frame?.LineNumber ?? 0;
            SafeInvoke(BreakpointHit, scriptPath, lineNumber);
            PublishEvent(
                pauseReason == DebuggerPauseReason.Breakpoint ? DebuggerEventCategory.Breakpoint : DebuggerEventCategory.Step,
                pauseReason == DebuggerPauseReason.Breakpoint ? "PSES debugger paused at breakpoint." : "PSES debugger paused after a step.",
                scriptPath,
                lineNumber,
                pauseReason);
        }
        catch (Exception ex) { Log("PSES DAP stopped-event inspection failed.", new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name, ["pauseGeneration"] = pauseGeneration, ["pauseReason"] = pauseReason.ToString() }); }
    }

    private async Task CloseTransportAsync()
    {
        _lifetime.Cancel();
        try { if (_readerTask is not null) await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false); } catch { }
        FailPending(new EndOfStreamException("The PSES DAP transport was closed."));
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
            completion.TrySetException(exception);
        _pending.Clear();
    }

    private void Finish(DebugTerminationReason reason, string message, bool wasUserRequested = false, string? details = null)
    {
        if (TerminationInfo is not null) return;
        _initialized.TrySetException(new InvalidOperationException(message));
        SetState(DebugSessionState.Stopped);
        TerminationInfo = new DebugTerminationInfo(_sessionId, DateTimeOffset.UtcNow, reason, message, exitCode: null, processId: _endpoint.ProcessId, wasUserRequested: wasUserRequested, wasExpected: reason == DebugTerminationReason.NormalCompletion, details: details);
        SafeInvoke(Terminated, TerminationInfo);
        SafeInvoke(SessionEnded);
    }

    private void SetState(DebugSessionState state)
    {
        CurrentState = state;
        SafeInvoke(StateChanged, state);
    }

    private void PublishEvent(DebuggerEventCategory category, string text, string? path = null, int? line = null, DebuggerPauseReason? pause = null)
        => SafeInvoke(TypedEventReceived, new DebuggerEvent(_sessionId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Ticks, category, DebuggerEventSeverity.Information, "PSES-DAP", text, path, line, pauseReason: pause, processId: _endpoint.ProcessId));

    private static void EnsureSuccess(DapResponse response, string command)
    { if (!response.Success) throw new InvalidOperationException($"PSES DAP '{command}' failed: {response.Message ?? "unspecified error"}"); }

    private static DapResponse ParseResponse(JsonElement message)
        => new(GetString(message, "command") ?? string.Empty, message.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True, GetString(message, "message"), GetObject(message, "body"));

    private static bool TryReadFrame(List<byte> bytes, out JsonElement? message)
    {
        message = null;
        var delimiter = FindDelimiter(bytes);
        if (delimiter < 0) return false;
        var header = Encoding.ASCII.GetString(bytes.Take(delimiter).ToArray());
        var line = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).SingleOrDefault(item => item.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (line is null || !int.TryParse(line["Content-Length:".Length..].Trim(), out var length) || length < 0 || length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid DAP Content-Length.");
        var bodyStart = delimiter + 4;
        if (bytes.Count - bodyStart < length) return false;
        var body = bytes.Skip(bodyStart).Take(length).ToArray();
        bytes.RemoveRange(0, bodyStart + length);
        using var document = JsonDocument.Parse(body);
        message = document.RootElement.Clone();
        return true;
    }

    private static int FindDelimiter(List<byte> bytes)
    { for (var i = 0; i <= bytes.Count - 4; i++) if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i; return -1; }
    private static JsonElement GetObject(JsonElement value, string name) => value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Object ? result : default;
    private static bool TryGetArray(JsonElement value, string name, out JsonElement result) { result = value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Array ? found : default; return result.ValueKind == JsonValueKind.Array; }
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static int? GetInt(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.TryGetInt32(out var number) ? number : null;
    private static void SafeInvoke<T>(Action<T>? handler, T value) { try { handler?.Invoke(value); } catch { } }
    private static void SafeInvoke<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second) { try { handler?.Invoke(first, second); } catch { } }
    private static void SafeInvoke(Action? handler) { try { handler?.Invoke(); } catch { } }
    private static void Log(string message, Dictionary<string, object?> metadata) => DeveloperDiagnostics.LogInfo("Debugger", message, metadata);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PsesDapDebugSession)); }
    private sealed record DapResponse(string Command, bool Success, string? Message, JsonElement Body);
}
