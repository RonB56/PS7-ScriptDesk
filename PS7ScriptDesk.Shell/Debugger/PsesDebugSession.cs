using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Shell.Debug
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
        private const int DebugRequestTimeoutSeconds = 20;
        private const int OrphanedRequestOutputSuppressionMilliseconds = 30000;
        private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(1);
        private static readonly string[] InternalRequestStartMarkers =
        {
            CurrentFrameStartMarker,
            VariablesStartMarker,
            CallStackStartMarker
        };
        private static readonly string[] InternalRequestEndMarkers =
        {
            CurrentFrameEndMarker,
            VariablesEndMarker,
            CallStackEndMarker
        };
        private static readonly Regex DebugBreakpointOutputRegex = new(@"^Hit .+ breakpoint on ", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebugLocationOutputRegex = new(@"^At\s+.+(?:(?:\s+line\s+\d+\s+char:\s*\d+)|(?::\d+\s+char:\s*\d+))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebugLocationColonCaptureRegex = new(@"^At\s+(?<path>.+):(?<line>\d+)\s+char:\s*\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DebugLocationWordCaptureRegex = new(@"^At\s+(?<path>.+)\s+line\s+(?<line>\d+)\s+char:\s*\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AnsiControlSequenceRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex OscControlSequenceRegex = new(@"\x1B\].*?(\x07|\x1B\\)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

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
        private Task<bool>? _stopTask;
        private TaskCompletionSource<bool>? _readyCompletionSource;
        private ActiveRequest? _activeRequest;
        private bool _capturingBreakpointPayload;
        private bool _ignoreNextDebugPrompt;
        private int _suppressNextDebugPromptCount;
        private int _currentFrameQueryInProgress;
        private bool _sessionEndedRaised;
        private bool _userStopRequested;
        private bool _startupFailure;
        private bool _transportFailure;
        private bool _normalCompletionObserved;
        private DebugExceptionInfo? _exceptionInfo;
        private DebugTerminationInfo? _terminationInfo;
        private bool _disposed;
        private long _lastLocationNotificationTicks;
        private string? _orphanedRequestOutputEndMarker;
        private long _orphanedRequestOutputSuppressUntilTicks;
        private Action<DebugSessionState>? _stateChanged;
        private Action<string?, int>? _breakpointHit;
        private Action? _sessionEnded;
        private Action<string>? _outputReceived;
        private Action<DebuggerEvent>? _typedEventReceived;
        private int _stateChangedSubscriberCount;
        private int _breakpointHitSubscriberCount;
        private int _sessionEndedSubscriberCount;
        private int _outputReceivedSubscriberCount;
        private int _typedEventReceivedSubscriberCount;
        private Action<DebugTerminationInfo>? _terminated;
        private int _terminatedSubscriberCount;
        private long _eventSequence;
        private long _pauseGeneration;

        public DebugSessionState CurrentState { get; private set; } = DebugSessionState.Stopped;
        public Guid SessionId { get; } = Guid.NewGuid();
        public long PauseGeneration => Interlocked.Read(ref _pauseGeneration);
        public DebugTerminationInfo? TerminationInfo => _terminationInfo;
        public DebuggerPauseReason CurrentPauseReason { get; private set; } = DebuggerPauseReason.Unknown;

        public event Action<DebugSessionState>? StateChanged
        {
            add => AddSubscriber(ref _stateChanged, ref _stateChangedSubscriberCount, value);
            remove => RemoveSubscriber(ref _stateChanged, ref _stateChangedSubscriberCount, value);
        }

        public event Action<string?, int>? BreakpointHit
        {
            add => AddSubscriber(ref _breakpointHit, ref _breakpointHitSubscriberCount, value);
            remove => RemoveSubscriber(ref _breakpointHit, ref _breakpointHitSubscriberCount, value);
        }

        public event Action? SessionEnded
        {
            add => AddSubscriber(ref _sessionEnded, ref _sessionEndedSubscriberCount, value);
            remove => RemoveSubscriber(ref _sessionEnded, ref _sessionEndedSubscriberCount, value);
        }

        public event Action<DebugTerminationInfo>? Terminated
        {
            add => AddSubscriber(ref _terminated, ref _terminatedSubscriberCount, value);
            remove => RemoveSubscriber(ref _terminated, ref _terminatedSubscriberCount, value);
        }

        public event Action<string>? OutputReceived
        {
            add => AddSubscriber(ref _outputReceived, ref _outputReceivedSubscriberCount, value);
            remove => RemoveSubscriber(ref _outputReceived, ref _outputReceivedSubscriberCount, value);
        }

        public event Action<DebuggerEvent>? TypedEventReceived
        {
            add => AddSubscriber(ref _typedEventReceived, ref _typedEventReceivedSubscriberCount, value);
            remove => RemoveSubscriber(ref _typedEventReceived, ref _typedEventReceivedSubscriberCount, value);
        }

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

            ArgumentNullException.ThrowIfNull(breakpoints);

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
                _terminationInfo = null;
                _userStopRequested = false;
                _startupFailure = false;
                _transportFailure = false;
                _normalCompletionObserved = false;
                _exceptionInfo = null;
                CurrentPauseReason = DebuggerPauseReason.Unknown;
                Interlocked.Exchange(ref _eventSequence, 0);
                _ignoreNextDebugPrompt = false;
                _suppressNextDebugPromptCount = 0;
                _currentFrameQueryInProgress = 0;
                _lastLocationNotificationTicks = 0;
                _orphanedRequestOutputEndMarker = null;
                _orphanedRequestOutputSuppressUntilTicks = 0;
                _capturingBreakpointPayload = false;
                _breakpointPayloadBuffer.Clear();
            }

            SetCurrentState(DebugSessionState.Starting);

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
                _startupFailure = true;
                Trace("StartAsync", $"Failed; processId={TryGetProcessId(process)}; {DescribeSessionState()}");
                await StopInternalAsync().ConfigureAwait(false);
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

            var frameId = $"{SessionId:N}/{PauseGeneration}/0";
            return DeserializeList<DebugVariableInfo>(payload, "VariablesRequest")
                .Select(variable => variable with
                {
                    SessionId = SessionId,
                    PauseGeneration = PauseGeneration,
                    Scope = "Current",
                    FrameId = frameId,
                    IsNull = string.Equals(variable.Type, "null", StringComparison.OrdinalIgnoreCase),
                    IsTruncated = variable.Value.EndsWith("...", StringComparison.Ordinal),
                    HasChildren = variable.Value.StartsWith("Dictionary Count=", StringComparison.Ordinal) || variable.Value.StartsWith("Collection Count=", StringComparison.Ordinal),
                    IsExpandable = false,
                    LoadState = "Loaded"
                })
                .ToArray();
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

            return DeserializeList<DebugCallStackFrame>(payload, "CallStackRequest")
                .Select((frame, index) => frame with
                {
                    SessionId = SessionId,
                    PauseGeneration = PauseGeneration,
                    FrameIndex = index,
                    IsCurrentFrame = index == 0,
                    IsSelectedInspectionFrame = index == 0,
                    IsNavigable = frame.LineNumber > 0 && !string.IsNullOrWhiteSpace(frame.ScriptName) && File.Exists(frame.ScriptName)
                })
                .ToArray();
        }

        public void Dispose()
        {
            _ = StopInternalAsync();
            GC.SuppressFinalize(this);
        }

        public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_syncRoot)
            {
                _userStopRequested = true;
            }
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> StopInternalAsync(CancellationToken cancellationToken = default)
        {
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> StopCoreAsync(CancellationToken cancellationToken)
        {
            Task<bool> stopTask;
            lock (_syncRoot)
            {
                _stopTask ??= BeginStopCore();
                stopTask = _stopTask;
            }

            return await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private Task<bool> BeginStopCore()
        {
            Trace("StopAsync", $"Entry; {DescribeSessionState()}");

            Process? processToDispose = null;
            CancellationTokenSource? cancellationTokenSource = null;
            StreamWriter? stdinToDispose = null;
            Task? stdoutReaderTask = null;
            Task? stderrReaderTask = null;
            bool raiseSessionEnded = false;

            if (_disposed)
            {
                return Task.FromResult(true);
            }

            _disposed = true;
            raiseSessionEnded = !_sessionEndedRaised;

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
            _orphanedRequestOutputEndMarker = null;
            _orphanedRequestOutputSuppressUntilTicks = 0;
            // The termination record is published after bounded process teardown so
            // process-exit and reader-drain evidence are available to the UI.

            var teardownState = new DebugProcessTeardownState(
                processToDispose,
                cancellationTokenSource,
                stdinToDispose,
                stdoutReaderTask,
                stderrReaderTask,
                raiseSessionEnded,
                _userStopRequested,
                _startupFailure,
                _transportFailure,
                _normalCompletionObserved,
                _exceptionInfo);
            return Task.Run(() => StopDetachedProcessAsync(teardownState));
        }

        private async Task<bool> StopDetachedProcessAsync(DebugProcessTeardownState teardownState)
        {
            var stopwatch = Stopwatch.StartNew();
            var processStopped = teardownState.Process is null;
            var readersDrained = true;

            try
            {
                try
                {
                    teardownState.CancellationTokenSource?.Cancel();
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException("Debugger", ex, "Debugger lifetime cancellation failed during teardown.");
                }

                try
                {
                    teardownState.StandardInput?.Dispose();
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException("Debugger", ex, "Debugger stdin disposal failed during teardown.");
                }

                var process = teardownState.Process;
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            using var processTimeout = new CancellationTokenSource(ProcessStopTimeout);
                            await process.WaitForExitAsync(processTimeout.Token).ConfigureAwait(false);
                        }

                        processStopped = process.HasExited;
                    }
                    catch (Exception ex)
                    {
                        processStopped = SafeHasExited(process);
                        DeveloperDiagnostics.LogException(
                            "Debugger",
                            ex,
                            "Debugger process did not confirm exit during bounded teardown.",
                            new Dictionary<string, object?>
                            {
                                ["processId"] = TryGetProcessId(process),
                                ["processStopped"] = processStopped,
                                ["timeoutMs"] = ProcessStopTimeout.TotalMilliseconds
                            });
                    }
                }

                var readerTasks = new List<Task>(capacity: 2);
                if (teardownState.StandardOutputReaderTask is not null)
                {
                    readerTasks.Add(teardownState.StandardOutputReaderTask);
                }

                if (teardownState.StandardErrorReaderTask is not null)
                {
                    readerTasks.Add(teardownState.StandardErrorReaderTask);
                }

                if (readerTasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(readerTasks)
                            .WaitAsync(ReaderDrainTimeout)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        readersDrained = false;
                        DeveloperDiagnostics.LogException(
                            "Debugger",
                            ex,
                            "Debugger output readers did not drain during bounded teardown.",
                            new Dictionary<string, object?>
                            {
                                ["readerCount"] = readerTasks.Count,
                                ["readersDrained"] = readersDrained,
                                ["timeoutMs"] = ReaderDrainTimeout.TotalMilliseconds
                            });
                    }
                }
            }
            finally
            {
                try
                {
                    teardownState.Process?.Dispose();
                }
                catch
                {
                }

                try
                {
                    teardownState.CancellationTokenSource?.Dispose();
                }
                catch
                {
                }

                if (teardownState.RaiseSessionEnded)
                {
                    Trace("StopAsync", $"Raising SessionEnded from bounded teardown; {DescribeSessionState()}");
                    FinalizeTermination("BoundedTeardown", streamSource: null, teardownState);
                }
            }

            var succeeded = processStopped && readersDrained;
            stopwatch.Stop();
            AppLogger.Info(
                "Debug",
                $"Debugger teardown completed. Succeeded={succeeded}, ProcessStopped={processStopped}, ReadersDrained={readersDrained}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            DeveloperDiagnostics.LogStateTransition(
                "Debugger",
                "DebuggerProcessTeardown",
                "Stopping",
                succeeded ? "Stopped" : "StopIncomplete",
                "Bounded debugger process teardown completed.",
                new Dictionary<string, object?>
                {
                    ["succeeded"] = succeeded,
                    ["processStopped"] = processStopped,
                    ["readersDrained"] = readersDrained,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                });

            Trace("StopAsync", $"Completed; succeeded={succeeded}; elapsedMs={stopwatch.ElapsedMilliseconds}; {DescribeSessionState()}");
            return succeeded;
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
                PublishTypedEvent(
                    DebuggerEventCategory.Step,
                    DebuggerEventSeverity.Information,
                    "DebuggerControl",
                    $"Debugger control command accepted: {debuggerCommand}.");
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
            Trace("SendRequestAsync", $"Entry; startMarker='{startMarker}'; endMarker='{endMarker}'; suppressNextDebugPrompt={suppressNextDebugPrompt}; timeoutSeconds={DebugRequestTimeoutSeconds}; {DescribeSessionState()}");
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = new ActiveRequest(startMarker, endMarker);
            var completed = false;

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

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(DebugRequestTimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var payload = await request.CompletionSource.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                completed = true;
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

                if (!completed)
                {
                    BeginOrphanedRequestOutputSuppression(endMarker, $"Request did not complete normally; startMarker='{startMarker}'");
                }

                _requestGate.Release();
            }
        }

        private async Task ReadLoopAsync(Process owningProcess, TextReader reader, bool isErrorStream, CancellationToken cancellationToken)
        {
            var buffer = new char[2048];
            var terminationReason = ReaderTerminationReason.EndOfStream;
            Trace("ReadLoopAsync", $"Started; isErrorStream={isErrorStream}; processId={TryGetProcessId(owningProcess)}");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        terminationReason = ReaderTerminationReason.EndOfStream;
                        break;
                    }

                    ProcessIncomingChunk(new string(buffer, 0, read), isErrorStream);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    terminationReason = ReaderTerminationReason.Cancellation;
                }
            }
            catch (OperationCanceledException)
            {
                terminationReason = ReaderTerminationReason.Cancellation;
            }
            catch (ObjectDisposedException)
            {
                terminationReason = ReaderTerminationReason.Disposed;
            }
            catch (Exception ex)
            {
                terminationReason = ReaderTerminationReason.UnexpectedFailure;
                ReportUnexpectedReaderFailure(owningProcess, isErrorStream, "ReadLoop", ex);
            }
            finally
            {
                if (terminationReason != ReaderTerminationReason.UnexpectedFailure)
                {
                    try
                    {
                        FlushPendingLineBuffer(isErrorStream);
                    }
                    catch (OperationCanceledException)
                    {
                        terminationReason = ReaderTerminationReason.Cancellation;
                    }
                    catch (ObjectDisposedException)
                    {
                        terminationReason = ReaderTerminationReason.Disposed;
                    }
                    catch (Exception ex)
                    {
                        terminationReason = ReaderTerminationReason.UnexpectedFailure;
                        ReportUnexpectedReaderFailure(owningProcess, isErrorStream, "FlushPendingLineBuffer", ex);
                    }
                }

                if (terminationReason == ReaderTerminationReason.EndOfStream)
                {
                    if (SafeHasExited(owningProcess))
                    {
                        HandleProcessExited(owningProcess);
                    }
                    else
                    {
                        terminationReason = ReaderTerminationReason.UnexpectedFailure;
                        ReportUnexpectedReaderFailure(
                            owningProcess,
                            isErrorStream,
                            "EndOfStreamBeforeProcessExit",
                            new IOException("Debugger output stream reached end-of-stream while the debugger process was still alive."));
                    }
                }

                Trace("ReadLoopAsync", $"Exiting; isErrorStream={isErrorStream}; terminationReason={terminationReason}; processId={TryGetProcessId(owningProcess)}");
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
            var normalizedLine = NormalizeDebuggerOutputLine(line);
            Trace("ProcessIncomingLine", $"Line received; isErrorStream={isErrorStream}; lineLength={line.Length}; normalizedLength={normalizedLine.Length}; classification={ClassifyLine(normalizedLine)}; {DescribeSessionState()}");
            if (!isErrorStream && DebugExceptionEnvelope.TryDecode(normalizedLine, out var exception) && exception is not null)
            {
                _exceptionInfo = exception;
                PublishTypedEvent(
                    DebuggerEventCategory.Exception,
                    DebuggerEventSeverity.Error,
                    "PowerShellException",
                    exception.Message,
                    filePath: exception.ScriptPath,
                    lineNumber: exception.LineNumber,
                    details: $"type={exception.ExceptionType}; errorId={exception.FullyQualifiedErrorId ?? "(none)"}; handled={exception.IsHandled}; terminating={exception.IsTerminating}",
                    isNavigable: !string.IsNullOrWhiteSpace(exception.ScriptPath) && exception.LineNumber is > 0,
                    isExpandable: true);
                Trace("ProcessIncomingLine", $"Structured exception envelope observed; exceptionType={exception.ExceptionType}; handled={exception.IsHandled}; lineNumber={exception.LineNumber}; {DescribeSessionState()}");
                return;
            }
            if (isErrorStream)
            {
                if (TryHandleObservedDebugPauseOutput(normalizedLine, "stderr"))
                {
                    return;
                }

                PublishOutputLine(line, "ProcessIncomingLine", "stderr");
                return;
            }

            if (IsMarkerLine(normalizedLine, ReadyMarker))
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

            if (LineContainsMarker(normalizedLine, SessionEndedMarker))
            {
                Trace("ProcessIncomingLine", $"Session-ended marker observed in line; lineLength={line.Length}; processId={TryGetProcessId(_process)}");
                HandleSessionEndedMarker();

                var remaining = RemoveMarker(normalizedLine, SessionEndedMarker).Trim();
                if (!string.IsNullOrWhiteSpace(remaining) && !IsInternalDebuggerNoiseLine(remaining))
                {
                    PublishOutputLine(remaining, "SessionEndedMarkerRemainder", "stdout");
                }

                return;
            }

            if (LineContainsMarker(normalizedLine, BreakpointHitStartMarker))
            {
                _capturingBreakpointPayload = true;
                _breakpointPayloadBuffer.Clear();
                Trace("ProcessIncomingLine", "Breakpoint payload capture started.");
                return;
            }

            if (_capturingBreakpointPayload)
            {
                if (LineContainsMarker(normalizedLine, BreakpointHitEndMarker))
                {
                    _capturingBreakpointPayload = false;
                    HandleBreakpointPayload(_breakpointPayloadBuffer.ToString());
                    _breakpointPayloadBuffer.Clear();
                    return;
                }

                _breakpointPayloadBuffer.AppendLine(normalizedLine);
                return;
            }

            ActiveRequest? activeRequest;
            lock (_syncRoot)
            {
                activeRequest = _activeRequest;
            }

            if (activeRequest is not null)
            {
                if (!activeRequest.IsCapturing && LineContainsMarker(normalizedLine, activeRequest.StartMarker))
                {
                    activeRequest.IsCapturing = true;
                    activeRequest.Capture.Clear();
                    Trace("ProcessIncomingLine", $"Active request capture started; startMarker='{activeRequest.StartMarker}'.");
                    return;
                }

                if (activeRequest.IsCapturing)
                {
                    if (LineContainsMarker(normalizedLine, activeRequest.EndMarker))
                    {
                        activeRequest.IsCapturing = false;
                        activeRequest.CompletionSource.TrySetResult(activeRequest.Capture.ToString().Trim());
                        Trace("ProcessIncomingLine", $"Active request capture completed; endMarker='{activeRequest.EndMarker}'; payloadLength={activeRequest.Capture.Length}.");
                        return;
                    }

                    if (!IsInternalDebuggerNoiseLine(normalizedLine))
                    {
                        activeRequest.Capture.AppendLine(normalizedLine);
                    }

                    return;
                }
            }

            if (TrySuppressInternalRequestOutput(normalizedLine))
            {
                return;
            }

            if (LineContainsMarker(normalizedLine, DebugPromptMarker))
            {
                HandleDebugPromptMarker("stdout");
                return;
            }

            if (normalizedLine.Length == 0)
            {
                return;
            }

            if (TryHandleObservedDebugPauseOutput(normalizedLine, "stdout"))
            {
                return;
            }

            PublishOutputLine(line, "ProcessIncomingLine", "stdout");
        }

        private bool TrySuppressInternalRequestOutput(string normalizedLine)
        {
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return false;
            }

            string? orphanedEndMarker;
            var now = Stopwatch.GetTimestamp();
            lock (_syncRoot)
            {
                orphanedEndMarker = _orphanedRequestOutputEndMarker;
                if (!string.IsNullOrWhiteSpace(orphanedEndMarker) && now > _orphanedRequestOutputSuppressUntilTicks)
                {
                    Trace("TrySuppressInternalRequestOutput", $"Expired orphaned request output suppression; endMarker='{orphanedEndMarker}'.");
                    _orphanedRequestOutputEndMarker = null;
                    _orphanedRequestOutputSuppressUntilTicks = 0;
                    orphanedEndMarker = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(orphanedEndMarker))
            {
                if (LineContainsMarker(normalizedLine, orphanedEndMarker))
                {
                    lock (_syncRoot)
                    {
                        if (string.Equals(_orphanedRequestOutputEndMarker, orphanedEndMarker, StringComparison.Ordinal))
                        {
                            _orphanedRequestOutputEndMarker = null;
                            _orphanedRequestOutputSuppressUntilTicks = 0;
                        }
                    }

                    Trace("TrySuppressInternalRequestOutput", $"Suppressed orphaned request end marker; endMarker='{orphanedEndMarker}'.");
                    return true;
                }

                Trace("TrySuppressInternalRequestOutput", $"Suppressed orphaned request output; waitingForEndMarker='{orphanedEndMarker}'; classification={ClassifyLine(normalizedLine)}.");
                return true;
            }

            for (var index = 0; index < InternalRequestStartMarkers.Length; index++)
            {
                var startMarker = InternalRequestStartMarkers[index];
                if (LineContainsMarker(normalizedLine, startMarker))
                {
                    var endMarker = InternalRequestEndMarkers[index];
                    BeginOrphanedRequestOutputSuppression(endMarker, $"Observed internal request start marker without an active request; startMarker='{startMarker}'");
                    Trace("TrySuppressInternalRequestOutput", $"Suppressed unmatched internal request start marker; startMarker='{startMarker}'; endMarker='{endMarker}'.");
                    return true;
                }
            }

            foreach (var endMarker in InternalRequestEndMarkers)
            {
                if (LineContainsMarker(normalizedLine, endMarker))
                {
                    Trace("TrySuppressInternalRequestOutput", $"Suppressed unmatched internal request end marker; endMarker='{endMarker}'.");
                    return true;
                }
            }

            return false;
        }

        private void BeginOrphanedRequestOutputSuppression(string endMarker, string reason)
        {
            if (string.IsNullOrWhiteSpace(endMarker))
            {
                return;
            }

            var suppressUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (OrphanedRequestOutputSuppressionMilliseconds / 1000.0));
            lock (_syncRoot)
            {
                _orphanedRequestOutputEndMarker = endMarker;
                _orphanedRequestOutputSuppressUntilTicks = suppressUntil;
            }

            Trace("BeginOrphanedRequestOutputSuppression", $"Started; endMarker='{endMarker}'; durationMs={OrphanedRequestOutputSuppressionMilliseconds}; reason={reason}; {DescribeSessionState()}");
            DeveloperDiagnostics.LogDecision(
                "Debugger",
                "DebugRequestOutputSuppression",
                "Internal debug request output suppression was started to prevent helper JSON/markers from reaching the user console.",
                "SuppressOrphanedInternalRequestOutput",
                new Dictionary<string, object?>
                {
                    ["endMarker"] = endMarker,
                    ["durationMs"] = OrphanedRequestOutputSuppressionMilliseconds,
                    ["reason"] = reason
                });
        }

        private static bool IsInternalDebuggerNoiseLine(string normalizedLine)
        {
            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                return true;
            }

            if (LineContainsMarker(normalizedLine, DebugPromptMarker) ||
                LineContainsMarker(normalizedLine, CurrentFrameStartMarker) ||
                LineContainsMarker(normalizedLine, CurrentFrameEndMarker) ||
                LineContainsMarker(normalizedLine, VariablesStartMarker) ||
                LineContainsMarker(normalizedLine, VariablesEndMarker) ||
                LineContainsMarker(normalizedLine, CallStackStartMarker) ||
                LineContainsMarker(normalizedLine, CallStackEndMarker) ||
                LineContainsMarker(normalizedLine, BreakpointHitStartMarker) ||
                LineContainsMarker(normalizedLine, BreakpointHitEndMarker))
            {
                return true;
            }

            return false;
        }

        private static bool IsMarkerLine(string normalizedLine, string marker)
        {
            return string.Equals(normalizedLine, marker, StringComparison.Ordinal) ||
                   LineContainsMarker(normalizedLine, marker);
        }

        private static bool LineContainsMarker(string normalizedLine, string marker)
        {
            return !string.IsNullOrEmpty(normalizedLine) &&
                   !string.IsNullOrEmpty(marker) &&
                   string.Equals(normalizedLine, marker, StringComparison.Ordinal);
        }

        private static string RemoveMarker(string normalizedLine, string marker)
        {
            return string.IsNullOrEmpty(normalizedLine)
                ? string.Empty
                : normalizedLine.Replace(marker, string.Empty, StringComparison.Ordinal);
        }

        private void PublishOutputLine(string line, string publicationOrigin, string? streamSource)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            Trace("PublishOutputLine", $"Forwarding output; lineLength={line.Length}; classification={ClassifyLine(line)}");
            var isErrorStream = string.Equals(streamSource, "stderr", StringComparison.OrdinalIgnoreCase);
            PublishTypedEvent(
                isErrorStream ? DebuggerEventCategory.NativeStderr : DebuggerEventCategory.NativeStdout,
                isErrorStream ? DebuggerEventSeverity.Warning : DebuggerEventSeverity.Information,
                isErrorStream ? "NativeStderr" : "NativeStdout",
                line,
                details: $"publicationOrigin={publicationOrigin}; stream={streamSource ?? "unknown"}");
            RaiseOutputReceived(line + Environment.NewLine, publicationOrigin, streamSource);
        }

        private void HandleDebugPromptMarker(string streamSource)
        {
            Trace("HandleDebugPromptMarker", $"Entry; ignoreNext={_ignoreNextDebugPrompt}; suppressNextCount={_suppressNextDebugPromptCount}; {DescribeSessionState()}");

            var wasPaused = CurrentState == DebugSessionState.Paused;

            if (_ignoreNextDebugPrompt)
            {
                _ignoreNextDebugPrompt = false;
                SetCurrentState(DebugSessionState.Paused, "DebugPromptMarker", streamSource);
                AppLogger.Info("Debug", "DebugPausedDetected via prompt marker.");
                Trace("HandleDebugPromptMarker", $"Ignored-next prompt consumed without scheduling another helper query; wasPaused={wasPaused}; {DescribeSessionState()}");
                return;
            }

            if (_suppressNextDebugPromptCount > 0)
            {
                _suppressNextDebugPromptCount--;
                SetCurrentState(DebugSessionState.Paused, "DebugPromptMarker", streamSource);
                AppLogger.Info("Debug", "DebugPausedDetected via suppressed prompt marker.");
                Trace("HandleDebugPromptMarker", $"Suppressed helper prompt consumed without scheduling another helper query; wasPaused={wasPaused}; {DescribeSessionState()}");
                return;
            }

            SetCurrentState(DebugSessionState.Paused, "DebugPromptMarker", streamSource);
            AppLogger.Info("Debug", "DebugPausedDetected via prompt marker.");
            Trace("HandleDebugPromptMarker", $"Paused prompt observed; wasPaused={wasPaused}; {DescribeSessionState()}");
            QueueCurrentFrameQueryIfNoRecentLocation("prompt marker");
        }

        private void HandleBreakpointPayload(string payload)
        {
            Trace("HandleBreakpointPayload", $"Entry; payloadLength={payload.Length}; {DescribeSessionState()}");
            SetCurrentState(DebugSessionState.Paused, "LegacyBreakpointPayload", "stdout");
            AppLogger.Info("Debug", "DebugPausedDetected via breakpoint payload.");

            var location = DeserializeSingle<BreakpointLocation>(payload, "LegacyBreakpointPayload", requiresPayload: true, out var malformedPayload);
            if (malformedPayload || location is null || !HasResolvableLocation(location))
            {
                _ignoreNextDebugPrompt = false;
                Trace("HandleBreakpointPayload", $"Location unavailable; malformedPayload={malformedPayload}; {DescribeSessionState()}");
                return;
            }

            _ignoreNextDebugPrompt = true;
            MarkLocationNotificationObserved();
            Trace("HandleBreakpointPayload", $"Raising BreakpointHit; scriptPathPresent={!string.IsNullOrWhiteSpace(location.ScriptPath)}; lineNumber={location.LineNumber}; {DescribeSessionState()}");
            RaiseBreakpointHit(location.ScriptPath, location.LineNumber, "LegacyBreakpointPayload", "stdout");
        }

        private bool TryHandleObservedDebugPauseOutput(string line, string source)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmed = NormalizeDebuggerOutputLine(line);
            if (!string.Equals(trimmed, "Entering debug mode. Use h or ? for help.", StringComparison.OrdinalIgnoreCase) &&
                !DebugBreakpointOutputRegex.IsMatch(trimmed) &&
                !DebugLocationOutputRegex.IsMatch(trimmed) &&
                !trimmed.StartsWith("[DBG]:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var wasPaused = CurrentState == DebugSessionState.Paused;
            SetCurrentState(DebugSessionState.Paused, "ObservedDebugPauseOutput", source);
            AppLogger.Info("Debug", $"DebugPausedDetected via {source} output: {trimmed}");
            Trace("TryHandleObservedDebugPauseOutput", $"Matched pause output; source={source}; wasPaused={wasPaused}; classification={ClassifyLine(trimmed)}; {DescribeSessionState()}");

            // Native debugger pause/status lines are control information for the app,
            // not script output.  Consume them here so they do not appear in the user's
            // console transcript as noisy [debug] lines.
            if (TryParseDebugLocation(trimmed, out var scriptPath, out var lineNumber))
            {
                MarkLocationNotificationObserved();
                Trace("TryHandleObservedDebugPauseOutput", $"Raising BreakpointHit from parsed debug location; scriptPathPresent={!string.IsNullOrWhiteSpace(scriptPath)}; lineNumber={lineNumber}; {DescribeSessionState()}");
                RaiseBreakpointHit(scriptPath, lineNumber, "ObservedDebugPauseOutput", source);
            }

            return true;
        }

        private static bool TryParseDebugLocation(string line, out string? scriptPath, out int lineNumber)
        {
            scriptPath = null;
            lineNumber = 0;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var normalizedLine = NormalizeDebuggerOutputLine(line);
            var match = DebugLocationColonCaptureRegex.Match(normalizedLine);
            if (!match.Success)
            {
                match = DebugLocationWordCaptureRegex.Match(normalizedLine);
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

                var location = DeserializeSingle<BreakpointLocation>(payload, "CurrentFrameRequest", requiresPayload: true, out var malformedPayload);
                if (!malformedPayload && location is not null && HasResolvableLocation(location))
                {
                    MarkLocationNotificationObserved();
                    Trace("QueryCurrentFrameAndRaiseBreakpointAsync", $"Raising BreakpointHit; scriptPathPresent={!string.IsNullOrWhiteSpace(location.ScriptPath)}; lineNumber={location.LineNumber}; {DescribeSessionState()}");
                    RaiseBreakpointHit(location.ScriptPath, location.LineNumber, "CurrentFrameRequest", streamSource: null);
                }
            }
            catch (Exception ex)
            {
                Trace("QueryCurrentFrameAndRaiseBreakpointAsync", $"Failed; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeSessionState()}");
                RaiseOutputReceived($"Unable to query the paused debug location: {ex.Message}{Environment.NewLine}", "CurrentFrameQueryFailure", streamSource: null);
            }
        }

        private void HandleSessionEndedMarker()
        {
            Trace("HandleSessionEndedMarker", $"Entry; {DescribeSessionState()}");
            _normalCompletionObserved = true;
            FinalizeTermination("SessionEndedMarker", "stdout", teardownState: null);
        }

        private void HandleProcessExited(Process exitedProcess)
        {
            Trace("HandleProcessExited", $"Entry; exitedProcessId={TryGetProcessId(exitedProcess)}; hasExited={SafeHasExited(exitedProcess)}; exitCode={TryGetExitCode(exitedProcess)}; {DescribeSessionState()}");
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_process, exitedProcess))
                {
                    return;
                }

            }

            Trace("HandleProcessExited", $"Raising SessionEnded after process exit; exitedProcessId={TryGetProcessId(exitedProcess)}; {DescribeSessionState()}");
            FinalizeTermination("ConfirmedProcessExit", streamSource: null, teardownState: null, process: exitedProcess);
        }

        private IReadOnlyList<T> DeserializeList<T>(string payload, string requestSource)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<T>();
            }

            JsonException? collectionException = null;
            try
            {
                var list = JsonSerializer.Deserialize<List<T>>(payload, JsonOptions);
                if (list is not null)
                {
                    return list;
                }
            }
            catch (JsonException ex)
            {
                collectionException = ex;
            }

            try
            {
                var single = JsonSerializer.Deserialize<T>(payload, JsonOptions);
                return single is null ? Array.Empty<T>() : new[] { single };
            }
            catch (JsonException ex)
            {
                ReportProtocolParseFailure(
                    payload,
                    requestSource,
                    typeof(T),
                    ex,
                    new Dictionary<string, object?>
                    {
                        ["collectionParseFailed"] = collectionException is not null,
                        ["collectionExceptionType"] = collectionException?.GetType().Name
                    });
                return Array.Empty<T>();
            }
        }

        private T? DeserializeSingle<T>(string payload, string requestSource, bool requiresPayload, out bool malformedPayload) where T : class
        {
            malformedPayload = false;
            if (string.IsNullOrWhiteSpace(payload))
            {
                if (requiresPayload)
                {
                    malformedPayload = true;
                    ReportProtocolParseFailure(
                        payload,
                        requestSource,
                        typeof(T),
                        new InvalidDataException("Debugger protocol response did not contain the required JSON payload."),
                        additionalProperties: null);
                }

                return null;
            }

            try
            {
                var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
                if (result is null && requiresPayload)
                {
                    malformedPayload = true;
                    ReportProtocolParseFailure(
                        payload,
                        requestSource,
                        typeof(T),
                        new InvalidDataException("Debugger protocol response contained a null JSON payload."),
                        additionalProperties: null);
                }

                return result;
            }
            catch (JsonException ex)
            {
                malformedPayload = true;
                ReportProtocolParseFailure(payload, requestSource, typeof(T), ex, additionalProperties: null);
                return null;
            }
        }

        private void SetCurrentState(DebugSessionState newState, string publicationOrigin = "StateTransition", string? streamSource = null)
        {
            if (CurrentState == newState)
            {
                Trace("SetCurrentState", $"No-op; state remains {newState}; {DescribeSessionState()}");
                return;
            }

            var oldState = CurrentState;
            CurrentState = newState;
            if (newState == DebugSessionState.Paused)
            {
                Interlocked.Increment(ref _pauseGeneration);
                CurrentPauseReason = MapPauseReason(publicationOrigin);
            }
            AppLogger.Info("Debug", $"DebugStateChanged: {newState}");
            DeveloperDiagnostics.LogStateTransition("Debugger", "DebugSessionStateChanged", oldState.ToString(), newState.ToString(), "PsesDebugSession state changed.", new Dictionary<string, object?> { ["processId"] = TryGetProcessId(_process) });
            PublishTypedEvent(
                DebuggerEventCategory.DebuggerLifecycle,
                newState == DebugSessionState.Stopped ? DebuggerEventSeverity.Information : DebuggerEventSeverity.Information,
                "DebuggerLifecycle",
                $"Debugger state changed: {oldState} -> {newState}.",
                details: $"publicationOrigin={publicationOrigin}; stream={streamSource ?? "none"}",
                terminationReason: newState == DebugSessionState.Stopped ? MapTerminationReason(publicationOrigin) : null,
                pauseReason: newState == DebugSessionState.Paused ? MapPauseReason(publicationOrigin) : null);
            Trace("SetCurrentState", $"Transition; oldState={oldState}; newState={newState}; processId={TryGetProcessId(_process)}; sessionEndedRaised={_sessionEndedRaised}");
            Trace("SetCurrentState", $"Raising StateChanged; newState={newState}; processId={TryGetProcessId(_process)}");
            RaiseStateChanged(newState, publicationOrigin, streamSource);
        }

        private void RaiseBreakpointHit(string? scriptPath, int lineNumber, string publicationOrigin, string? streamSource)
        {
            PublishTypedEvent(
                DebuggerEventCategory.Breakpoint,
                DebuggerEventSeverity.Information,
                "Breakpoint",
                lineNumber > 0 ? $"Breakpoint hit at line {lineNumber}." : "Breakpoint hit.",
                filePath: scriptPath,
                lineNumber: lineNumber,
                isNavigable: !string.IsNullOrWhiteSpace(scriptPath) && lineNumber > 0,
                pauseReason: DebuggerPauseReason.Breakpoint,
                details: $"publicationOrigin={publicationOrigin}; stream={streamSource ?? "none"}");
            Action<string?, int>? subscribers;
            int subscriberCount;
            lock (_syncRoot)
            {
                subscribers = _breakpointHit;
                subscriberCount = _breakpointHitSubscriberCount;
            }

            InvokeEventSafely(nameof(BreakpointHit), subscribers, () => subscribers?.Invoke(scriptPath, lineNumber), publicationOrigin, streamSource, subscriberCount);
        }

        private void RaiseStateChanged(DebugSessionState state, string publicationOrigin, string? streamSource)
        {
            Action<DebugSessionState>? subscribers;
            int subscriberCount;
            lock (_syncRoot)
            {
                subscribers = _stateChanged;
                subscriberCount = _stateChangedSubscriberCount;
            }

            InvokeEventSafely(nameof(StateChanged), subscribers, () => subscribers?.Invoke(state), publicationOrigin, streamSource, subscriberCount);
        }

        private void RaiseSessionEnded(string publicationOrigin, string? streamSource)
        {
            PublishTypedEvent(
                DebuggerEventCategory.DebuggerLifecycle,
                DebuggerEventSeverity.Information,
                "DebuggerLifecycle",
                "Debugger session ended.",
                details: $"publicationOrigin={publicationOrigin}; stream={streamSource ?? "none"}",
                terminationReason: MapTerminationReason(publicationOrigin));
            Action? subscribers;
            int subscriberCount;
            lock (_syncRoot)
            {
                subscribers = _sessionEnded;
                subscriberCount = _sessionEndedSubscriberCount;
            }

            InvokeEventSafely(nameof(SessionEnded), subscribers, () => subscribers?.Invoke(), publicationOrigin, streamSource, subscriberCount);
        }

        private void FinalizeTermination(
            string publicationOrigin,
            string? streamSource,
            DebugProcessTeardownState? teardownState,
            Process? process = null)
        {
            DebugTerminationInfo terminationInfo;
            lock (_syncRoot)
            {
                if (_sessionEndedRaised)
                {
                    return;
                }

                _sessionEndedRaised = true;
                var selectedReason = DebugTerminationPolicy.Select(
                    _userStopRequested,
                    _exceptionInfo is not null,
                    _startupFailure,
                    _transportFailure,
                    protocolFailed: false,
                    _normalCompletionObserved,
                    processExitedUnexpectedly: string.Equals(publicationOrigin, "ConfirmedProcessExit", StringComparison.Ordinal));
                terminationInfo = new DebugTerminationInfo(
                    SessionId,
                    DateTimeOffset.UtcNow,
                    selectedReason,
                    BuildTerminationMessage(selectedReason),
                    TryGetExitCodeAsInt(process ?? _process),
                    TryGetProcessId(process ?? _process),
                    _userStopRequested,
                    selectedReason is DebugTerminationReason.NormalCompletion or DebugTerminationReason.UserStop,
                    $"publicationOrigin={publicationOrigin}; stream={streamSource ?? "none"}; teardown={(teardownState is not null)}",
                    _exceptionInfo);
                _terminationInfo = terminationInfo;
            }

            SetCurrentState(DebugSessionState.Stopped, publicationOrigin, streamSource);
            Trace("FinalizeTermination", $"Finalized exactly once; reason={terminationInfo.Reason}; processId={terminationInfo.ProcessId}; exitCode={terminationInfo.ExitCode}; {DescribeSessionState()}");
            DeveloperDiagnostics.LogStateTransition("Debugger", "DebuggerTermination", "Active", terminationInfo.Reason.ToString(), "Structured debugger termination finalized.", new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["terminationReason"] = terminationInfo.Reason.ToString(),
                ["exitCode"] = terminationInfo.ExitCode,
                ["processId"] = terminationInfo.ProcessId,
                ["wasUserRequested"] = terminationInfo.WasUserRequested,
                ["hasException"] = terminationInfo.Exception is not null,
                ["publicationOrigin"] = publicationOrigin
            });
            RaiseTerminated(terminationInfo, publicationOrigin, streamSource);
            RaiseSessionEnded(publicationOrigin, streamSource);
        }

        private void RaiseTerminated(DebugTerminationInfo terminationInfo, string publicationOrigin, string? streamSource)
        {
            Action<DebugTerminationInfo>? subscribers;
            int subscriberCount;
            lock (_syncRoot)
            {
                subscribers = _terminated;
                subscriberCount = _terminatedSubscriberCount;
            }

            InvokeEventSafely(nameof(Terminated), subscribers, () => subscribers?.Invoke(terminationInfo), publicationOrigin, streamSource, subscriberCount);
        }

        private static string BuildTerminationMessage(DebugTerminationReason reason)
            => reason switch
            {
                DebugTerminationReason.NormalCompletion => "Debugger session completed normally.",
                DebugTerminationReason.UserStop => "Debugger session stopped by the user.",
                DebugTerminationReason.TerminatingException => "Debugger session terminated by an unhandled PowerShell exception.",
                DebugTerminationReason.StartupFailure => "Debugger session failed during startup.",
                DebugTerminationReason.TransportFailure => "Debugger session ended after a debugger transport failure.",
                DebugTerminationReason.ChildProcessExit => "Debugger child process exited unexpectedly.",
                DebugTerminationReason.ProtocolFailure => "Debugger session ended after a protocol failure.",
                DebugTerminationReason.Cancellation => "Debugger session was cancelled.",
                _ => "Debugger session ended for an unknown reason."
            };

        private void RaiseOutputReceived(string output, string publicationOrigin, string? streamSource)
        {
            Action<string>? subscribers;
            int subscriberCount;
            lock (_syncRoot)
            {
                subscribers = _outputReceived;
                subscriberCount = _outputReceivedSubscriberCount;
            }

            InvokeEventSafely(nameof(OutputReceived), subscribers, () => subscribers?.Invoke(output), publicationOrigin, streamSource, subscriberCount);
        }

        private void PublishTypedEvent(
            DebuggerEventCategory category,
            DebuggerEventSeverity severity,
            string source,
            string displayText,
            string? filePath = null,
            int? lineNumber = null,
            string? details = null,
            bool isNavigable = false,
            bool isExpandable = false,
            DebuggerPauseReason? pauseReason = null,
            DebuggerTerminationReason? terminationReason = null)
        {
            var typedEvent = new DebuggerEvent(
                SessionId,
                DateTimeOffset.UtcNow,
                Interlocked.Increment(ref _eventSequence),
                category,
                severity,
                source,
                displayText,
                filePath,
                lineNumber,
                details,
                isNavigable,
                isExpandable,
                pauseReason,
                terminationReason,
                TryGetProcessId(_process));

            Action<DebuggerEvent>? subscribers;
            int subscriberCount;
            lock (_syncRoot)
            {
                subscribers = _typedEventReceived;
                subscriberCount = _typedEventReceivedSubscriberCount;
            }

            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Typed debugger event published.",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = SessionId,
                    ["processId"] = typedEvent.ProcessId,
                    ["sequence"] = typedEvent.Sequence,
                    ["category"] = typedEvent.Category.ToString(),
                    ["severity"] = typedEvent.Severity.ToString(),
                    ["source"] = typedEvent.Source,
                    ["displayTextLength"] = typedEvent.DisplayText.Length,
                    ["detailsLength"] = typedEvent.Details?.Length ?? 0,
                    ["isNavigable"] = typedEvent.IsNavigable,
                    ["lineNumber"] = typedEvent.LineNumber,
                    ["subscriberCount"] = subscriberCount
                });

            if (subscribers is null)
            {
                return;
            }

            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    ((Action<DebuggerEvent>)subscriber)(typedEvent);
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException(
                        "Debugger",
                        ex,
                        "Typed debugger event subscriber failed; other subscribers remain active.",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = SessionId,
                            ["sequence"] = typedEvent.Sequence,
                            ["category"] = typedEvent.Category.ToString(),
                            ["subscriberCount"] = subscriberCount
                        });
                }
            }
        }

        private static DebuggerPauseReason MapPauseReason(string publicationOrigin)
            => publicationOrigin.Contains("Breakpoint", StringComparison.OrdinalIgnoreCase)
                ? DebuggerPauseReason.Breakpoint
                : publicationOrigin.Contains("Step", StringComparison.OrdinalIgnoreCase)
                    ? DebuggerPauseReason.Step
                    : publicationOrigin.Contains("Prompt", StringComparison.OrdinalIgnoreCase)
                        ? DebuggerPauseReason.Prompt
                        : DebuggerPauseReason.Unknown;

        private static DebuggerTerminationReason MapTerminationReason(string publicationOrigin)
            => publicationOrigin.Contains("SessionEndedMarker", StringComparison.OrdinalIgnoreCase)
                ? DebuggerTerminationReason.NormalCompletion
                : publicationOrigin.Contains("ProcessExit", StringComparison.OrdinalIgnoreCase)
                    ? DebuggerTerminationReason.ChildProcessExit
                    : publicationOrigin.Contains("Teardown", StringComparison.OrdinalIgnoreCase)
                        ? DebuggerTerminationReason.UserStop
                        : DebuggerTerminationReason.Unknown;

        private void InvokeEventSafely(string eventName, Delegate? subscribers, Action invoke, string publicationOrigin, string? streamSource, int subscriberCount)
        {
            if (subscribers is null)
            {
                return;
            }

            try
            {
                invoke();
            }
            catch (Exception ex)
            {
                var properties = new Dictionary<string, object?>
                {
                    ["eventName"] = eventName,
                    ["publicationOrigin"] = publicationOrigin,
                    ["streamSource"] = streamSource,
                    ["debuggerState"] = CurrentState.ToString(),
                    ["processId"] = TryGetProcessId(_process),
                    ["subscriberCount"] = subscriberCount
                };
                AppLogger.Error("Debugger", $"Debugger {eventName} subscriber failed. Origin={publicationOrigin}; Stream={streamSource ?? "(none)"}; State={CurrentState}; ProcessId={TryGetProcessId(_process)}; Subscribers={subscriberCount}.", ex);
                DeveloperDiagnostics.LogException("Debugger", ex, $"Debugger {eventName} subscriber failed.", properties);
            }
        }

        private void AddSubscriber<TDelegate>(ref TDelegate? subscribers, ref int subscriberCount, TDelegate? subscriber)
            where TDelegate : Delegate
        {
            if (subscriber is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                subscribers = (TDelegate?)Delegate.Combine(subscribers, subscriber);
                subscriberCount++;
            }
        }

        private void RemoveSubscriber<TDelegate>(ref TDelegate? subscribers, ref int subscriberCount, TDelegate? subscriber)
            where TDelegate : Delegate
        {
            if (subscriber is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                var updatedSubscribers = (TDelegate?)Delegate.Remove(subscribers, subscriber);
                if (!Equals(updatedSubscribers, subscribers))
                {
                    subscribers = updatedSubscribers;
                    subscriberCount--;
                }
            }
        }

        private void ReportUnexpectedReaderFailure(Process owningProcess, bool isErrorStream, string phase, Exception exception)
        {
            _transportFailure = true;
            var streamSource = isErrorStream ? "stderr" : "stdout";
            var properties = new Dictionary<string, object?>
            {
                ["readerStream"] = streamSource,
                ["readerPhase"] = phase,
                ["debuggerState"] = CurrentState.ToString(),
                ["processId"] = TryGetProcessId(owningProcess),
                ["processHasExited"] = SafeHasExited(owningProcess)
            };
            AppLogger.Error("Debugger", $"Debugger {streamSource} reader failed unexpectedly during {phase}.", exception);
            DeveloperDiagnostics.LogException("Debugger", exception, "Debugger transport reader failed unexpectedly; bounded teardown was initiated.", properties);
            _ = StopInternalAsync();
        }

        private void ReportProtocolParseFailure(string payload, string requestSource, Type parserTargetType, Exception exception, IReadOnlyDictionary<string, object?>? additionalProperties)
        {
            var properties = new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(payload))
            {
                ["requestSource"] = requestSource,
                ["parserTargetType"] = parserTargetType.Name,
                ["debuggerState"] = CurrentState.ToString(),
                ["processId"] = TryGetProcessId(_process)
            };

            if (additionalProperties is not null)
            {
                foreach (var property in additionalProperties)
                {
                    properties[property.Key] = property.Value;
                }
            }

            AppLogger.Error("Debugger", $"Debugger protocol payload could not be parsed. Source={requestSource}; Target={parserTargetType.Name}; State={CurrentState}; ProcessId={TryGetProcessId(_process)}.", exception);
            DeveloperDiagnostics.LogException("Debugger", exception, "Debugger protocol payload could not be parsed.", properties);
        }

        private static bool HasResolvableLocation(BreakpointLocation? location)
            => location is not null && !string.IsNullOrWhiteSpace(location.ScriptPath) && location.LineNumber > 0;

        private void Trace(string source, string message)
        {
            DebuggerTraceLogger.Write($"PsesDebugSession.{source}", message);
        }

        private string DescribeSessionState()
        {
            return $"currentState={CurrentState}; disposed={_disposed}; sessionEndedRaised={_sessionEndedRaised}; processNull={(_process is null)}; processId={TryGetProcessId(_process)}; hasExited={SafeHasExited(_process)}; activeRequest={(_activeRequest is not null)}; capturingBreakpointPayload={_capturingBreakpointPayload}; currentFrameQueryInProgress={Volatile.Read(ref _currentFrameQueryInProgress)}; ignoreNextPrompt={_ignoreNextDebugPrompt}; suppressNextPromptCount={_suppressNextDebugPromptCount}; orphanedRequestEndMarker={_orphanedRequestOutputEndMarker ?? "(none)"}";
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

        private static int? TryGetExitCodeAsInt(Process? process)
        {
            if (process is null)
            {
                return null;
            }

            try
            {
                return process.HasExited ? process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeDebuggerOutputLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return string.Empty;
            }

            var normalized = OscControlSequenceRegex.Replace(line, string.Empty);
            normalized = AnsiControlSequenceRegex.Replace(normalized, string.Empty);
            return normalized.Trim();
        }

        private static string ClassifyLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return "Empty";
            }

            var normalizedLine = NormalizeDebuggerOutputLine(line);

            if (string.Equals(normalizedLine, ReadyMarker, StringComparison.Ordinal))
            {
                return "ReadyMarker";
            }

            if (string.Equals(normalizedLine, SessionEndedMarker, StringComparison.Ordinal))
            {
                return "SessionEndedMarker";
            }

            if (normalizedLine.StartsWith(DebugExceptionEnvelope.Marker, StringComparison.Ordinal))
            {
                return "StructuredExceptionEnvelope";
            }

            if (string.Equals(normalizedLine, DebugPromptMarker, StringComparison.Ordinal))
            {
                return "DebugPromptMarker";
            }

            if (string.Equals(normalizedLine, BreakpointHitStartMarker, StringComparison.Ordinal))
            {
                return "BreakpointHitStartMarker";
            }

            if (string.Equals(normalizedLine, BreakpointHitEndMarker, StringComparison.Ordinal))
            {
                return "BreakpointHitEndMarker";
            }

            var trimmed = normalizedLine;
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
            builder.AppendLine("    catch {");
            builder.AppendLine("        $errorRecord = $_");
            builder.AppendLine("        $invocation = $errorRecord.InvocationInfo");
            builder.AppendLine("        $exception = [pscustomobject]@{");
            builder.AppendLine("            ExceptionType = if ($null -ne $errorRecord.Exception) { [string]$errorRecord.Exception.GetType().FullName } else { 'System.Management.Automation.RuntimeException' }");
            builder.AppendLine("            Message = [string]$errorRecord.Exception.Message");
            builder.AppendLine("            FullyQualifiedErrorId = [string]$errorRecord.FullyQualifiedErrorId");
            builder.AppendLine("            Category = [string]$errorRecord.CategoryInfo.Category");
            builder.AppendLine("            IsTerminating = $true");
            builder.AppendLine("            IsHandled = $false");
            builder.AppendLine("            ScriptPath = if ($null -ne $invocation) { [string]$invocation.ScriptName } else { '' }");
            builder.AppendLine("            LineNumber = if ($null -ne $invocation) { [int]$invocation.ScriptLineNumber } else { 0 }");
            builder.AppendLine("            ColumnNumber = if ($null -ne $invocation) { [int]$invocation.OffsetInLine } else { 0 }");
            builder.AppendLine("            InvocationName = if ($null -ne $invocation) { [string]$invocation.InvocationName } else { '' }");
            builder.AppendLine("            PositionText = [string]$errorRecord.InvocationInfo.PositionMessage");
            builder.AppendLine("            StackSummary = [string]$errorRecord.ScriptStackTrace");
            builder.AppendLine("            InnerDetails = ''");
            builder.AppendLine("        }");
            builder.AppendLine("        $json = $exception | ConvertTo-Json -Compress -Depth 4");
            builder.AppendLine("        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))");
            builder.AppendLine($"        [Console]::Out.WriteLine('{DebugExceptionEnvelope.Marker}' + $encoded)");
            builder.AppendLine("        [Console]::Out.Flush()");
            builder.AppendLine("        throw");
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
            builder.AppendLine("$payload = if ($null -ne $__pssInvocation) { [pscustomobject]@{ ScriptPath = [string]$__pssInvocation.ScriptName; LineNumber = [int]$__pssInvocation.ScriptLineNumber } } elseif ($null -ne $__pssFrame) { [pscustomobject]@{ ScriptPath = [string]$__pssFrame.ScriptName; LineNumber = [int]$__pssFrame.ScriptLineNumber } } else { [pscustomobject]@{ ScriptPath = ''; LineNumber = 0 } }");
            builder.AppendLine("$json = $payload | ConvertTo-Json -Compress -Depth 4");
            builder.Append("[Console]::Out.WriteLine('").Append(CurrentFrameStartMarker).AppendLine("')");
            builder.AppendLine("[Console]::Out.WriteLine($json)");
            builder.Append("[Console]::Out.WriteLine('").Append(CurrentFrameEndMarker).AppendLine("')");
            builder.AppendLine("[Console]::Out.Flush()");
            return builder.ToString();
        }

        private static string BuildVariablesRequestScript()
        {
            var builder = new StringBuilder();
            builder.AppendLine("function __PSS_FormatDebugValueText {");
            builder.AppendLine("    param($Value)");
            builder.AppendLine("    try {");
            builder.AppendLine("        if ($null -eq $Value) { return '' }");
            builder.AppendLine("        if ($Value -is [string]) { return $Value }");
            builder.AppendLine("        if ($Value -is [char]) { return [string]$Value }");
            builder.AppendLine("        if ($Value -is [bool]) { return [string]$Value }");
            builder.AppendLine("        if ($Value -is [byte] -or $Value -is [sbyte] -or $Value -is [int16] -or $Value -is [uint16] -or $Value -is [int] -or $Value -is [uint32] -or $Value -is [long] -or $Value -is [uint64] -or $Value -is [single] -or $Value -is [double] -or $Value -is [decimal]) { return [string]$Value }");
            builder.AppendLine("        if ($Value -is [datetime]) { return $Value.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) }");
            builder.AppendLine("        if ($Value -is [guid]) { return [string]$Value }");
            builder.AppendLine("        if ($Value -is [System.Collections.IDictionary]) { return ('Dictionary Count=' + $Value.Count) }");
            builder.AppendLine("        if ($Value -is [System.Collections.ICollection]) { return ('Collection Count=' + $Value.Count + ' Type=' + $Value.GetType().Name) }");
            builder.AppendLine("        if ($Value -is [System.Collections.IEnumerable]) { return ('Enumerable Type=' + $Value.GetType().Name) }");
            builder.AppendLine("        $text = [string]$Value");
            builder.AppendLine("        if ([string]::IsNullOrWhiteSpace($text)) { return ('<' + $Value.GetType().Name + '>') }");
            builder.AppendLine("        return $text");
            builder.AppendLine("    } catch { return '<unavailable>' }");
            builder.AppendLine("}");
            builder.AppendLine("$items = @(Get-Variable | Sort-Object Name | ForEach-Object {");
            builder.AppendLine("    $value = $_.Value");
            builder.AppendLine("    $typeName = if ($null -eq $value) { 'null' } else { $value.GetType().Name }");
            builder.AppendLine("    $valueText = __PSS_FormatDebugValueText $value");
            builder.AppendLine("    if ($null -eq $valueText) { $valueText = '' }");
            builder.AppendLine("    $valueText = [string]$valueText");
            builder.AppendLine("    if ($valueText.Length -gt 500) { $valueText = $valueText.Substring(0, 500) + '...' }");
            builder.AppendLine("    [pscustomobject]@{ Name = [string]$_.Name; Type = [string]$typeName; Value = [string]$valueText }");
            builder.AppendLine("})");
            builder.AppendLine("$json = $items | ConvertTo-Json -Compress -Depth 5");
            builder.Append("[Console]::Out.WriteLine('").Append(VariablesStartMarker).AppendLine("')");
            builder.AppendLine("[Console]::Out.WriteLine($json)");
            builder.Append("[Console]::Out.WriteLine('").Append(VariablesEndMarker).AppendLine("')");
            builder.AppendLine("[Console]::Out.Flush()");
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
            builder.AppendLine("        InvocationName = [string]$_.InvocationInfo.MyCommand.Name");
            builder.AppendLine("    }");
            builder.AppendLine("})");
            builder.AppendLine("$json = $items | ConvertTo-Json -Compress -Depth 5");
            builder.Append("[Console]::Out.WriteLine('").Append(CallStackStartMarker).AppendLine("')");
            builder.AppendLine("[Console]::Out.WriteLine($json)");
            builder.Append("[Console]::Out.WriteLine('").Append(CallStackEndMarker).AppendLine("')");
            builder.AppendLine("[Console]::Out.Flush()");
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

        private sealed record DebugProcessTeardownState(
            Process? Process,
            CancellationTokenSource? CancellationTokenSource,
            StreamWriter? StandardInput,
            Task? StandardOutputReaderTask,
            Task? StandardErrorReaderTask,
            bool RaiseSessionEnded,
            bool UserStopRequested,
            bool StartupFailure,
            bool TransportFailure,
            bool NormalCompletionObserved,
            DebugExceptionInfo? ExceptionInfo);

        private sealed class BreakpointLocation
        {
            public string? ScriptPath { get; set; }
            public int LineNumber { get; set; }
        }

        private enum ReaderTerminationReason
        {
            EndOfStream,
            Cancellation,
            Disposed,
            UnexpectedFailure
        }
    }
}
