using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Domain.Models;
using PowerShellStudio.Shell.Editor;

namespace PowerShellStudio.Shell.Debug
{
    internal sealed class PsesDebugSession : IDebugSession
    {
        private const string ReadyMarker = "__PSS_DEBUG_READY__";
        private const string SessionEndedMarker = "__PSS_DEBUG_SESSION_ENDED__";
        private const string DebugPromptMarker = "__PSS_DEBUG_PROMPT__";
        private const string BreakpointHitStartMarker = "__PSS_BREAKPOINT_HIT_BEGIN__";
        private const string BreakpointHitEndMarker = "__PSS_BREAKPOINT_HIT_END__";
        private const string CurrentFrameStartMarker = "__PSS_CURRENT_FRAME_BEGIN__";
        private const string CurrentFrameEndMarker = "__PSS_CURRENT_FRAME_END__";
        private const string VariablesStartMarker = "__PSS_VARIABLES_BEGIN__";
        private const string VariablesEndMarker = "__PSS_VARIABLES_END__";
        private const string CallStackStartMarker = "__PSS_CALLSTACK_BEGIN__";
        private const string CallStackEndMarker = "__PSS_CALLSTACK_END__";
        private static readonly Regex DebugBreakpointOutputRegex = new(@"^Hit .+ breakpoint on ", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebugLocationOutputRegex = new(@"^At\s+.+(?:(?:\s+line\s+\d+\s+char:\d+)|(?::\d+\s+char:\d+))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebugLocationColonCaptureRegex = new(@"^At\s+(?<path>.+):(?<line>\d+)\s+char:\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebugLocationWordCaptureRegex = new(@"^At\s+(?<path>.+)\s+line\s+(?<line>\d+)\s+char:\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly object _syncRoot = new();
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly StringBuilder _stdoutLineBuffer = new();
        private readonly StringBuilder _stderrLineBuffer = new();
        private readonly StringBuilder _breakpointPayloadBuffer = new();

        private Process? _process;
        private StreamWriter? _stdin;
        private CancellationTokenSource? _lifetimeCancellationTokenSource;
        private Task? _stdoutReaderTask;
        private Task? _stderrReaderTask;
        private TaskCompletionSource<bool>? _readyCompletionSource;
        private ActiveRequest? _activeRequest;
        private bool _capturingBreakpointPayload;
        private bool _ignoreNextDebugPrompt;
        private int _suppressNextDebugPromptCount;
        private int _currentFrameQueryInProgress;
        private bool _sessionEndedRaised;
        private bool _disposed;
        private long _lastLocationNotificationTicks;

        public DebugSessionState CurrentState { get; private set; } = DebugSessionState.Stopped;

        public event Action<DebugSessionState>? StateChanged;
        public event Action<string?, int>? BreakpointHit;
        public event Action? SessionEnded;
        public event Action<string>? OutputReceived;

        public async Task StartAsync(PowerShellRuntimeInfo runtime, string launchScriptPath, IReadOnlyList<DebugBreakpointInfo> breakpoints)
        {
            Trace("StartAsync", $"Entry; runtime='{runtime?.DisplayName ?? "(null)"}'; launchPath='{Path.GetFileName(launchScriptPath)}'; breakpointCount={breakpoints?.Count ?? 0}; {DescribeSessionState()}");
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PsesDebugSession));
            }

            if (runtime is null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            if (string.IsNullOrWhiteSpace(runtime.ExecutablePath))
            {
                throw new ArgumentException("A PowerShell runtime executable path is required.", nameof(runtime));
            }

            if (string.IsNullOrWhiteSpace(launchScriptPath))
            {
                throw new ArgumentException("A launch script path is required.", nameof(launchScriptPath));
            }

            if (!File.Exists(launchScriptPath))
            {
                throw new FileNotFoundException("The debug launch script was not found.", launchScriptPath);
            }

            ThrowIfSessionAlreadyActive();

            var processStartInfo = new ProcessStartInfo
            {
                FileName = runtime.ExecutablePath,
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            PowerShellBackgroundProcessEnvironment.Apply(processStartInfo, "Debug", runtime.ExecutablePath);

            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("The debug PowerShell process could not be started.");
            }

            Trace("StartAsync", $"Process started; processId={TryGetProcessId(process)}; executable='{processStartInfo.FileName}'; {DescribeSessionState()}");

            var lifetimeCancellationTokenSource = new CancellationTokenSource();
            var readyCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_syncRoot)
            {
                _process = process;
                var standardInput = process.StandardInput;
                standardInput.NewLine = "\n";
                _stdin = standardInput;
                _lifetimeCancellationTokenSource = lifetimeCancellationTokenSource;
                _readyCompletionSource = readyCompletionSource;
                _sessionEndedRaised = false;
                _ignoreNextDebugPrompt = false;
                _suppressNextDebugPromptCount = 0;
                _currentFrameQueryInProgress = 0;
                _lastLocationNotificationTicks = 0;
                _capturingBreakpointPayload = false;
                _breakpointPayloadBuffer.Clear();
                SetCurrentState(DebugSessionState.Starting);
            }

            _stdoutReaderTask = Task.Run(
                () => ReadLoopAsync(process, process.StandardOutput, isErrorStream: false, lifetimeCancellationTokenSource.Token),
                lifetimeCancellationTokenSource.Token);
            _stderrReaderTask = Task.Run(
                () => ReadLoopAsync(process, process.StandardError, isErrorStream: true, lifetimeCancellationTokenSource.Token),
                lifetimeCancellationTokenSource.Token);

            try
            {
                Trace("StartAsync", $"Sending bootstrap script; processId={TryGetProcessId(process)}");
                await SendCommandAsync(BuildBootstrapScript(), CancellationToken.None).ConfigureAwait(false);

                using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await readyCompletionSource.Task.WaitAsync(readyTimeout.Token).ConfigureAwait(false);
                Trace("StartAsync", $"Ready marker observed; processId={TryGetProcessId(process)}; {DescribeSessionState()}");

                SetCurrentState(DebugSessionState.Running);
                Trace("StartAsync", $"Sending start script; processId={TryGetProcessId(process)}; breakpointCount={breakpoints.Count}");
                await SendCommandAsync(BuildStartScript(launchScriptPath, breakpoints), CancellationToken.None).ConfigureAwait(false);
                Trace("StartAsync", $"Completed; processId={TryGetProcessId(process)}; {DescribeSessionState()}");
            }
            catch
            {
                Trace("StartAsync", $"Failed; processId={TryGetProcessId(process)}; {DescribeSessionState()}");
                Dispose();
                throw;
            }
        }

