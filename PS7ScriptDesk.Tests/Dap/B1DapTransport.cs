using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PS7ScriptDesk.Tests.Dap;

internal sealed record DapMessage(
    int? Seq,
    string Type,
    string? Command,
    int? RequestSeq,
    bool? Success,
    string? Message,
    string? Event,
    JsonElement? Arguments,
    JsonElement? Body)
{
    public static DapMessage Parse(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        JsonElement? Clone(string name) => root.TryGetProperty(name, out var value) ? value.Clone() : null;
        return new DapMessage(
            root.TryGetProperty("seq", out var seq) && seq.TryGetInt32(out var seqValue) ? seqValue : null,
            root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("command", out var command) ? command.GetString() : null,
            root.TryGetProperty("request_seq", out var requestSeq) && requestSeq.TryGetInt32(out var requestSeqValue) ? requestSeqValue : null,
            root.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False ? success.GetBoolean() : null,
            root.TryGetProperty("message", out var message) ? message.GetString() : null,
            root.TryGetProperty("event", out var @event) ? @event.GetString() : null,
            Clone("arguments"),
            Clone("body"));
    }
}

internal sealed class DapMessageFramer
{
    public const int DefaultMaximumPayloadBytes = 8 * 1024 * 1024;
    private readonly List<byte> _buffer = new();
    private readonly int _maximumPayloadBytes;

    public DapMessageFramer(int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        if (maximumPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        _maximumPayloadBytes = maximumPayloadBytes;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        _buffer.AddRange(bytes.ToArray());
        if (_buffer.Count > _maximumPayloadBytes + 64 * 1024)
        {
            throw new InvalidDataException("DAP input exceeded the bounded framing buffer.");
        }
    }

    public bool TryRead(out DapMessage? message)
    {
        message = null;
        var delimiter = FindDelimiter();
        if (delimiter < 0) return false;

        var header = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_buffer)[..delimiter]);
        var contentLength = ParseContentLength(header);
        if (contentLength > _maximumPayloadBytes) throw new InvalidDataException("DAP payload exceeds the configured maximum.");
        var bodyStart = delimiter + 4;
        if (_buffer.Count - bodyStart < contentLength) return false;

        var body = CollectionsMarshal.AsSpan(_buffer).Slice(bodyStart, contentLength).ToArray();
        _buffer.RemoveRange(0, bodyStart + contentLength);
        try
        {
            message = DapMessage.Parse(body);
            if (string.IsNullOrWhiteSpace(message.Type)) throw new InvalidDataException("DAP message type is missing.");
            return true;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("DAP JSON payload is malformed.", ex);
        }
    }

    public static byte[] Encode(object payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Encode(body);
    }

    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    private int FindDelimiter()
    {
        for (var index = 0; index <= _buffer.Count - 4; index++)
        {
            if (_buffer[index] == '\r' && _buffer[index + 1] == '\n' && _buffer[index + 2] == '\r' && _buffer[index + 3] == '\n') return index;
        }

        return -1;
    }

    private static int ParseContentLength(string header)
    {
        var line = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (line is null) throw new InvalidDataException("DAP Content-Length header is missing.");
        var value = line["Content-Length:".Length..].Trim();
        if (!int.TryParse(value, out var length) || length < 0) throw new InvalidDataException("DAP Content-Length header is invalid.");
        return length;
    }
}

internal sealed class DapRequestTracker
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapMessage>> _pending = new();

    public Task<DapMessage> Add(int sequence, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<DapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(sequence, completion)) throw new InvalidOperationException($"DAP sequence {sequence} is already pending.");
        cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(sequence, out var removed)) removed.TrySetCanceled(cancellationToken);
        });
        return completion.Task;
    }

    public bool Complete(DapMessage message)
    {
        if (message.RequestSeq is not int requestSequence || !_pending.TryRemove(requestSequence, out var completion)) return false;
        return completion.TrySetResult(message);
    }

    public void FailAll(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(exception);
        }
    }

    public int Count => _pending.Count;
}

