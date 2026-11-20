using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PS7ScriptDesk.PowerShell.Services;

internal sealed class PsesEditorExecutionBridgeClient : IAsyncDisposable
{
    private const int MaximumMessageLength = 4 * 1024 * 1024;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _activeRequestSync = new();
    private readonly Guid _hostInstanceId;
    private readonly Guid _runspaceInstanceId;
    private readonly string _bridgeGeneration;
    private readonly int _processId;
    private ActiveRequestIdentity? _activeRequest;
    private int _disposed;
    private int _faulted;

    private PsesEditorExecutionBridgeClient(NamedPipeClientStream pipe, string bridgeGeneration,
        int processId, Guid hostInstanceId, Guid runspaceInstanceId)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
        _bridgeGeneration = bridgeGeneration;
        _processId = processId;
        _hostInstanceId = hostInstanceId;
        _runspaceInstanceId = runspaceInstanceId;
    }

    public static async Task<PsesEditorExecutionBridgeClient> ConnectAsync(string pipeName, string secret,
        int expectedProcessId, string expectedGeneration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15000, cancellationToken).ConfigureAwait(false);
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { Operation = "authenticate", Token = secret })).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ready = JsonDocument.Parse(await ReadBoundedLineAsync(reader, timeout.Token).ConfigureAwait(false));
            var root = ready.RootElement;
            var actualProcessId = root.GetProperty("ProcessId").GetInt32();
            var actualGeneration = root.GetProperty("Generation").GetString();
            var host = root.GetProperty("HostInstanceId").GetGuid();
            var runspace = root.GetProperty("RunspaceInstanceId").GetGuid();
            if (root.GetProperty("Operation").GetString() != "ready" ||
                root.GetProperty("Status").GetString() != "Authenticated" || actualProcessId != expectedProcessId ||
                root.GetProperty("ProtocolVersion").GetInt32() != 1 ||
                !string.Equals(actualGeneration, expectedGeneration, StringComparison.Ordinal) || host == Guid.Empty || runspace == Guid.Empty)
                throw new InvalidOperationException("The PSES bridge readiness identity did not match the launched session.");

            var client = new PsesEditorExecutionBridgeClient(pipe, actualGeneration!, actualProcessId, host, runspace);
            pipe = null!;
            reader.Dispose();
            writer.Dispose();
            return client;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    public async Task<BridgeExecutionResult> ExecuteAsync(Guid requestId, long sessionGeneration,
        string script, bool scriptScope, CancellationToken cancellationToken)
        => await ExecuteCoreAsync(requestId, sessionGeneration, script, scriptScope ? "Script" : "Current", cancellationToken)
            .ConfigureAwait(false);

    public Task<BridgeExecutionResult> ExecuteFileAsync(Guid requestId, long sessionGeneration,
        string scriptPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        return ExecuteCoreAsync(requestId, sessionGeneration, scriptPath, "File", cancellationToken);
    }

    private async Task<BridgeExecutionResult> ExecuteCoreAsync(Guid requestId, long sessionGeneration,
        string script, string scope, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request identity is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(script);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActiveRequestIdentity? activeRequest = null;
        try
        {
            var publishedRequest = new ActiveRequestIdentity(requestId.ToString("N"), sessionGeneration);
            activeRequest = publishedRequest;
            lock (_activeRequestSync)
            {
                ThrowIfDisposed();
                if (_activeRequest is not null)
                    throw new InvalidOperationException("A PSES bridge request identity is already active.");
                Volatile.Write(ref _activeRequest, publishedRequest);
            }
            var request = new BridgeMessage
            {
                Operation = "execute", RequestId = requestId.ToString("N"), ProcessId = _processId,
                Generation = _bridgeGeneration, HostInstanceId = _hostInstanceId, RunspaceInstanceId = _runspaceInstanceId,
                Scope = scope, Script = script, SessionGeneration = sessionGeneration,
                ProtocolVersion = 1
            };
            await WriteMessageAsync(request, cancellationToken).ConfigureAwait(false);
            using var cancelRegistration = cancellationToken.Register(static state =>
            {
                var tuple = ((PsesEditorExecutionBridgeClient Client, ActiveRequestIdentity Identity))state!;
                _ = tuple.Client.SendCancelBestEffortAsync(tuple.Identity, false);
            }, (this, publishedRequest));

            var cancellationAcknowledged = false;
            using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            while (true)
            {
                var line = await ReadBoundedLineAsync(_reader, waitToken.Token).ConfigureAwait(false);
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                var operation = root.TryGetProperty("Operation", out var responseOperation) ? responseOperation.GetString() : null;
                if (!root.TryGetProperty("RequestId", out var responseId) || responseId.GetString() != requestId.ToString("N"))
                    throw new InvalidDataException("The PSES bridge returned a completion for a different request.");
                ValidateIdentity(root, sessionGeneration);
                if (operation == "cancelAcknowledged")
                {
                    cancellationAcknowledged = root.GetProperty("Status").GetString() == "CancellationRequested";
                    continue;
                }
                if (operation != "complete") throw new InvalidDataException("The PSES bridge returned an unexpected response.");
                var status = root.GetProperty("Status").GetString();
                return new(status switch
                {
                    "Completed" => BridgeExecutionStatus.Completed,
                    "Cancelled" => BridgeExecutionStatus.Cancelled,
                    "Rejected" => BridgeExecutionStatus.Rejected,
                    _ => BridgeExecutionStatus.Failed
                }, cancellationAcknowledged, root.TryGetProperty("Error", out var error) ? error.GetString() : null);
            }
        }
        catch
        {
            FaultConnection();
            throw;
        }
        finally
        {
            ActiveRequestIdentity? completedIdentity;
            lock (_activeRequestSync)
            {
                completedIdentity = ReferenceEquals(_activeRequest, activeRequest) ? _activeRequest : null;
                if (completedIdentity is not null) Volatile.Write(ref _activeRequest, null);
            }
            completedIdentity?.Settled.TrySetResult(true);
            _requestGate.Release();
        }
    }

    private void ValidateIdentity(JsonElement root, long sessionGeneration)
    {
        if (root.GetProperty("ProtocolVersion").GetInt32() != 1 ||
            root.GetProperty("ProcessId").GetInt32() != _processId ||
            root.GetProperty("Generation").GetString() != _bridgeGeneration ||
            root.GetProperty("HostInstanceId").GetGuid() != _hostInstanceId ||
            root.GetProperty("RunspaceInstanceId").GetGuid() != _runspaceInstanceId ||
            root.GetProperty("SessionGeneration").GetInt64() != sessionGeneration)
            throw new InvalidDataException("The PSES bridge response belonged to a stale or incompatible session.");
    }

    private async Task SendCancelBestEffortAsync(ActiveRequestIdentity identity, bool duringDispose)
    {
        try
        {
            if (!duringDispose && !ReferenceEquals(Volatile.Read(ref _activeRequest), identity)) return;
            if (Volatile.Read(ref _faulted) != 0) return;
            await WriteMessageAsync(new BridgeMessage
            {
                Operation = "cancel", RequestId = identity.RequestId, ProcessId = _processId, Generation = _bridgeGeneration,
                HostInstanceId = _hostInstanceId, RunspaceInstanceId = _runspaceInstanceId,
                SessionGeneration = identity.SessionGeneration, ProtocolVersion = 1
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    public async Task<bool> RequestActiveCancellationAsync()
    {
        var identity = Volatile.Read(ref _activeRequest);
        if (identity is not null && ReferenceEquals(identity, Volatile.Read(ref _activeRequest)))
        {
            await SendCancelBestEffortAsync(identity, false).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task WriteMessageAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        if (json.Length > MaximumMessageLength) throw new InvalidDataException("The editor bridge request exceeded the configured size limit.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false); await _writer.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder(1024);
        var buffer = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The PSES bridge closed before completing a response.");
            if (buffer[0] == '\n') return line.ToString();
            if (buffer[0] != '\r')
            {
                if (line.Length >= MaximumMessageLength) throw new InvalidDataException("The PSES bridge response exceeded the configured size limit.");
                line.Append(buffer[0]);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        ActiveRequestIdentity? activeRequest;
        lock (_activeRequestSync)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            activeRequest = _activeRequest;
        }
        if (activeRequest is not null)
            await SendCancelBestEffortAsync(activeRequest, true).ConfigureAwait(false);
        _lifetime.Cancel();
        try { _pipe.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        bool requestSettled = activeRequest is null ||
            await Task.WhenAny(activeRequest.Settled.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == activeRequest.Settled.Task;
        if (requestSettled)
        {
            _reader.Dispose();
            _writer.Dispose();
            _lifetime.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(PsesEditorExecutionBridgeClient));
        if (_faulted != 0) throw new InvalidOperationException("The PSES bridge protocol stream is faulted and cannot be reused.");
    }

    private void FaultConnection()
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            _lifetime.Cancel();
            try { _pipe.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    private sealed class ActiveRequestIdentity(string requestId, long sessionGeneration)
    {
        public string RequestId { get; } = requestId;
        public long SessionGeneration { get; } = sessionGeneration;
        public TaskCompletionSource<bool> Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BridgeMessage
    {
        public string? Operation { get; init; }
        public string? RequestId { get; init; }
        public int ProcessId { get; init; }
        public string? Generation { get; init; }
        public Guid HostInstanceId { get; init; }
        public Guid RunspaceInstanceId { get; init; }
        public string? Scope { get; init; }
        public string? Script { get; init; }
        public long SessionGeneration { get; init; }
        public int ProtocolVersion { get; init; }
    }
}

internal enum BridgeExecutionStatus { Completed, Cancelled, Rejected, Failed }
internal sealed record BridgeExecutionResult(BridgeExecutionStatus Status, bool CancellationAcknowledged, string? Error);