        public Task ContinueAsync()
        {
            Trace("ContinueAsync", $"Requested; {DescribeSessionState()}");
            return SendDebugControlCommandAsync("c");
        }

        public Task StepIntoAsync()
        {
            Trace("StepIntoAsync", $"Requested; {DescribeSessionState()}");
            return SendDebugControlCommandAsync("s");
        }

        public Task StepOverAsync()
        {
            Trace("StepOverAsync", $"Requested; {DescribeSessionState()}");
            return SendDebugControlCommandAsync("v");
        }

        public Task StepOutAsync()
        {
            Trace("StepOutAsync", $"Requested; {DescribeSessionState()}");
            return SendDebugControlCommandAsync("o");
        }

        public async Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync()
        {
            EnsurePaused();

            var payload = await SendRequestAsync(
                BuildVariablesRequestScript(),
                VariablesStartMarker,
                VariablesEndMarker,
                suppressNextDebugPrompt: true,
                CancellationToken.None).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<DebugVariableInfo>();
            }

            return DeserializeList<DebugVariableInfo>(payload);
        }

        public async Task<IReadOnlyList<DebugCallStackFrame>> GetCallStackAsync()
        {
            EnsurePaused();

            var payload = await SendRequestAsync(
                BuildCallStackRequestScript(),
                CallStackStartMarker,
                CallStackEndMarker,
                suppressNextDebugPrompt: true,
                CancellationToken.None).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<DebugCallStackFrame>();
            }

            return DeserializeList<DebugCallStackFrame>(payload);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            Trace("Dispose", $"Entry; {DescribeSessionState()}");

            Process? processToDispose = null;
            CancellationTokenSource? cancellationTokenSource = null;
            StreamWriter? stdinToDispose = null;
            Task? stdoutReaderTask = null;
            Task? stderrReaderTask = null;
            bool raiseSessionEnded = false;

            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                raiseSessionEnded = !_sessionEndedRaised;
                _sessionEndedRaised = true;

                processToDispose = _process;
                cancellationTokenSource = _lifetimeCancellationTokenSource;
                stdinToDispose = _stdin;
                stdoutReaderTask = _stdoutReaderTask;
                stderrReaderTask = _stderrReaderTask;