internal sealed class DapConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly DapRequestTracker _requests = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DapMessageFramer _framer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readerTask;
    private int _nextSequence;
    private int _closed;

    public DapConnection(Stream stream, Action<DapMessage>? eventReceived = null, Action<Exception>? connectionFailed = null, Action<DapMessage>? messageSent = null, Action<DapMessage>? messageReceived = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        EventReceived = eventReceived;
        ConnectionFailed = connectionFailed;
        MessageSent = messageSent;
        MessageReceived = messageReceived;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    public Action<DapMessage>? EventReceived { get; }
    public Action<Exception>? ConnectionFailed { get; }
    public Action<DapMessage>? MessageSent { get; }
    public Action<DapMessage>? MessageReceived { get; }
    public int PendingRequestCount => _requests.Count;

    public async Task<DapMessage> SendRequestAsync(string command, object? arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A DAP command is required.", nameof(command));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var sequence = Interlocked.Increment(ref _nextSequence);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, _lifetime.Token);
        var responseTask = _requests.Add(sequence, linked.Token);
        try
        {
            var request = new Dictionary<string, object?> { ["seq"] = sequence, ["type"] = "request", ["command"] = command };
            if (arguments is not null) request["arguments"] = arguments;
            var encoded = DapMessageFramer.Encode(request);
            MessageSent?.Invoke(DapMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(request)));
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await _stream.WriteAsync(encoded, linked.Token).ConfigureAwait(false); await _stream.FlushAsync(linked.Token).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            var response = await responseTask.ConfigureAwait(false);
            if (response.Success == false) throw new InvalidOperationException($"DAP '{command}' failed: {response.Message ?? "unspecified error"}");
            return response;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"DAP '{command}' timed out after {timeout.TotalMilliseconds:N0} ms.");
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("DAP pipe closed.");
                _framer.Append(buffer.AsSpan(0, read));
                while (_framer.TryRead(out var message) && message is not null)
                {
                    MessageReceived?.Invoke(message);
                    if (string.Equals(message.Type, "response", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_requests.Complete(message)) ConnectionFailed?.Invoke(new InvalidDataException("DAP response had no pending request."));
                    }
                    else if (string.Equals(message.Type, "event", StringComparison.OrdinalIgnoreCase))
                    {
                        EventReceived?.Invoke(message);
                    }
                    else
                    {
                        ConnectionFailed?.Invoke(new InvalidDataException($"Unexpected DAP message type '{message.Type}'."));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _requests.FailAll(ex);
            ConnectionFailed?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _lifetime.Cancel();
        _requests.FailAll(new IOException("DAP connection closed."));
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed record PsesSessionDetails(
    string? Status,
    string? LanguageServiceTransport,
    string? LanguageServicePipeName,
    string? DebugServiceTransport,
    string DebugServicePipeName,
    string PowerShellVersion);

internal sealed record PsesBundleInfo(
    string Root,
    string StartupScript,
    string Manifest,
    string HostingAssembly,
    string SourceKind,
    string? ExtensionVersion,
    string ModuleVersion);

internal static class PsesBundleResolver
{
    public static PsesBundleInfo Resolve(string? configuredRoot = null)
    {
        var candidates = new List<(string Path, string Kind)>();
        if (!string.IsNullOrWhiteSpace(configuredRoot)) candidates.Add((configuredRoot, "TestFixtureOrConfigured"));
        var environmentRoot = Environment.GetEnvironmentVariable("PS7SD_B1_PSES_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot)) candidates.Add((environmentRoot, "TestFixtureEnvironment"));
        var extensionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (Directory.Exists(extensionsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(extensionsRoot, "ms-vscode.powershell-*").OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) candidates.Add((Path.Combine(directory, "modules", "PowerShellEditorServices"), "EvidenceInstalledVSCode"));
        }

        foreach (var candidate in candidates.OrderByDescending(value => value.Kind.Contains("TestFixture", StringComparison.Ordinal)))
        {
            var root = Path.GetFullPath(candidate.Path);
            var startup = Path.Combine(root, "Start-EditorServices.ps1");
            var manifest = Path.Combine(root, "PowerShellEditorServices.psd1");
            var hosting = Path.Combine(root, "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll");
            if (!File.Exists(startup) || !File.Exists(manifest) || !File.Exists(hosting)) continue;
            var manifestText = File.ReadAllText(manifest);
            var moduleVersion = System.Text.RegularExpressions.Regex.Match(manifestText, "ModuleVersion\\s*=\\s*'(?<v>[^']+)'").Groups["v"].Value;
            var extensionVersion = Directory.GetParent(Directory.GetParent(root)?.FullName ?? root)?.Name.Replace("ms-vscode.powershell-", string.Empty, StringComparison.OrdinalIgnoreCase);
            return new PsesBundleInfo(root, startup, manifest, hosting, candidate.Kind, extensionVersion, moduleVersion);
        }

        throw new FileNotFoundException("No valid PSES bundle was found. Set PS7SD_B1_PSES_ROOT to an application-owned test bundle.");
    }
}

internal sealed class PsesHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _rootDirectory;
    private readonly Task<string> _stdoutTask;
    private readonly Task<string> _stderrTask;

    private PsesHostProcess(Process process, string rootDirectory, PsesBundleInfo bundle, PsesSessionDetails details, Task<string> stdoutTask, Task<string> stderrTask)
    {
        _process = process;
        _rootDirectory = rootDirectory;
        _stdoutTask = stdoutTask;
        _stderrTask = stderrTask;
        Bundle = bundle;
        Details = details;
    }

    public PsesBundleInfo Bundle { get; }
    public PsesSessionDetails Details { get; }
    public int ProcessId => _process.Id;

    public static async Task<PsesHostProcess> StartAsync(string? configuredBundleRoot, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var bundle = PsesBundleResolver.Resolve(configuredBundleRoot);
        var root = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-B1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var detailsPath = Path.Combine(root, "session.json");
        var logPath = Path.Combine(root, "logs");
        Directory.CreateDirectory(logPath);
        var powershell = ResolvePowerShell();
        var psi = new ProcessStartInfo(powershell);
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-File", bundle.StartupScript, "-HostName", "PS7ScriptDesk-B1", "-HostProfileId", "PS7ScriptDesk-B1", "-HostVersion", "1.0.0", "-BundledModulesPath", Path.GetDirectoryName(bundle.Root)!, "-EnableConsoleRepl", "-DebugServiceOnly", "-SessionDetailsPath", detailsPath, "-LogPath", logPath, "-LogLevel", "Diagnostic" }) psi.ArgumentList.Add(argument);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("PSES process could not be started.");
        var stdoutTask = DrainProcessStreamAsync(process.StandardOutput);
        var stderrTask = DrainProcessStreamAsync(process.StandardError);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            while (!File.Exists(detailsPath))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var stdout = await stdoutTask.ConfigureAwait(false);
                    var stderr = await stderrTask.ConfigureAwait(false);
                    throw new InvalidOperationException($"PSES exited before session details were written. ExitCode={process.ExitCode}; stdout={SummarizeProcessText(stdout)}; stderr={SummarizeProcessText(stderr)}.");
                }
                await Task.Delay(100, linked.Token).ConfigureAwait(false);
            }

            var details = await ReadDetailsAsync(detailsPath, linked.Token).ConfigureAwait(false);
            ValidateDetails(details);
            return new PsesHostProcess(process, root, bundle, details, stdoutTask, stderrTask);
        }
        catch
        {
            TryKill(process);
            TryDelete(root);
            throw;
        }
    }

    public async Task<DapConnection> ConnectDebugPipeAsync(TimeSpan timeout, Action<DapMessage>? eventReceived = null, Action<Exception>? connectionFailed = null, Action<DapMessage>? messageSent = null, Action<DapMessage>? messageReceived = null, CancellationToken cancellationToken = default)
    {
        var pipeName = Details.DebugServicePipeName.Replace("\\\\.\\pipe\\", string.Empty, StringComparison.OrdinalIgnoreCase);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        return new DapConnection(pipe, eventReceived, connectionFailed, messageSent, messageReceived);
    }

    private static async Task<PsesSessionDetails> ReadDetailsAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var details = new PsesSessionDetails(
                    GetString(root, "status"), GetString(root, "languageServiceTransport"), GetString(root, "languageServicePipeName"), GetString(root, "debugServiceTransport"), GetString(root, "debugServicePipeName") ?? string.Empty, GetString(root, "powerShellVersion") ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(details.DebugServicePipeName)) return details;
            }
            catch (JsonException) { }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidDataException("PSES session details did not contain a valid debug-service endpoint.");
    }

    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static void ValidateDetails(PsesSessionDetails details)
    {
        if (!string.Equals(details.Status, "started", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PSES session details status was not started.");
        if (!string.Equals(details.DebugServiceTransport, "NamedPipe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PSES debug service did not select NamedPipe transport.");
        if (!details.DebugServicePipeName.StartsWith("\\\\.\\pipe\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PSES debug-service pipe name is invalid.");
        if (string.IsNullOrWhiteSpace(details.PowerShellVersion)) throw new InvalidDataException("PSES PowerShell version is missing.");
    }

    private static string ResolvePowerShell()
    {
        var configured = Environment.GetEnvironmentVariable("PS7SD_B1_PWSH_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(standard)) return standard;
        return "pwsh";
    }

    public async ValueTask DisposeAsync()
    {
        TryKill(_process);
        try { await Task.WhenAll(_stdoutTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        _process.Dispose();
        TryDelete(_rootDirectory);
    }

    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
    private static async Task<string> DrainProcessStreamAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            if (builder.Length < 32 * 1024) builder.Append(buffer, 0, Math.Min(read, 32 * 1024 - builder.Length));
        }

        return builder.ToString();
    }

    private static string SummarizeProcessText(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrEmpty(normalized) ? "(empty)" : normalized[..Math.Min(500, normalized.Length)];
    }
}