                _process = null;
                _stdin = null;
                _lifetimeCancellationTokenSource = null;
                _stdoutReaderTask = null;
                _stderrReaderTask = null;
                _readyCompletionSource = null;
                _activeRequest = null;
                _capturingBreakpointPayload = false;
                _ignoreNextDebugPrompt = false;
                _suppressNextDebugPromptCount = 0;
                _currentFrameQueryInProgress = 0;
                _lastLocationNotificationTicks = 0;
                SetCurrentState(DebugSessionState.Stopped);
            }

            try
            {
                cancellationTokenSource?.Cancel();
            }
            catch
            {
            }

            try
            {
                stdinToDispose?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (processToDispose is not null && !processToDispose.HasExited)
                {
                    processToDispose.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                var tasksToWait = new List<Task>(capacity: 2);
                if (stdoutReaderTask is not null)
                {
                    tasksToWait.Add(stdoutReaderTask);
                }

                if (stderrReaderTask is not null)
                {
                    tasksToWait.Add(stderrReaderTask);
                }

                if (tasksToWait.Count > 0)
                {
                    Task.WaitAll(tasksToWait.ToArray(), TimeSpan.FromSeconds(1));
                }
            }
            catch
            {
            }

            try
            {
                processToDispose?.Dispose();
            }
            catch
            {
            }

            cancellationTokenSource?.Dispose();

            if (raiseSessionEnded)
            {
                Trace("Dispose", $"Raising SessionEnded from Dispose; {DescribeSessionState()}");
                SessionEnded?.Invoke();
            }

            Trace("Dispose", $"Completed; {DescribeSessionState()}");
        }

        private async Task SendDebugControlCommandAsync(string debuggerCommand)
        {
            Trace("SendDebugControlCommandAsync", $"Entry; command='{debuggerCommand}'; {DescribeSessionState()}");
            EnsurePaused();

            // Debugger control commands share the same redirected stdin/stdout stream as
            // variable, call-stack, and current-frame requests.  Serialize them through
            // the same gate so a Step/Continue command cannot be interleaved with an
            // in-flight panel refresh request and leave the shell in a stale Running
            // state.
            await _requestGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                EnsurePaused();
                SetCurrentState(DebugSessionState.Running);
                await SendCommandAsync(debuggerCommand, CancellationToken.None).ConfigureAwait(false);
                Trace("SendDebugControlCommandAsync", $"Command sent; command='{debuggerCommand}'; {DescribeSessionState()}");
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private void ThrowIfSessionAlreadyActive()
        {
            lock (_syncRoot)
            {
                if (_process is not null)
                {
                    throw new InvalidOperationException("A debug session is already active.");
                }
            }
        }

        private void EnsurePaused()
        {
            if (CurrentState != DebugSessionState.Paused)
            {
                throw new InvalidOperationException("The debug session is not paused.");
            }
        }

        private async Task SendCommandAsync(string command, CancellationToken cancellationToken)
        {
            StreamWriter? stdin;

            lock (_syncRoot)
            {
                stdin = _stdin;
            }

            if (stdin is null)
            {
                throw new InvalidOperationException("The debug PowerShell process is not available.");
            }

            Trace("SendCommandAsync", $"Writing command; length={command.Length}; commandPreview={SummarizeCommand(command)}; processId={TryGetProcessId(_process)}");
            await stdin.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }

        private async Task<string> SendRequestAsync(
            string command,
            string startMarker,
            string endMarker,
            bool suppressNextDebugPrompt,
            CancellationToken cancellationToken)
        {
            Trace("SendRequestAsync", $"Entry; startMarker='{startMarker}'; endMarker='{endMarker}'; suppressNextDebugPrompt={suppressNextDebugPrompt}; {DescribeSessionState()}");
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = new ActiveRequest(startMarker, endMarker);

            try
            {
                lock (_syncRoot)
                {
                    if (_activeRequest is not null)
                    {
                        throw new InvalidOperationException("A debug request is already in progress.");
                    }

                    if (suppressNextDebugPrompt)
                    {
                        _suppressNextDebugPromptCount++;
                    }

                    _activeRequest = request;
                }

                await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var payload = await request.CompletionSource.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                Trace("SendRequestAsync", $"Completed; startMarker='{startMarker}'; endMarker='{endMarker}'; payloadLength={payload.Length}; {DescribeSessionState()}");
                return payload;
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_activeRequest, request))
                    {
                        _activeRequest = null;
                    }
                }

                _requestGate.Release();
            }
        }

        private async Task ReadLoopAsync(Process owningProcess, TextReader reader, bool isErrorStream, CancellationToken cancellationToken)
        {
            var buffer = new char[2048];
            Trace("ReadLoopAsync", $"Started; isErrorStream={isErrorStream}; processId={TryGetProcessId(owningProcess)}");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    ProcessIncomingChunk(new string(buffer, 0, read), isErrorStream);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                FlushPendingLineBuffer(isErrorStream);
                Trace("ReadLoopAsync", $"Exiting; isErrorStream={isErrorStream}; processId={TryGetProcessId(owningProcess)}");
                HandleProcessExited(owningProcess);
            }
        }

        private void ProcessIncomingChunk(string chunk, bool isErrorStream)
        {
            Trace("ProcessIncomingChunk", $"Chunk received; isErrorStream={isErrorStream}; chunkLength={chunk.Length}; containsReady={chunk.Contains(ReadyMarker, StringComparison.Ordinal)}; containsPrompt={chunk.Contains(DebugPromptMarker, StringComparison.Ordinal)}; containsSessionEnded={chunk.Contains(SessionEndedMarker, StringComparison.Ordinal)}; containsBreakpointMarker={chunk.Contains(BreakpointHitStartMarker, StringComparison.Ordinal)}");
            var lineBuffer = isErrorStream ? _stderrLineBuffer : _stdoutLineBuffer;

            foreach (var ch in chunk)
            {
                if (ch == '\r')
                {
                    continue;
                }

                if (ch == '\n')
                {
                    var line = lineBuffer.ToString();
                    lineBuffer.Clear();
                    ProcessIncomingLine(line, isErrorStream);
                    continue;
                }

                lineBuffer.Append(ch);
            }
        }

        private void FlushPendingLineBuffer(bool isErrorStream)
        {
            var lineBuffer = isErrorStream ? _stderrLineBuffer : _stdoutLineBuffer;
            if (lineBuffer.Length == 0)
            {
                return;
            }

            var line = lineBuffer.ToString();
            lineBuffer.Clear();
            ProcessIncomingLine(line, isErrorStream);
        }

        private void ProcessIncomingLine(string line, bool isErrorStream)
        {
            Trace("ProcessIncomingLine", $"Line received; isErrorStream={isErrorStream}; lineLength={line.Length}; classification={ClassifyLine(line)}; {DescribeSessionState()}");
            if (isErrorStream)
            {
                TryHandleObservedDebugPauseOutput(line, "stderr");
                PublishOutputLine(line);
                return;
            }

            if (string.Equals(line.Trim(), ReadyMarker, StringComparison.Ordinal))
            {
                TaskCompletionSource<bool>? readyCompletionSource;
                lock (_syncRoot)
                {
                    readyCompletionSource = _readyCompletionSource;
                    _readyCompletionSource = null;
                }

                Trace("ProcessIncomingLine", $"Ready marker observed; processId={TryGetProcessId(_process)}");
                readyCompletionSource?.TrySetResult(true);
                return;
            }

            if (line.Contains(SessionEndedMarker, StringComparison.Ordinal))
            {
                Trace("ProcessIncomingLine", $"Session-ended marker observed in line; lineLength={line.Length}; processId={TryGetProcessId(_process)}");
                HandleSessionEndedMarker();

                var remaining = line.Replace(SessionEndedMarker, string.Empty, StringComparison.Ordinal).Trim();
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    PublishOutputLine(remaining);
                }

                return;
            }

            if (string.Equals(line, BreakpointHitStartMarker, StringComparison.Ordinal))
            {
                _capturingBreakpointPayload = true;
                _breakpointPayloadBuffer.Clear();
                return;
            }

            if (_capturingBreakpointPayload)
            {
                if (string.Equals(line, BreakpointHitEndMarker, StringComparison.Ordinal))
                {
                    _capturingBreakpointPayload = false;
                    HandleBreakpointPayload(_breakpointPayloadBuffer.ToString());
                    _breakpointPayloadBuffer.Clear();
                    return;
                }

                _breakpointPayloadBuffer.AppendLine(line);
                return;
            }

            ActiveRequest? activeRequest;
            lock (_syncRoot)
            {
                activeRequest = _activeRequest;
            }

            if (activeRequest is not null)
            {
                if (!activeRequest.IsCapturing && string.Equals(line, activeRequest.StartMarker, StringComparison.Ordinal))
                {
                    activeRequest.IsCapturing = true;
                    activeRequest.Capture.Clear();
                    return;
                }

                if (activeRequest.IsCapturing)
                {
                    if (string.Equals(line, activeRequest.EndMarker, StringComparison.Ordinal))
                    {
                        activeRequest.IsCapturing = false;
                        activeRequest.CompletionSource.TrySetResult(activeRequest.Capture.ToString().Trim());
                        return;
                    }

                    activeRequest.Capture.AppendLine(line);
                    return;
                }
            }

            if (string.Equals(line, DebugPromptMarker, StringComparison.Ordinal))
            {
                HandleDebugPromptMarker();
                return;
            }

            if (line.Length == 0)
            {
                return;
            }

            TryHandleObservedDebugPauseOutput(line, "stdout");
            PublishOutputLine(line);
        }

        private void PublishOutputLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            Trace("PublishOutputLine", $"Forwarding output; lineLength={line.Length}; classification={ClassifyLine(line)}");
            OutputReceived?.Invoke(line + Environment.NewLine);
        }

        private void HandleDebugPromptMarker()
        {
            Trace("HandleDebugPromptMarker", $"Entry; ignoreNext={_ignoreNextDebugPrompt}; suppressNextCount={_suppressNextDebugPromptCount}; {DescribeSessionState()}");

            var wasPaused = CurrentState == DebugSessionState.Paused;

            if (_ignoreNextDebugPrompt)
            {
                _ignoreNextDebugPrompt = false;
                SetCurrentState(DebugSessionState.Paused);
                AppLogger.Info("Debug", "DebugPausedDetected via prompt marker.");
                Trace("HandleDebugPromptMarker", $"Ignored-next prompt consumed; wasPaused={wasPaused}; {DescribeSessionState()}");
                QueueCurrentFrameQueryIfNoRecentLocation("ignored-next prompt marker");
                return;
            }

            if (_suppressNextDebugPromptCount > 0)
            {
                _suppressNextDebugPromptCount--;
                SetCurrentState(DebugSessionState.Paused);
                AppLogger.Info("Debug", "DebugPausedDetected via suppressed prompt marker.");
                Trace("HandleDebugPromptMarker", $"Suppressed prompt consumed; wasPaused={wasPaused}; {DescribeSessionState()}");
                QueueCurrentFrameQueryIfNoRecentLocation("suppressed prompt marker");
                return;
            }

            SetCurrentState(DebugSessionState.Paused);
            AppLogger.Info("Debug", "DebugPausedDetected via prompt marker.");
            Trace("HandleDebugPromptMarker", $"Paused prompt observed; wasPaused={wasPaused}; {DescribeSessionState()}");
            QueueCurrentFrameQueryIfNoRecentLocation("prompt marker");
        }

        private void HandleBreakpointPayload(string payload)
        {
            Trace("HandleBreakpointPayload", $"Entry; payloadLength={payload.Length}; {DescribeSessionState()}");
            SetCurrentState(DebugSessionState.Paused);
            _ignoreNextDebugPrompt = true;
            AppLogger.Info("Debug", "DebugPausedDetected via breakpoint payload.");

            var location = DeserializeSingle<BreakpointLocation>(payload);
            MarkLocationNotificationObserved();
            Trace("HandleBreakpointPayload", $"Raising BreakpointHit; scriptPathPresent={!string.IsNullOrWhiteSpace(location?.ScriptPath)}; lineNumber={location?.LineNumber ?? 0}; {DescribeSessionState()}");
            BreakpointHit?.Invoke(location?.ScriptPath, location?.LineNumber ?? 0);
        }

        private void TryHandleObservedDebugPauseOutput(string line, string source)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var trimmed = line.Trim();
            if (!string.Equals(trimmed, "Entering debug mode. Use h or ? for help.", StringComparison.OrdinalIgnoreCase) &&
                !DebugBreakpointOutputRegex.IsMatch(trimmed) &&
                !DebugLocationOutputRegex.IsMatch(trimmed))
            {
                return;
            }

            var wasPaused = CurrentState == DebugSessionState.Paused;
            SetCurrentState(DebugSessionState.Paused);
            AppLogger.Info("Debug", $"DebugPausedDetected via {source} output: {trimmed}");
            Trace("TryHandleObservedDebugPauseOutput", $"Matched pause output; source={source}; wasPaused={wasPaused}; classification={ClassifyLine(trimmed)}; {DescribeSessionState()}");

            // Do not inject current-frame/variable/call-stack request scripts while
            // the native PowerShell debugger is paused.  Those helper scripts run
            // through the same debug prompt and can themselves be stepped/traced,
            // which created the observed runaway line-by-line execution.  Instead,
            // use the native "At <script>:<line> char:<n>" location text to update
            // the editor highlight without sending another PowerShell command.
            if (TryParseDebugLocation(trimmed, out var scriptPath, out var lineNumber))
            {
                MarkLocationNotificationObserved();
                Trace("TryHandleObservedDebugPauseOutput", $"Raising BreakpointHit from parsed debug location; scriptPathPresent={!string.IsNullOrWhiteSpace(scriptPath)}; lineNumber={lineNumber}; {DescribeSessionState()}");
                BreakpointHit?.Invoke(scriptPath, lineNumber);
            }
        }

        private static bool TryParseDebugLocation(string line, out string? scriptPath, out int lineNumber)
        {
            scriptPath = null;
            lineNumber = 0;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var match = DebugLocationColonCaptureRegex.Match(line.Trim());
            if (!match.Success)
            {
                match = DebugLocationWordCaptureRegex.Match(line.Trim());
            }

            if (!match.Success)
            {
                return false;
            }

            var pathValue = match.Groups["path"].Value.Trim();
            var lineValue = match.Groups["line"].Value.Trim();
            if (string.IsNullOrWhiteSpace(pathValue) ||
                !int.TryParse(lineValue, out var parsedLine) ||
                parsedLine <= 0)
            {
                return false;
            }

            scriptPath = pathValue;
            lineNumber = parsedLine;
            return true;
        }

        private void MarkLocationNotificationObserved()
        {
            Interlocked.Exchange(ref _lastLocationNotificationTicks, Stopwatch.GetTimestamp());
        }

        private void QueueCurrentFrameQueryIfNoRecentLocation(string reason)
        {
            // Native PowerShell usually emits an "At <script>:<line> char:<n>"
            // location near the debug prompt.  Prefer that parsed location because it
            // does not require injecting another helper script into the debugger.  If
            // no location arrives shortly after the prompt marker, fall back to the
            // lightweight current-frame request so Start Debug still visibly pauses
            // and highlights a source line on hosts that do not emit location text
            // in a predictable line-oriented way.
            _ = Task.Run(async () =>
            {
                var promptTicks = Stopwatch.GetTimestamp();

                try
                {
                    await Task.Delay(200).ConfigureAwait(false);

                    if (CurrentState != DebugSessionState.Paused)
                    {
                        Trace("QueueCurrentFrameQueryIfNoRecentLocation", $"Skipped because session is no longer paused; reason={reason}; {DescribeSessionState()}");
                        return;
                    }

                    var lastLocationTicks = Interlocked.Read(ref _lastLocationNotificationTicks);
                    if (lastLocationTicks >= promptTicks)
                    {
                        Trace("QueueCurrentFrameQueryIfNoRecentLocation", $"Skipped because native location was already observed; reason={reason}; {DescribeSessionState()}");
                        return;
                    }

                    QueueCurrentFrameQuery(reason);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    Trace("QueueCurrentFrameQueryIfNoRecentLocation", $"Failed; reason={reason}; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeSessionState()}");
                }
            });
        }

        private void QueueCurrentFrameQuery(string reason)
        {
            if (Interlocked.CompareExchange(ref _currentFrameQueryInProgress, 1, 0) != 0)
            {
                Trace("QueueCurrentFrameQuery", $"Skipped duplicate current-frame query; reason={reason}; {DescribeSessionState()}");
                return;
            }

            Trace("QueueCurrentFrameQuery", $"Scheduling current-frame query; reason={reason}; {DescribeSessionState()}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await QueryCurrentFrameAndRaiseBreakpointAsync().ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _currentFrameQueryInProgress, 0);
                    Trace("QueueCurrentFrameQuery", $"Current-frame query slot released; reason={reason}; {DescribeSessionState()}");
                }
            });
        }

        private async Task QueryCurrentFrameAndRaiseBreakpointAsync()
        {
            Trace("QueryCurrentFrameAndRaiseBreakpointAsync", $"Entry; {DescribeSessionState()}");
            try
            {
                var payload = await SendRequestAsync(
                    BuildCurrentFrameRequestScript(),
                    CurrentFrameStartMarker,
                    CurrentFrameEndMarker,
                    suppressNextDebugPrompt: true,
                    CancellationToken.None).ConfigureAwait(false);

                var location = DeserializeSingle<BreakpointLocation>(payload);
                Trace("QueryCurrentFrameAndRaiseBreakpointAsync", $"Raising BreakpointHit; scriptPathPresent={!string.IsNullOrWhiteSpace(location?.ScriptPath)}; lineNumber={location?.LineNumber ?? 0}; {DescribeSessionState()}");
                BreakpointHit?.Invoke(location?.ScriptPath, location?.LineNumber ?? 0);
            }
            catch (Exception ex)
            {
                Trace("QueryCurrentFrameAndRaiseBreakpointAsync", $"Failed; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeSessionState()}");
                OutputReceived?.Invoke($"Unable to query the paused debug location: {ex.Message}{Environment.NewLine}");
            }
        }

        private void HandleSessionEndedMarker()
        {
            Trace("HandleSessionEndedMarker", $"Entry; {DescribeSessionState()}");
            bool raiseSessionEnded;

            lock (_syncRoot)
            {
                if (_sessionEndedRaised)
                {
                    return;
                }

                _sessionEndedRaised = true;
                SetCurrentState(DebugSessionState.Stopped);
                raiseSessionEnded = true;
            }

            if (raiseSessionEnded)
            {
                Trace("HandleSessionEndedMarker", $"Raising SessionEnded; {DescribeSessionState()}");
                SessionEnded?.Invoke();
            }
        }

        private void HandleProcessExited(Process exitedProcess)
        {
            Trace("HandleProcessExited", $"Entry; exitedProcessId={TryGetProcessId(exitedProcess)}; hasExited={SafeHasExited(exitedProcess)}; exitCode={TryGetExitCode(exitedProcess)}; {DescribeSessionState()}");
            bool shouldRaiseSessionEnded = false;

            lock (_syncRoot)
            {
                if (!ReferenceEquals(_process, exitedProcess))
                {
                    return;
                }

                if (!_sessionEndedRaised)
                {
                    _sessionEndedRaised = true;
                    shouldRaiseSessionEnded = true;
                }

                SetCurrentState(DebugSessionState.Stopped);
            }

            if (shouldRaiseSessionEnded)
            {
                Trace("HandleProcessExited", $"Raising SessionEnded after process exit; exitedProcessId={TryGetProcessId(exitedProcess)}; {DescribeSessionState()}");
                SessionEnded?.Invoke();
            }
        }

        private static IReadOnlyList<T> DeserializeList<T>(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<T>();
            }

            try
            {
                var list = JsonSerializer.Deserialize<List<T>>(payload, JsonOptions);
                if (list is not null)
                {
                    return list;
                }
            }
            catch (JsonException)
            {
            }

            try
            {
                var single = JsonSerializer.Deserialize<T>(payload, JsonOptions);
                return single is null ? Array.Empty<T>() : new[] { single };
            }
            catch (JsonException)
            {
                return Array.Empty<T>();
            }
        }

        private static T? DeserializeSingle<T>(string payload) where T : class
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void SetCurrentState(DebugSessionState newState)
        {
            if (CurrentState == newState)
            {
                Trace("SetCurrentState", $"No-op; state remains {newState}; {DescribeSessionState()}");
                return;
            }

            var oldState = CurrentState;
            CurrentState = newState;
            AppLogger.Info("Debug", $"DebugStateChanged: {newState}");
            DeveloperDiagnostics.LogStateTransition("Debugger", "DebugSessionStateChanged", oldState.ToString(), newState.ToString(), "PsesDebugSession state changed.", new Dictionary<string, object?> { ["processId"] = TryGetProcessId(_process) });
            Trace("SetCurrentState", $"Transition; oldState={oldState}; newState={newState}; processId={TryGetProcessId(_process)}; sessionEndedRaised={_sessionEndedRaised}");
            Trace("SetCurrentState", $"Raising StateChanged; newState={newState}; processId={TryGetProcessId(_process)}");
            StateChanged?.Invoke(newState);
        }

        private void Trace(string source, string message)
        {
            DebuggerTraceLogger.Write($"PsesDebugSession.{source}", message);
        }

        private string DescribeSessionState()
        {
            return $"currentState={CurrentState}; disposed={_disposed}; sessionEndedRaised={_sessionEndedRaised}; processNull={(_process is null)}; processId={TryGetProcessId(_process)}; hasExited={SafeHasExited(_process)}; activeRequest={(_activeRequest is not null)}; capturingBreakpointPayload={_capturingBreakpointPayload}; currentFrameQueryInProgress={Volatile.Read(ref _currentFrameQueryInProgress)}; ignoreNextPrompt={_ignoreNextDebugPrompt}; suppressNextPromptCount={_suppressNextDebugPromptCount}";
        }

        private static int TryGetProcessId(Process? process)
        {
            if (process is null)
            {
                return -1;
            }

            try
            {
                return process.Id;
            }
            catch
            {
                return -1;
            }
        }

        private static bool SafeHasExited(Process? process)
        {
            if (process is null)
            {
                return false;
            }

            try
            {
                return process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static string TryGetExitCode(Process? process)
        {
            if (process is null)
            {
                return "(null)";
            }

            try
            {
                return process.HasExited ? process.ExitCode.ToString() : "(running)";
            }
            catch
            {
                return "(unavailable)";
            }
        }

        private static string ClassifyLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return "Empty";
            }

            if (string.Equals(line, ReadyMarker, StringComparison.Ordinal))
            {
                return "ReadyMarker";
            }

            if (string.Equals(line, SessionEndedMarker, StringComparison.Ordinal))
            {
                return "SessionEndedMarker";
            }

            if (string.Equals(line, DebugPromptMarker, StringComparison.Ordinal))
            {
                return "DebugPromptMarker";
            }

            if (string.Equals(line, BreakpointHitStartMarker, StringComparison.Ordinal))
            {
                return "BreakpointHitStartMarker";
            }

            if (string.Equals(line, BreakpointHitEndMarker, StringComparison.Ordinal))
            {
                return "BreakpointHitEndMarker";
            }

            var trimmed = line.Trim();
            if (string.Equals(trimmed, "Entering debug mode. Use h or ? for help.", StringComparison.OrdinalIgnoreCase))
            {
                return "EnteringDebugMode";
            }

            if (DebugBreakpointOutputRegex.IsMatch(trimmed))
            {
                return "BreakpointOutput";
            }

            if (DebugLocationOutputRegex.IsMatch(trimmed))
            {
                return "DebugLocationOutput";
            }

            return "PlainOutput";
        }

        private static string SummarizeCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return "(empty)";
            }

            var trimmed = command.Trim();
            if (string.Equals(trimmed, "c", StringComparison.Ordinal) ||
                string.Equals(trimmed, "s", StringComparison.Ordinal) ||
                string.Equals(trimmed, "v", StringComparison.Ordinal) ||
                string.Equals(trimmed, "o", StringComparison.Ordinal))
            {
                return trimmed;
            }

            if (trimmed.Contains(ReadyMarker, StringComparison.Ordinal))
            {
                return "BootstrapScript";
            }

            if (trimmed.Contains(SessionEndedMarker, StringComparison.Ordinal))
            {
                return "StartScript";
            }

            if (trimmed.Contains(CurrentFrameStartMarker, StringComparison.Ordinal))
            {
                return "CurrentFrameRequest";
            }

            if (trimmed.Contains(VariablesStartMarker, StringComparison.Ordinal))
            {
                return "VariablesRequest";
            }

            if (trimmed.Contains(CallStackStartMarker, StringComparison.Ordinal))
            {
                return "CallStackRequest";
            }

            return trimmed.Length > 48 ? trimmed[..48] : trimmed;
        }

        private static string BuildBootstrapScript()
        {
            var builder = new StringBuilder();
            builder.AppendLine("$ErrorActionPreference = 'Continue'");
            builder.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            builder.AppendLine("$global:__PSSDebugRunActive = $false");
            builder.AppendLine("function global:prompt {");
            builder.AppendLine($"    if ($null -ne $PSDebugContext) {{ Write-Output '{DebugPromptMarker}' }}");
            builder.AppendLine("    elseif ($global:__PSSDebugRunActive -eq $true) {");
            builder.AppendLine("        $global:__PSSDebugRunActive = $false");
            builder.AppendLine($"        [Console]::Out.WriteLine('{SessionEndedMarker}')");
            builder.AppendLine("        [Console]::Out.Flush()");
            builder.AppendLine("        exit");
            builder.AppendLine("    }");
            builder.AppendLine("    return ''");
            builder.AppendLine("}");
            builder.AppendLine($"Write-Output '{ReadyMarker}'");
            return builder.ToString();
        }

        private static string BuildStartScript(string launchScriptPath, IReadOnlyList<DebugBreakpointInfo> breakpoints)
        {
            var builder = new StringBuilder();
            builder.AppendLine("& {");
            builder.AppendLine("    $ErrorActionPreference = 'Stop'");
            builder.AppendLine("    $global:__PSSDebugRunActive = $true");
            builder.AppendLine("    Get-PSBreakpoint -ErrorAction SilentlyContinue | Remove-PSBreakpoint -ErrorAction SilentlyContinue");

            foreach (var breakpoint in breakpoints)
            {
                if (string.IsNullOrWhiteSpace(breakpoint.ScriptPath) || breakpoint.LineNumber <= 0)
                {
                    continue;
                }

                builder.AppendLine(BuildBreakpointRegistrationStatement(breakpoint));
            }

            builder.AppendLine("    try {");
            builder.Append("        & ").AppendLine(ToPowerShellLiteral(launchScriptPath));
            builder.AppendLine("    }");
            builder.AppendLine("    finally {");
            builder.AppendLine("        $global:__PSSDebugRunActive = $false");
            builder.Append("        [Console]::Out.WriteLine('").Append(SessionEndedMarker).AppendLine("')");
            builder.AppendLine("        [Console]::Out.Flush()");
            builder.AppendLine("        exit");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string BuildBreakpointRegistrationStatement(DebugBreakpointInfo breakpoint)
        {
            var scriptLiteral = ToPowerShellLiteral(breakpoint.ScriptPath);

            // Register a normal PowerShell line breakpoint and let the native
            // debugger stop on the user's script line.  The previous action-based
            // breakpoint emitted custom markers and then used `break`; that could
            // leave stepping positioned in the breakpoint action rather than in the
            // user's script.  The prompt marker now supplies the paused notification
            // and current-frame query after every breakpoint and step stop.
            return $"Set-PSBreakpoint -Script {scriptLiteral} -Line {breakpoint.LineNumber} | Out-Null";
        }

        private static string BuildCurrentFrameRequestScript()
        {
            var builder = new StringBuilder();
            builder.AppendLine("$__pssInvocation = $null");
            builder.AppendLine("if ($null -ne $PSDebugContext -and $null -ne $PSDebugContext.InvocationInfo) { $__pssInvocation = $PSDebugContext.InvocationInfo }");
            builder.AppendLine("$__pssFrame = if ($null -eq $__pssInvocation) { Get-PSCallStack | Select-Object -First 1 } else { $null }");
            builder.Append("Write-Output '").Append(CurrentFrameStartMarker).AppendLine("'");
            builder.AppendLine("$payload = if ($null -ne $__pssInvocation) { [pscustomobject]@{ ScriptPath = [string]$__pssInvocation.ScriptName; LineNumber = [int]$__pssInvocation.ScriptLineNumber } } elseif ($null -ne $__pssFrame) { [pscustomobject]@{ ScriptPath = [string]$__pssFrame.ScriptName; LineNumber = [int]$__pssFrame.ScriptLineNumber } } else { [pscustomobject]@{ ScriptPath = ''; LineNumber = 0 } }");
            builder.AppendLine("$payload | ConvertTo-Json -Compress -Depth 4");
            builder.Append("Write-Output '").Append(CurrentFrameEndMarker).AppendLine("'");
            return builder.ToString();
        }

        private static string BuildVariablesRequestScript()
        {
            var builder = new StringBuilder();
            builder.AppendLine("$items = @(Get-Variable | Sort-Object Name | ForEach-Object {");
            builder.AppendLine("    $value = $_.Value");
            builder.AppendLine("    $typeName = if ($null -eq $value) { 'null' } else { $value.GetType().Name }");
            builder.AppendLine("    $valueText = try { ($value | Out-String).Trim() } catch { '<unavailable>' }");
            builder.AppendLine("    [pscustomobject]@{ Name = $_.Name; Type = [string]$typeName; Value = [string]$valueText }");
            builder.AppendLine("})");
            builder.Append("Write-Output '").Append(VariablesStartMarker).AppendLine("'");
            builder.AppendLine("$json = $items | ConvertTo-Json -Compress -Depth 5");
            builder.AppendLine("Write-Output $json");
            builder.Append("Write-Output '").Append(VariablesEndMarker).AppendLine("'");
            return builder.ToString();
        }

        private static string BuildCallStackRequestScript()
        {
            var builder = new StringBuilder();
            builder.AppendLine("$items = @(Get-PSCallStack | ForEach-Object {");
            builder.AppendLine("    [pscustomobject]@{");
            builder.AppendLine("        FunctionName = [string]$_.FunctionName");
            builder.AppendLine("        ScriptName = [string]$_.ScriptName");
            builder.AppendLine("        LineNumber = [int]$_.ScriptLineNumber");
            builder.AppendLine("    }");
            builder.AppendLine("})");
            builder.Append("Write-Output '").Append(CallStackStartMarker).AppendLine("'");
            builder.AppendLine("$json = $items | ConvertTo-Json -Compress -Depth 5");
            builder.AppendLine("Write-Output $json");
            builder.Append("Write-Output '").Append(CallStackEndMarker).AppendLine("'");
            return builder.ToString();
        }

        private static string ToPowerShellLiteral(string value)
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        private sealed class ActiveRequest
        {
            public ActiveRequest(string startMarker, string endMarker)
            {
                StartMarker = startMarker;
                EndMarker = endMarker;
            }

            public string StartMarker { get; }
            public string EndMarker { get; }
            public bool IsCapturing { get; set; }
            public StringBuilder Capture { get; } = new();
            public TaskCompletionSource<string> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class BreakpointLocation
        {
            public string? ScriptPath { get; set; }
            public int LineNumber { get; set; }
        }
    }
}
