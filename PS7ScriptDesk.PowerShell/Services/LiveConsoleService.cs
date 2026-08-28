using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services
{
    /// <summary>
    /// Manages the single live ConPTY-backed PowerShell terminal session.
    ///
    /// ── xterm.js terminal architecture ────────────────────────────────────────
    /// ConPTY stdout is published via <see cref="RawOutputReceived"/> with ANSI/
    /// VT100 sequences intact (only null bytes and the exec-done sentinel are
    /// stripped). The Shell layer subscribes and forwards the raw data to the
    /// xterm.js <see cref="Controls.TerminalControl"/> via WebView2.
    ///
    /// Lifecycle events (session start/stop, process exit) are still delivered
    /// through the <c>onOutput</c> callback as <see cref="ExecutionOutputStreamKind.Lifecycle"/>
    /// records. The ViewModel logs routine lifecycle events and only shows user-
    /// actionable lifecycle failures/exits in the visible terminal.
    ///
    /// Input flows from xterm.js → TerminalControl.UserInput → ViewModel.WriteRawInputAsync
    /// → <see cref="WriteRawInputAsync"/> → <see cref="WriteTerminalInputAsync"/>.
    ///
    /// The per-dispatch exec-done sentinel is stripped before any data reaches
    /// xterm.js so it never appears in the visible terminal.
    /// ──────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class LiveConsoleService : ILiveConsoleService
    {
        private static readonly Regex AnsiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex OscRegex = new(@"\x1B\].*?(\x07|\x1B\\)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex PromptRegex = new(@"PS\s+(?<path>.+?)>", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex LegacySnapshotFileNamePattern = new(@"^\d{8}_\d{6}_\d{3}_.+\.ps1$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Sentinel prefix written to the terminal after every script dispatch.
        // A unique token is generated per dispatch so completion cannot be
        // confused with user output that happens to contain a static marker.
        private const string ExecStartTokenPrefix = "##PSSTUDIO_EXEC_START_";
        private const string ExecDoneTokenPrefix = "##PSSTUDIO_EXEC_DONE_";
        private const string LocationTokenPrefix = "##PSSTUDIO_LOCATION_";
        private const string TerminalSnapshotFilePrefix = "psstudio-terminal-";
        private const string ScriptSnapshotFilePrefix = "pss-";
        private const string DispatchSnapshotFilePrefix = "psd-";
        private const string DispatchInstructionFilePrefix = "psi-";
        private const string DispatchHelperFilePrefix = "psh-";
        // Interactive terminals submit Enter as carriage return (\r). Do not send CRLF into ConPTY/PSReadLine.
        private const string TerminalEnterSequence = "\r";
        private static readonly TimeSpan InterruptGracefulTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan InterruptInputWriteTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan InterruptRecoveryPollInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan InterruptLifecycleGateTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan NoVisibleOutputFeedbackDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ScriptStartConfirmationDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan CommandHealthPollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan InputDrainTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(2);
        private const int MaxPreStartBufferCharacters = 64 * 1024;
        private const string DispatchDiagnosticTokenPrefix = "##PSSTUDIO_DISPATCH_DIAG##";

        private readonly object _syncRoot = new();
        private readonly SemaphoreSlim _sessionLifecycleGate = new(1, 1);
        private readonly TerminalInputRouter _terminalInputRouter = new();
        private readonly bool _preferRedirectedTerminalSession;
        private bool _firstOutputLogged;
        private bool _firstAnsiOutputLogged;
        private int _rawOutputInfoLogCount;

        private Process? _process;
        private IntPtr _pseudoConsoleHandle = IntPtr.Zero;
        private IntPtr _inputWriterHandle = IntPtr.Zero;
        private IntPtr _outputReaderHandle = IntPtr.Zero;
        private StreamWriter? _terminalWriter;
        private CancellationTokenSource? _readerCancellationTokenSource;
        private Task? _stdoutReaderTask;
        private Task? _stderrReaderTask;
        private int _terminalColumns = 120;
        private int _terminalRows = 30;
        private bool _hostAttached = true;
        private bool _isCommandInProgress;
        private bool _currentCommandIsScript;
        private int _commandDispatchGeneration;
        // Tracks meaningful user/script output only. Internal dispatch echo, ANSI-only
        // chunks, and blank lines must not suppress the user-facing "no output"
        // warning because those can make a failed/blocked script look healthy.
        private bool _currentDispatchVisibleOutputSeen;
        private bool _currentDispatchStartConfirmed;
        private DateTime? _currentDispatchStartedUtc;
        private int? _handledTerminalExitProcessId;
        private string? _pendingStartToken;
        private string? _pendingCompletionToken;
        private string? _pendingLocationToken;
        private readonly Queue<string> _pendingSnapshotPaths = new();
        private readonly List<string> _pendingHiddenOutputFragments = new();
        private string _hiddenOutputBuffer = string.Empty;
        private bool _preStartBufferTruncated;
        private string? _lastPromptHeuristicDirectory;
        private int _terminalSessionGeneration;
        private bool _terminalSessionTeardownInProgress = true;
        private bool _redirectedTerminalTransportActive;
        private readonly ResizeFailureEpisode _resizeFailureEpisode = new();
        private Task _lastProcessExitTeardownTask = Task.CompletedTask;
        private Task _pendingNativeTeardownTask = Task.CompletedTask;

        public LiveConsoleService()
            : this(preferRedirectedTerminalSession: false)
        {
        }

        internal LiveConsoleService(bool preferRedirectedTerminalSession)
        {
            _preferRedirectedTerminalSession = preferRedirectedTerminalSession;
        }

        public bool IsSessionRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return IsProcessRunningNoThrow(_process);
                }
            }
        }

        public bool IsCommandInProgress
        {
            get
            {
                lock (_syncRoot)
                {
                    // A command cannot still be running after the owned PowerShell process exits.
                    // Treat this as idle even if a process-exit race prevented normal cleanup.
                    return _isCommandInProgress && IsProcessRunningNoThrow(_process);
                }
            }
        }

        // -------------------------------------------------------------------------
        // Events (ILiveConsoleService)
        // -------------------------------------------------------------------------

        /// <inheritdoc />
        public event Action? ScriptExecutionCompleted;

        /// <inheritdoc />
        public event Action? CommandExecutionCompleted;

        /// <inheritdoc />
        public event Action? SessionTerminated;

        /// <inheritdoc />
        public event Action<int>? TerminalSessionStarted;

        public event Action<int>? TerminalSessionStopping;

        public event Action<int, string>? RawOutputReceived;

        public bool IsHostAttached
        {
            get
            {
                lock (_syncRoot)
                {
                    return _hostAttached;
                }
            }
        }

        public PowerShellRuntimeInfo? ActiveRuntime { get; private set; }

        public string? CurrentWorkingDirectory { get; private set; }

        private static bool IsProcessRunningNoThrow(Process? process)
        {
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private int BeginTerminalSessionGeneration()
        {
            lock (_syncRoot)
            {
                _terminalSessionGeneration++;
                _terminalSessionTeardownInProgress = false;
                _resizeFailureEpisode.ResetForSession(_terminalSessionGeneration);
                _handledTerminalExitProcessId = null;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingLocationToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
                _preStartBufferTruncated = false;
                _lastPromptHeuristicDirectory = null;
                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = false;
                _currentDispatchStartedUtc = null;
                return _terminalSessionGeneration;
            }
        }

        private static string? TryGetMainWindowTitleNoThrow(Process? process)
        {
            if (process is null)
            {
                return null;
            }

            try
            {
                if (process.HasExited)
                {
                    return null;
                }

                process.Refresh();
                var title = process.MainWindowTitle;
                return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private int? GetCurrentProcessIdNoThrow()
        {
            lock (_syncRoot)
            {
                return TryGetProcessId(_process);
            }
        }

        public void AttachHost(IntPtr hostHandle, int width, int height)
        {
            lock (_syncRoot)
            {
                _hostAttached = true;
                UpdateTerminalSize(width, height);
            }
        }

        public void ResizeHost(int width, int height)
        {
            ResizeRequest resizeRequest;

            lock (_syncRoot)
            {
                UpdateTerminalSize(width, height);
                resizeRequest = new ResizeRequest(
                    _pseudoConsoleHandle,
                    _terminalSessionGeneration,
                    _terminalColumns,
                    _terminalRows,
                    _process);
            }

            if (resizeRequest.PseudoConsole != IntPtr.Zero)
            {
                var hResult = ResizePseudoConsole(
                    resizeRequest.PseudoConsole,
                    new COORD((short)resizeRequest.Columns, (short)resizeRequest.Rows));
                ObserveResizeResult("ResizeHost", resizeRequest, hResult);
            }
        }

        public void FocusConsole()
        {
            // Focus is handled by the WPF input box in the shell layer.
        }

        public async Task StartSessionAsync(
            PowerShellRuntimeInfo runtime,
            Action<ExecutionOutputRecord> onOutput,
            string? startupWorkingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            if (runtime is null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            if (onOutput is null)
            {
                throw new ArgumentNullException(nameof(onOutput));
            }

            await _sessionLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool shouldRestart;
                lock (_syncRoot)
                {
                    shouldRestart = !IsProcessRunningNoThrow(_process) ||
                                    ActiveRuntime is null ||
                                    !string.Equals(ActiveRuntime.ExecutablePath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }

                if (!shouldRestart)
                {
                    return;
                }

                var previousSessionStoppedCleanly = await StopConsoleCoreAsync(onOutput, "session-start").ConfigureAwait(false);
                if (!previousSessionStoppedCleanly && IsSessionRunning)
                {
                    throw new InvalidOperationException("The previous PowerShell terminal session did not stop cleanly.");
                }

                if (!previousSessionStoppedCleanly)
                {
                    AppLogger.Warning(
                        "LiveConsole",
                        "Starting a replacement PowerShell session after the previous owned process stopped, even though bounded ConPTY cleanup is still completing in the background.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                CleanupStaleExecutionSnapshots();
                var workingDirectory = NormalizeWorkingDirectory(startupWorkingDirectory);
                var sessionGeneration = BeginTerminalSessionGeneration();
                NotifyTerminalSessionStarted(sessionGeneration);

                if (_preferRedirectedTerminalSession)
                {
                    AppLogger.Info(
                        "LiveConsole",
                        $"Starting redirected terminal session because the host requested redirected mode. SessionGeneration={sessionGeneration}, DisplayPath='{runtime.ExecutablePath}', LaunchPath='{runtime.LaunchExecutablePath}', LaunchPathExists={File.Exists(runtime.LaunchExecutablePath)}, WorkingDirectory={workingDirectory}");
                    StartRedirectedSession(runtime, workingDirectory, onOutput, sessionGeneration);
                    AppLogger.Info("LiveConsole", $"Redirected terminal session started with {runtime.DisplayName}; SessionGeneration={sessionGeneration}, WorkingDirectory={workingDirectory}");
                    onOutput(new ExecutionOutputRecord(
                        ExecutionOutputStreamKind.Lifecycle,
                        $"Redirected terminal session started with {runtime.DisplayName}.",
                        DateTime.Now));
                }
                else
                {
                    try
                    {
                        AppLogger.Info(
                            "LiveConsole",
                            $"Starting terminal session. SessionGeneration={sessionGeneration}, DisplayPath='{runtime.ExecutablePath}', LaunchPath='{runtime.LaunchExecutablePath}', LaunchPathExists={File.Exists(runtime.LaunchExecutablePath)}, WorkingDirectory={workingDirectory}");
                        StartPseudoConsoleSession(runtime, workingDirectory, onOutput, sessionGeneration);
                        AppLogger.Info("LiveConsole", $"ConPTY terminal session started with {runtime.DisplayName}; SessionGeneration={sessionGeneration}, WorkingDirectory={workingDirectory}");
                        onOutput(new ExecutionOutputRecord(
                            ExecutionOutputStreamKind.Lifecycle,
                            $"ConPTY terminal session started with {runtime.DisplayName}.",
                            DateTime.Now));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("LiveConsole", $"ConPTY startup failed for {runtime.DisplayName}; falling back to redirected terminal mode. SessionGeneration={sessionGeneration}, Error={ex.Message}");
                        onOutput(new ExecutionOutputRecord(
                            ExecutionOutputStreamKind.Lifecycle,
                            $"ConPTY startup failed ({ex.Message}). Falling back to redirected terminal mode.",
                            DateTime.Now));

                        if (!await StopConsoleCoreAsync(onOutput, "conpty-startup-fallback").ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("The failed ConPTY session could not be torn down before fallback startup.", ex);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        sessionGeneration = BeginTerminalSessionGeneration();
                        NotifyTerminalSessionStarted(sessionGeneration);
                        try
                        {
                            StartRedirectedSession(runtime, workingDirectory, onOutput, sessionGeneration);
                        }
                        catch
                        {
                            await StopConsoleCoreAsync(onOutput, "redirected-startup-failure").ConfigureAwait(false);
                            throw;
                        }
                    }
                }

                lock (_syncRoot)
                {
                    if (_terminalSessionGeneration != sessionGeneration || _terminalSessionTeardownInProgress)
                    {
                        throw new InvalidOperationException("The PowerShell terminal session changed during startup.");
                    }

                    ActiveRuntime = runtime;
                    CurrentWorkingDirectory = workingDirectory;
                    _handledTerminalExitProcessId = null;
                }

                DeveloperDiagnostics.LogStateTransition(
                    "Terminal",
                    "TerminalSessionStarted",
                    "Stopped",
                    "Running",
                    "Terminal session generation became active.",
                    new Dictionary<string, object?>
                    {
                        ["sessionGeneration"] = sessionGeneration,
                        ["runtimePath"] = runtime.ExecutablePath,
                        ["workingDirectory"] = workingDirectory
                    });
            }
            finally
            {
                _sessionLifecycleGate.Release();
            }
        }

        private void NotifyTerminalSessionStarted(int sessionGeneration)
        {
            NotifyTerminalSessionLifecycleHandler(TerminalSessionStarted, "TerminalSessionStarted", sessionGeneration);
        }

        private void NotifyTerminalSessionStopping(int sessionGeneration)
        {
            NotifyTerminalSessionLifecycleHandler(TerminalSessionStopping, "TerminalSessionStopping", sessionGeneration);
        }

        private static void NotifyTerminalSessionLifecycleHandler(Action<int>? handler, string eventName, int sessionGeneration)
        {
            if (handler is null)
            {
                return;
            }

            try
            {
                handler(sessionGeneration);
            }
            catch (Exception ex)
            {
                AppLogger.Error("LiveConsole", $"Terminal lifecycle subscriber failed. Event={eventName}, SessionGeneration={sessionGeneration}, Error={ex.Message}");
                DeveloperDiagnostics.LogException("Terminal", ex, "Terminal lifecycle subscriber failed.", new Dictionary<string, object?>
                {
                    ["event"] = eventName,
                    ["sessionGeneration"] = sessionGeneration
                });
            }
        }

        public async Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(
            string commandText,
            Action<ExecutionOutputRecord> onOutput,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                throw new ArgumentException("A command is required.", nameof(commandText));
            }

            if (!IsSessionRunning)
            {
                throw new InvalidOperationException("The PowerShell terminal session is not running.");
            }

            var commandSnapshotPath = CreateExecutionSnapshot("Console command", commandText);
            var startToken = CreateStartToken();
            var completionToken = CreateCompletionToken();
            var locationToken = CreateLocationToken();
            var instructionSnapshotPath = CreateDispatchInstructionSnapshot(
                commandSnapshotPath,
                startToken,
                completionToken,
                locationToken,
                executeInCurrentScope: true);
            string helperSnapshotPath;
            try
            {
                helperSnapshotPath = CreateDispatchHelperSnapshot();
            }
            catch
            {
                TryDeleteSnapshot(commandSnapshotPath);
                TryDeleteSnapshot(instructionSnapshotPath);
                throw;
            }

            if (!TryBeginCommandDispatch(
                    isScript: false,
                    snapshotPath: commandSnapshotPath,
                    out var sessionGeneration,
                    out var dispatchFailure))
            {
                TryDeleteSnapshot(commandSnapshotPath);
                TryDeleteSnapshot(instructionSnapshotPath);
                TryDeleteSnapshot(helperSnapshotPath);
                throw new InvalidOperationException(dispatchFailure ?? "Another terminal operation is already running.");
            }

            var dispatchGeneration = GetCurrentCommandDispatchGeneration();
            var startedAt = DateTime.Now;
            AddPendingSnapshotPath(commandSnapshotPath);
            AddPendingSnapshotPath(instructionSnapshotPath);
            AddPendingSnapshotPath(helperSnapshotPath);
            SetPendingExecutionTokens(startToken, completionToken, locationToken);
            var dispatchCommand = BuildScriptDispatchCommand(
                helperSnapshotPath,
                instructionSnapshotPath,
                executeInCurrentScope: true);
            RegisterHiddenOutputFragment(dispatchCommand);
            AppLogger.Info(
                "LiveConsole",
                $"Sending editor command through the explicit terminal dispatch protocol. CommandLength={commandText.Length}, DispatchGeneration={dispatchGeneration}.");

            try
            {
                await WriteTerminalInputAsync(
                    dispatchCommand + TerminalEnterSequence,
                    sessionGeneration,
                    cancellationToken).ConfigureAwait(false);
                ScheduleNoVisibleOutputFeedback(dispatchGeneration, isScript: false, displayName: "console command", onOutput);
                ScheduleCommandHealthMonitor(dispatchGeneration, isScript: false, displayName: "console command", onOutput);

                return new LiveConsoleCommandResult(
                    "Console command",
                    wasStopped: false,
                    CurrentWorkingDirectory,
                    startedAt,
                    DateTime.Now);
            }
            catch
            {
                CancelPendingCommandDispatch(deleteSnapshot: true);
                throw;
            }
        }

        public async Task<LiveConsoleCommandResult> ExecuteScriptAsync(
            string documentDisplayName,
            string scriptContent,
            Action<ExecutionOutputRecord> onOutput,
            bool executeInCurrentScope = false,
            CancellationToken cancellationToken = default)
        {
            if (!IsSessionRunning)
            {
                throw new InvalidOperationException("The PowerShell terminal session is not running.");
            }

            var executionTarget = CreateExecutionTarget(documentDisplayName, scriptContent, executeInCurrentScope);
            var scriptSnapshotPath = executionTarget.Path;
            var startedAt = DateTime.Now;
            var startToken = CreateStartToken();
            var completionToken = CreateCompletionToken();
            var locationToken = CreateLocationToken();
            var instructionSnapshotPath = CreateDispatchInstructionSnapshot(
                scriptSnapshotPath,
                startToken,
                completionToken,
                locationToken,
                executeInCurrentScope);
            string helperSnapshotPath;
            try
            {
                helperSnapshotPath = CreateDispatchHelperSnapshot();
            }
            catch
            {
                TryDeleteSnapshot(instructionSnapshotPath);
                if (executionTarget.DeleteAfterRun)
                {
                    TryDeleteSnapshot(scriptSnapshotPath);
                }

                throw;
            }

            if (!TryBeginCommandDispatch(
                    isScript: true,
                    snapshotPath: executionTarget.DeleteAfterRun ? scriptSnapshotPath : null,
                    out var sessionGeneration,
                    out var dispatchFailure))
            {
                if (executionTarget.DeleteAfterRun)
                {
                    TryDeleteSnapshot(scriptSnapshotPath);
                }

                TryDeleteSnapshot(instructionSnapshotPath);
                TryDeleteSnapshot(helperSnapshotPath);
                throw new InvalidOperationException(dispatchFailure ?? "Another terminal operation is already running.");
            }

            var dispatchGeneration = GetCurrentCommandDispatchGeneration();
            var ownedProcessIdAtDispatch = GetCurrentProcessIdNoThrow();
            AddPendingSnapshotPath(instructionSnapshotPath);
            AddPendingSnapshotPath(helperSnapshotPath);
            SetPendingExecutionTokens(startToken, completionToken, locationToken);
            var dispatchCommand = BuildScriptDispatchCommand(
                helperSnapshotPath,
                instructionSnapshotPath,
                executeInCurrentScope);
            var scriptCommand = dispatchCommand + TerminalEnterSequence;
            RegisterHiddenOutputFragment(dispatchCommand);
            var scriptSnapshotExists = File.Exists(scriptSnapshotPath);
            var instructionSnapshotExists = File.Exists(instructionSnapshotPath);
            AppLogger.Info(
                "LiveConsole",
                $"Dispatching editor script to the live ConPTY terminal via preloaded session helper and instruction snapshot. ScriptPath={scriptSnapshotPath}, ScriptPathExists={scriptSnapshotExists}, DeleteScriptAfterRun={executionTarget.DeleteAfterRun}, InstructionSnapshotPath={instructionSnapshotPath}, InstructionSnapshotExists={instructionSnapshotExists}, ScriptLength={scriptContent?.Length ?? 0}, ExecuteInCurrentScope={executeInCurrentScope}, CommandLength={scriptCommand.Length}, EndsWithEnter={scriptCommand.EndsWith(TerminalEnterSequence, StringComparison.Ordinal)}, DispatchGeneration={dispatchGeneration}, OwnedProcessId={ownedProcessIdAtDispatch?.ToString() ?? "(none)"}.");
            DeveloperDiagnostics.LogInfo(
                "Execution",
                "Live terminal script dispatch prepared.",
                new Dictionary<string, object?>
                {
                    ["scriptPath"] = scriptSnapshotPath,
                    ["scriptPathExists"] = scriptSnapshotExists,
                    ["instructionSnapshotPath"] = instructionSnapshotPath,
                    ["instructionSnapshotExists"] = instructionSnapshotExists,
                    ["helperSnapshotExists"] = File.Exists(helperSnapshotPath),
                    ["deleteScriptAfterRun"] = executionTarget.DeleteAfterRun,
                    ["executeInCurrentScope"] = executeInCurrentScope,
                    ["dispatchGeneration"] = dispatchGeneration,
                    ["ownedProcessId"] = ownedProcessIdAtDispatch,
                    ["startTokenPrefix"] = ExecStartTokenPrefix,
                    ["completionTokenPrefix"] = ExecDoneTokenPrefix
                });
            AppLogger.Debug("LiveConsole", $"Dispatch command prepared. Length={scriptCommand.Length}, ContentOmitted=True.");
            PublishLifecycleMessage(
                onOutput,
                $"Running script '{GetDisplayNameForStatus(documentDisplayName)}'. Waiting for script output...");

            try
            {
                AppLogger.Debug("LiveConsole", $"Sending helper dispatch command to terminal stdin. ScriptSnapshotPath={scriptSnapshotPath}, InstructionSnapshotPath={instructionSnapshotPath}");
                await WriteTerminalInputAsync(scriptCommand, sessionGeneration, cancellationToken).ConfigureAwait(false);
                AppLogger.Info("LiveConsole", $"Script dispatch command written to terminal input. DispatchGeneration={dispatchGeneration}, OwnedProcessId={ownedProcessIdAtDispatch?.ToString() ?? "(none)"}.");
                DeveloperDiagnostics.LogInfo(
                    "Execution",
                    "Script dispatch command written to terminal input; scheduling no-output and health monitors.",
                    new Dictionary<string, object?>
                    {
                        ["dispatchGeneration"] = dispatchGeneration,
                        ["ownedProcessId"] = ownedProcessIdAtDispatch,
                        ["noVisibleOutputFeedbackDelayMs"] = NoVisibleOutputFeedbackDelay.TotalMilliseconds,
                        ["scriptStartConfirmationDelayMs"] = ScriptStartConfirmationDelay.TotalMilliseconds,
                        ["commandHealthPollIntervalMs"] = CommandHealthPollInterval.TotalMilliseconds
                    });
                ScheduleNoVisibleOutputFeedback(dispatchGeneration, isScript: true, displayName: documentDisplayName, onOutput);
                ScheduleCommandHealthMonitor(dispatchGeneration, isScript: true, displayName: documentDisplayName, onOutput);

                return new LiveConsoleCommandResult(
                    documentDisplayName,
                    wasStopped: false,
                    CurrentWorkingDirectory,
                    startedAt,
                    DateTime.Now);
            }
            catch
            {
                CancelPendingCommandDispatch(deleteSnapshot: true);
                throw;
            }
        }

        private static string QuotePowerShellSingleQuotedString(string value)
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        private static string BuildScriptDispatchCommand(
            string helperSnapshotPath,
            string instructionSnapshotPath,
            bool executeInCurrentScope)
        {
            var quotedHelperPath = QuotePowerShellSingleQuotedString(helperSnapshotPath);
            var quotedInstructionPath = QuotePowerShellSingleQuotedString(instructionSnapshotPath);

            // Each dispatch invokes a unique app-owned helper snapshot. User code can
            // remove or replace global variables and functions without disabling later
            // editor runs. Tokens remain in the instruction file and are never typed
            // into PSReadLine or exposed in the command echo.
            var invocationOperator = executeInCurrentScope ? "." : "&";
            return $"{invocationOperator} {quotedHelperPath} {quotedInstructionPath}";
        }

        private static string BuildInteractivePowerShellArguments(string startupCommand)
        {
            // PowerShell ISE-style script hosts should run the interactive session in
            // STA on Windows. WinForms/WPF scripts commonly require STA and often
            // self-relaunch into a separate pwsh.exe when they detect MTA. That
            // separate child process is outside the embedded terminal's lifecycle, so
            // crashes can look like a frozen/silent script. Starting the hosted
            // terminal as STA keeps GUI scripts inside the process PS7 ScriptDesk
            // owns and monitors.
            return "-NoLogo -NoExit -STA -ExecutionPolicy Bypass -Command " + QuoteCommandArgument(startupCommand);
        }

        private static string BuildTerminalStartupCommand()
        {
            return "try { Set-PSReadLineOption -PredictionSource None -ErrorAction SilentlyContinue } catch { }";
        }

        private static string CreateStartToken()
        {
            return ExecStartTokenPrefix + Guid.NewGuid().ToString("N");
        }

        private static string CreateCompletionToken()
        {
            return ExecDoneTokenPrefix + Guid.NewGuid().ToString("N");
        }

        private static string CreateLocationToken()
        {
            return LocationTokenPrefix + Guid.NewGuid().ToString("N") + "_";
        }

        private void SetPendingExecutionTokens(
            string? startToken,
            string? completionToken,
            string? locationToken)
        {
            lock (_syncRoot)
            {
                _pendingStartToken = startToken;
                _pendingCompletionToken = completionToken;
                _pendingLocationToken = locationToken;
            }
        }

        public async Task SendInterruptAsync()
        {
            // Send Ctrl+C (ETX = 0x03) to the ConPTY process.  This is the standard way
            // to interrupt a running command in an interactive terminal without killing the
            // whole session.  If the session is not running we fall back to a no-op — the
            // caller should handle the case where there is nothing to interrupt.
            if (!TryGetWritableSessionGeneration(out var sessionGeneration))
            {
                return;
            }

            await WriteTerminalInputAsync("\x03", sessionGeneration, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(
            Action<ExecutionOutputRecord>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            var operationId = $"ConsoleInterrupt-{Guid.NewGuid():N}";
            using var scope = DeveloperDiagnostics.BeginTimedOperation(
                "Terminal",
                "InterruptOrRestart",
                "Interrupt or restart requested for the live PowerShell session.",
                operationId: operationId);

            Process? process;
            PowerShellRuntimeInfo? runtime;
            string? workingDirectory;
            bool commandInProgress;
            bool hasPseudoConsole;
            bool hostAttached;
            int sessionGeneration;

            lock (_syncRoot)
            {
                process = _process;
                runtime = ActiveRuntime;
                workingDirectory = CurrentWorkingDirectory;
                commandInProgress = _isCommandInProgress;
                hasPseudoConsole = _pseudoConsoleHandle != IntPtr.Zero;
                hostAttached = _hostAttached;
                sessionGeneration = _terminalSessionGeneration;
            }

            var ownedProcessId = TryGetProcessId(process);
            AppLogger.Info(
                "LiveConsole",
                $"Interrupt requested. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, SessionRunning={process is not null && !process.HasExited}, CommandInProgress={commandInProgress}, HasPseudoConsole={hasPseudoConsole}, HostAttached={hostAttached}, Runtime='{runtime?.DisplayName ?? "(none)"}', WorkingDirectory='{workingDirectory ?? "(none)"}'.");
            DeveloperDiagnostics.LogUserAction(
                "Terminal",
                "InterruptRequested",
                "Interrupt requested for the live PowerShell session.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["ownedProcessId"] = ownedProcessId,
                    ["sessionRunning"] = process is not null && !process.HasExited,
                    ["commandInProgress"] = commandInProgress,
                    ["hasPseudoConsole"] = hasPseudoConsole,
                    ["hostAttached"] = hostAttached,
                    ["runtimePath"] = runtime?.ExecutablePath,
                    ["workingDirectory"] = workingDirectory
                });

            if (process is null || process.HasExited)
            {
                return new LiveConsoleInterruptResult(
                    interruptAttempted: false,
                    completedGracefully: false,
                    escalationRequired: false,
                    processTerminationSucceeded: false,
                    sessionRestarted: false,
                    ownedProcessId,
                    InterruptGracefulTimeout);
            }

            if (!commandInProgress)
            {
                AppLogger.Info("LiveConsole", $"Interrupt request ignored because no tracked command was running. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "InterruptRequested",
                    "Interrupt request was ignored because no tracked command was active.",
                    "IgnoredNoTrackedCommand",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["ownedProcessId"] = ownedProcessId
                    });

                return new LiveConsoleInterruptResult(
                    interruptAttempted: false,
                    completedGracefully: false,
                    escalationRequired: false,
                    processTerminationSucceeded: false,
                    sessionRestarted: false,
                    ownedProcessId,
                    InterruptGracefulTimeout);
            }

            var interruptSent = false;
            var interruptWriteTask = SendInterruptAsync();
            try
            {
                await interruptWriteTask
                    .WaitAsync(InterruptInputWriteTimeout, cancellationToken)
                    .ConfigureAwait(false);
                interruptSent = true;
                AppLogger.Info("LiveConsole", $"Graceful Ctrl+C interrupt sent. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, InputWriteTimeoutMs={InterruptInputWriteTimeout.TotalMilliseconds:0}, RecoveryTimeoutMs={InterruptGracefulTimeout.TotalMilliseconds:0}.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Graceful interrupt was sent to the owned PowerShell session.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["ownedProcessId"] = ownedProcessId,
                        ["inputWriteTimeoutMs"] = InterruptInputWriteTimeout.TotalMilliseconds,
                        ["recoveryTimeoutMs"] = InterruptGracefulTimeout.TotalMilliseconds
                    });
            }
            catch (TimeoutException)
            {
                _ = interruptWriteTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                AppLogger.Warning("LiveConsole", $"Ctrl+C input write did not complete within the bounded timeout. Escalating recovery. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, TimeoutMs={InterruptInputWriteTimeout.TotalMilliseconds:0}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "InterruptRequested",
                    "Ctrl+C input delivery timed out before it could be confirmed.",
                    "InputWriteTimedOut",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["ownedProcessId"] = ownedProcessId,
                        ["timeoutMs"] = InterruptInputWriteTimeout.TotalMilliseconds
                    });
            }

            var recoveryOutcome = interruptSent
                ? await WaitForInterruptRecoveryAsync(
                        process,
                        sessionGeneration,
                        InterruptGracefulTimeout,
                        cancellationToken)
                    .ConfigureAwait(false)
                : InterruptRecoveryWaitOutcome.TimedOut;

            if (recoveryOutcome == InterruptRecoveryWaitOutcome.Recovered)
            {
                AppLogger.Info("LiveConsole", $"Graceful interrupt completed before timeout. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "InterruptRequested",
                    "Graceful interrupt completed before timeout.",
                    "GracefulCompletion",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["ownedProcessId"] = ownedProcessId,
                        ["timeoutMs"] = InterruptGracefulTimeout.TotalMilliseconds
                    });

                return new LiveConsoleInterruptResult(
                    interruptAttempted: true,
                    completedGracefully: true,
                    escalationRequired: false,
                    processTerminationSucceeded: false,
                    sessionRestarted: false,
                    ownedProcessId,
                    InterruptGracefulTimeout);
            }

            if (recoveryOutcome == InterruptRecoveryWaitOutcome.Superseded)
            {
                AppLogger.Info("LiveConsole", $"Interrupt target was superseded before recovery completed. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, SessionGeneration={sessionGeneration}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "InterruptRequested",
                    "Interrupt recovery stopped because its source terminal generation was replaced.",
                    "Superseded",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["ownedProcessId"] = ownedProcessId,
                        ["sessionGeneration"] = sessionGeneration
                    });

                return new LiveConsoleInterruptResult(
                    interruptAttempted: true,
                    completedGracefully: false,
                    escalationRequired: false,
                    processTerminationSucceeded: false,
                    sessionRestarted: false,
                    ownedProcessId,
                    InterruptGracefulTimeout);
            }

            var hasVisibleOwnedWindow = !string.IsNullOrWhiteSpace(TryGetMainWindowTitleNoThrow(process));
            AppLogger.Warning("LiveConsole", $"Graceful interrupt recovery timed out. Escalating to owned session restart. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, SessionGeneration={sessionGeneration}, CommandInProgress={IsCommandInProgress}, VisibleOwnedWindow={hasVisibleOwnedWindow}, TimeoutMs={InterruptGracefulTimeout.TotalMilliseconds:0}.");
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "InterruptRequested",
                "Graceful interrupt timed out. Escalating to owned session restart.",
                "EscalateToRestart",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["ownedProcessId"] = ownedProcessId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["commandInProgress"] = IsCommandInProgress,
                    ["visibleOwnedWindow"] = hasVisibleOwnedWindow,
                    ["timeoutMs"] = InterruptGracefulTimeout.TotalMilliseconds
                });

            var output = onOutput ?? (_ => { });
            output(new ExecutionOutputRecord(
                ExecutionOutputStreamKind.Lifecycle,
                $"Interrupt timed out after {InterruptGracefulTimeout.TotalSeconds:0.#} seconds. Restarting the owned PowerShell session.",
                DateTime.Now));

            var stopResult = await StopInterruptTargetAsync(
                    process,
                    sessionGeneration,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
            // StopConsoleCoreAsync reports the health of the entire bounded ConPTY cleanup,
            // not just whether the owned PowerShell process was terminated. A GUI child can
            // keep a copied ConPTY handle alive briefly after Ctrl+C, causing reader/native
            // cleanup to miss its timeout even though the PowerShell process is already gone.
            // The replacement session must be allowed to start in that case.
            var processTerminationSucceeded = stopResult.Succeeded || !IsProcessRunningNoThrow(process);

            if (!stopResult.TargetWasCurrent)
            {
                AppLogger.Info("LiveConsole", $"Interrupt escalation did not stop a replacement terminal session. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, SessionGeneration={sessionGeneration}.");
                return new LiveConsoleInterruptResult(
                    interruptAttempted: true,
                    completedGracefully: false,
                    escalationRequired: true,
                    processTerminationSucceeded: false,
                    sessionRestarted: false,
                    ownedProcessId,
                    InterruptGracefulTimeout);
            }
            AppLogger.Info("LiveConsole", $"Owned PowerShell session termination completed. OperationId={operationId}, ProcessId={ownedProcessId?.ToString() ?? "(none)"}, TerminationSucceeded={processTerminationSucceeded}.");
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "Owned PowerShell session termination completed.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["ownedProcessId"] = ownedProcessId,
                    ["terminationSucceeded"] = processTerminationSucceeded
                });

            var sessionRestarted = false;
            if (processTerminationSucceeded && runtime is not null)
            {
                try
                {
                    await StartSessionAsync(runtime, output, workingDirectory, cancellationToken).ConfigureAwait(false);
                    sessionRestarted = true;
                    output(new ExecutionOutputRecord(
                        ExecutionOutputStreamKind.Lifecycle,
                        "PowerShell session was forcibly restarted because the running script did not respond to Interrupt.",
                        DateTime.Now));
                    AppLogger.Info("LiveConsole", $"Owned PowerShell session restarted after forced termination. OperationId={operationId}, PreviousProcessId={ownedProcessId?.ToString() ?? "(none)"}, Runtime='{runtime.DisplayName}'.");
                    DeveloperDiagnostics.LogStateTransition(
                        "Terminal",
                        "InterruptRequested",
                        "InterruptTimedOut",
                        "SessionRestarted",
                        "Owned PowerShell session restarted after forced termination.",
                        new Dictionary<string, object?>
                        {
                            ["operationId"] = operationId,
                            ["ownedProcessId"] = ownedProcessId,
                            ["runtimePath"] = runtime.ExecutablePath,
                            ["workingDirectory"] = workingDirectory
                        });
                }
                catch (Exception ex)
                {
                    AppLogger.Error("LiveConsole", $"Failed to restart the owned PowerShell session after forced termination. OperationId={operationId}, PreviousProcessId={ownedProcessId?.ToString() ?? "(none)"}.", ex);
                    DeveloperDiagnostics.LogException(
                        "Terminal",
                        ex,
                        "Failed to restart the owned PowerShell session after forced termination.",
                        new Dictionary<string, object?>
                        {
                            ["operationId"] = operationId,
                            ["ownedProcessId"] = ownedProcessId,
                            ["runtimePath"] = runtime.ExecutablePath,
                            ["workingDirectory"] = workingDirectory
                        });
                    throw;
                }
            }

            return new LiveConsoleInterruptResult(
                interruptAttempted: true,
                completedGracefully: false,
                escalationRequired: true,
                processTerminationSucceeded,
                sessionRestarted,
                ownedProcessId,
                InterruptGracefulTimeout);
        }

        public async Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null)
        {
            await _sessionLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await StopConsoleCoreAsync(onOutput, "explicit-stop").ConfigureAwait(false);
            }
            finally
            {
                _sessionLifecycleGate.Release();
            }
        }

        public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            await _sessionLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await StopConsoleCoreAsync(onOutput: null, "application-shutdown").ConfigureAwait(false);
            }
            finally
            {
                _sessionLifecycleGate.Release();
            }
        }

        private async Task<bool> StopConsoleCoreAsync(
            Action<ExecutionOutputRecord>? onOutput,
            string reason)
        {
            Process? processToStop;
            CancellationTokenSource? readerCancellation;
            Task? stdoutReaderTask;
            Task? stderrReaderTask;
            StreamWriter? writerToDispose;
            IntPtr pseudoConsole;
            IntPtr inputWriterHandle;
            IntPtr outputReaderHandle;
            List<string> snapshotPathsToDelete;
            int sessionGeneration;
            Task priorNativeTeardown;

            lock (_syncRoot)
            {
                sessionGeneration = _terminalSessionGeneration;
                priorNativeTeardown = _pendingNativeTeardownTask;
                processToStop = _process;
                readerCancellation = _readerCancellationTokenSource;
                stdoutReaderTask = _stdoutReaderTask;
                stderrReaderTask = _stderrReaderTask;
                writerToDispose = _terminalWriter;
                pseudoConsole = _pseudoConsoleHandle;
                inputWriterHandle = _inputWriterHandle;
                outputReaderHandle = _outputReaderHandle;

                _process = null;
                _readerCancellationTokenSource = null;
                _stdoutReaderTask = null;
                _stderrReaderTask = null;
                _terminalWriter = null;
                _pseudoConsoleHandle = IntPtr.Zero;
                _inputWriterHandle = IntPtr.Zero;
                _outputReaderHandle = IntPtr.Zero;
                _redirectedTerminalTransportActive = false;
                ActiveRuntime = null;
                CurrentWorkingDirectory = null;
                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _firstOutputLogged = false;
                _firstAnsiOutputLogged = false;
                _rawOutputInfoLogCount = 0;
                snapshotPathsToDelete = new List<string>(_pendingSnapshotPaths);
                _pendingSnapshotPaths.Clear();
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingLocationToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
                _preStartBufferTruncated = false;
                _lastPromptHeuristicDirectory = null;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = false;
                _currentDispatchStartedUtc = null;
                _handledTerminalExitProcessId = null;
                _terminalSessionTeardownInProgress = true;
                _resizeFailureEpisode.ResetForSession(_terminalSessionGeneration);
                _commandDispatchGeneration++;
            }

            NotifyTerminalSessionStopping(sessionGeneration);

            AppLogger.Info(
                "LiveConsole",
                $"Terminal teardown started. Reason={reason}, SessionGeneration={sessionGeneration}, ProcessId={TryGetProcessId(processToStop)?.ToString() ?? "(none)"}, PendingSnapshots={snapshotPathsToDelete.Count}.");
            DeveloperDiagnostics.LogStateTransition(
                "Terminal",
                "TerminalSessionTeardown",
                "Running",
                "Stopping",
                "Terminal teardown rejected new input and reset protocol state.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["sessionGeneration"] = sessionGeneration,
                    ["pendingSnapshotCount"] = snapshotPathsToDelete.Count
                });

            var inputDrainTask = _terminalInputRouter.DeactivateAsync(
                sessionGeneration,
                InputDrainTimeout);
            var priorNativeTeardownCompletionTask = AwaitTaskWithinAsync(
                priorNativeTeardown,
                ReaderDrainTimeout);
            var processTerminationSucceeded = true;

            try
            {
                readerCancellation?.Cancel();
            }
            catch
            {
                // Best effort only.
            }

            // Kill the process first, THEN close the output reader handle and pseudo-console.
            // Windows documentation requires ClosePseudoConsole to be called after the process
            // exits.  Calling it while the process is still running and the reader task is
            // blocked in ReadFile causes ClosePseudoConsole to block until all pending I/O
            // completes — hanging StopConsoleAsync indefinitely and leaving IsExecutionRunning
            // stuck at true, which permanently disables the Play button.
            foreach (var snapshotPath in snapshotPathsToDelete)
            {
                TryDeleteSnapshot(snapshotPath);
            }

            if (processToStop is not null)
            {
                try
                {
                    if (!processToStop.HasExited)
                    {
                        processToStop.Kill(entireProcessTree: true);

                        // Cap the wait so Stop always feels immediate to the user.
                        // The process is already signalled; a 2-second timeout is
                        // a generous safety net for slow OS teardown.
                        using var killTimeout = new CancellationTokenSource(ProcessExitTimeout);
                        try
                        {
                            await processToStop.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            processTerminationSucceeded = false;
                            AppLogger.Warning(
                                "LiveConsole",
                                $"Terminal process did not confirm exit within the bounded wait. Reason={reason}, SessionGeneration={sessionGeneration}, TimeoutMs={ProcessExitTimeout.TotalMilliseconds}.");
                        }
                    }

                }
                catch (Exception ex)
                {
                    processTerminationSucceeded = false;
                    AppLogger.Error(
                        "LiveConsole",
                        $"Terminal process termination failed. Reason={reason}, SessionGeneration={sessionGeneration}.",
                        ex);
                }
                finally
                {
                    processToStop.Dispose();
                }
            }

            if (inputWriterHandle != IntPtr.Zero)
            {
                CloseHandle(inputWriterHandle);
            }

            var inputDrained = await inputDrainTask.ConfigureAwait(false);

            var writerDisposalTask = Task.Run(() =>
            {
                try
                {
                    writerToDispose?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("LiveConsole", $"Terminal writer disposal completed with an error. Reason={ex.Message}");
                }
            });

            var nativeTeardownTask = Task.Run(() =>
            {
                try
                {
                    if (outputReaderHandle != IntPtr.Zero)
                    {
                        CloseHandle(outputReaderHandle);
                    }
                }
                catch
                {
                    // Best effort only.
                }

                try
                {
                    if (pseudoConsole != IntPtr.Zero)
                    {
                        ClosePseudoConsole(pseudoConsole);
                    }
                }
                catch
                {
                    // Best effort only.
                }

            });

            var trackedNativeTeardownTask = Task.WhenAll(
                priorNativeTeardown,
                writerDisposalTask,
                nativeTeardownTask);
            lock (_syncRoot)
            {
                _pendingNativeTeardownTask = trackedNativeTeardownTask;
            }

            var readerTasks = new List<Task> { trackedNativeTeardownTask };
            if (stdoutReaderTask is not null)
            {
                readerTasks.Add(stdoutReaderTask);
            }

            if (stderrReaderTask is not null)
            {
                readerTasks.Add(stderrReaderTask);
            }

            var readersDrained = await AwaitTaskWithinAsync(
                Task.WhenAll(readerTasks),
                ReaderDrainTimeout).ConfigureAwait(false);
            var priorNativeTeardownCompleted = await priorNativeTeardownCompletionTask.ConfigureAwait(false);

            if (readersDrained)
            {
                readerCancellation?.Dispose();
            }
            else if (readerCancellation is not null)
            {
                _ = Task.WhenAll(readerTasks).ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    readerCancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            var teardownSucceeded = priorNativeTeardownCompleted &&
                                    processTerminationSucceeded &&
                                    inputDrained &&
                                    readersDrained;
            AppLogger.Info(
                "LiveConsole",
                $"Terminal teardown completed. Reason={reason}, SessionGeneration={sessionGeneration}, Succeeded={teardownSucceeded}, ProcessTerminationSucceeded={processTerminationSucceeded}, InputDrained={inputDrained}, ReadersDrained={readersDrained}, PriorNativeTeardownCompleted={priorNativeTeardownCompleted}.");
            DeveloperDiagnostics.LogStateTransition(
                "Terminal",
                "TerminalSessionTeardown",
                "Stopping",
                teardownSucceeded ? "Stopped" : "TeardownIncomplete",
                "Bounded terminal teardown completed.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["sessionGeneration"] = sessionGeneration,
                    ["processTerminationSucceeded"] = processTerminationSucceeded,
                    ["inputDrained"] = inputDrained,
                    ["readersDrained"] = readersDrained,
                    ["priorNativeTeardownCompleted"] = priorNativeTeardownCompleted
                });

            return teardownSucceeded;
        }

        public void Dispose()
        {
            try
            {
                ShutdownAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                AppLogger.Error("LiveConsole", "Synchronous terminal disposal did not complete cleanly within its bounded wait.", ex);
            }
        }

        private static async Task<bool> AwaitTaskWithinAsync(Task task, TimeSpan timeout)
        {
            try
            {
                await task.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        private void StartPseudoConsoleSession(
            PowerShellRuntimeInfo runtime,
            string workingDirectory,
            Action<ExecutionOutputRecord> onOutput,
            int sessionGeneration)
        {
            SECURITY_ATTRIBUTES securityAttributes = new()
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = true
            };

            IntPtr inputReadSide = IntPtr.Zero;
            IntPtr inputWriteSide = IntPtr.Zero;
            IntPtr outputReadSide = IntPtr.Zero;
            IntPtr outputWriteSide = IntPtr.Zero;
            IntPtr attributeListBuffer = IntPtr.Zero;
            PROCESS_INFORMATION processInformation = default;

            try
            {
                if (!CreatePipe(out inputReadSide, out inputWriteSide, ref securityAttributes, 0))
                {
                    throw new InvalidOperationException($"CreatePipe(input) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!SetHandleInformation(inputWriteSide, HANDLE_FLAG_INHERIT, 0))
                {
                    throw new InvalidOperationException($"SetHandleInformation(input) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!CreatePipe(out outputReadSide, out outputWriteSide, ref securityAttributes, 0))
                {
                    throw new InvalidOperationException($"CreatePipe(output) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!SetHandleInformation(outputReadSide, HANDLE_FLAG_INHERIT, 0))
                {
                    throw new InvalidOperationException($"SetHandleInformation(output) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                var size = new COORD((short)_terminalColumns, (short)_terminalRows);
                var createPseudoConsoleResult = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var pseudoConsole);
                if (createPseudoConsoleResult != 0)
                {
                    throw new InvalidOperationException($"CreatePseudoConsole failed with HRESULT 0x{createPseudoConsoleResult:X8}.");
                }

                _pseudoConsoleHandle = pseudoConsole;

                CloseHandle(inputReadSide);
                inputReadSide = IntPtr.Zero;
                CloseHandle(outputWriteSide);
                outputWriteSide = IntPtr.Zero;

                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeListBuffer = Marshal.AllocHGlobal(attributeListSize);

                if (!InitializeProcThreadAttributeList(attributeListBuffer, 1, 0, ref attributeListSize))
                {
                    throw new InvalidOperationException($"InitializeProcThreadAttributeList failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!UpdateProcThreadAttribute(
                        attributeListBuffer,
                        0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        pseudoConsole,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new InvalidOperationException($"UpdateProcThreadAttribute failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                STARTUPINFOEX startupInfo = new();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                startupInfo.lpAttributeList = attributeListBuffer;

                // Launch pwsh.exe as an interactive terminal and disable PSReadLine
                // prediction by default. The editor already provides IntelliSense, and
                // predictions in the embedded terminal can look like editor autofill.
                var startupCommand = BuildTerminalStartupCommand();
                var launchPath = runtime.LaunchExecutablePath;
                var arguments = BuildInteractivePowerShellArguments(startupCommand);
                AppLogger.Info("LiveConsole", $"ConPTY CreateProcessW launch path: '{launchPath}'. Starting hosted PowerShell with -STA. Arguments='{arguments}'.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Starting hosted ConPTY PowerShell process in STA mode.",
                    new Dictionary<string, object?>
                    {
                        ["launchPath"] = launchPath,
                        ["workingDirectory"] = workingDirectory,
                        ["usesSta"] = true,
                        ["arguments"] = arguments
                    });
                var commandLine = "\"" + launchPath + "\" " + arguments;

                if (!CreateProcessW(
                        launchPath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation))
                {
                    throw new InvalidOperationException($"CreateProcessW failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                var process = Process.GetProcessById((int)processInformation.dwProcessId);
                process.EnableRaisingEvents = true;
                // Capture onOutput and the event in closures so they always route to the
                // correct sink and handler, even if a new session starts before this
                // process fully terminates.
                var capturedOnOutput = onOutput;
                process.Exited += (_, _) => QueueTerminalProcessExitTeardown(
                    "ConPTY",
                    process,
                    capturedOnOutput,
                    sessionGeneration);

                _process = process;
                _inputWriterHandle = inputWriteSide;
                _outputReaderHandle = outputReadSide;

                var writerStream = new FileStream(
                    new SafeFileHandle(_inputWriterHandle, ownsHandle: false),
                    FileAccess.Write,
                    4096,
                    isAsync: false);

                _terminalWriter = new StreamWriter(writerStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                _redirectedTerminalTransportActive = false;
                _terminalInputRouter.Activate(sessionGeneration, _terminalWriter);

                _readerCancellationTokenSource = new CancellationTokenSource();
                _stdoutReaderTask = Task.Run(
                    () => ReadPseudoConsoleOutputLoopAsync(
                        _outputReaderHandle,
                        onOutput,
                        sessionGeneration,
                        _readerCancellationTokenSource.Token));

                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                    processInformation.hThread = IntPtr.Zero;
                }

                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                    processInformation.hProcess = IntPtr.Zero;
                }
            }
            catch
            {
                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                }

                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                }

                if (attributeListBuffer != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeListBuffer);
                    Marshal.FreeHGlobal(attributeListBuffer);
                }

                if (inputReadSide != IntPtr.Zero)
                {
                    CloseHandle(inputReadSide);
                }

                if (inputWriteSide != IntPtr.Zero)
                {
                    CloseHandle(inputWriteSide);
                }

                if (outputReadSide != IntPtr.Zero)
                {
                    CloseHandle(outputReadSide);
                }

                if (outputWriteSide != IntPtr.Zero)
                {
                    CloseHandle(outputWriteSide);
                }

                if (_pseudoConsoleHandle != IntPtr.Zero)
                {
                    ClosePseudoConsole(_pseudoConsoleHandle);
                    _pseudoConsoleHandle = IntPtr.Zero;
                }

                throw;
            }
            finally
            {
                if (attributeListBuffer != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeListBuffer);
                    Marshal.FreeHGlobal(attributeListBuffer);
                }
            }
        }

        private void StartRedirectedSession(
            PowerShellRuntimeInfo runtime,
            string workingDirectory,
            Action<ExecutionOutputRecord> onOutput,
            int sessionGeneration)
        {
            var redirectedArguments = BuildInteractivePowerShellArguments(BuildTerminalStartupCommand());
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtime.LaunchExecutablePath,
                    Arguments = redirectedArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    WorkingDirectory = workingDirectory
                },
                EnableRaisingEvents = true
            };
            AppLogger.Info("LiveConsole", $"Redirected terminal ProcessStartInfo.FileName='{process.StartInfo.FileName}'. Starting hosted PowerShell with -STA. Arguments='{redirectedArguments}'.");
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "Starting redirected hosted PowerShell process in STA mode.",
                new Dictionary<string, object?>
                {
                    ["launchPath"] = runtime.LaunchExecutablePath,
                    ["workingDirectory"] = workingDirectory,
                    ["usesSta"] = true,
                    ["arguments"] = redirectedArguments
                });

            var capturedOnOutput = onOutput;
            process.Exited += (_, _) => QueueTerminalProcessExitTeardown(
                "redirected",
                process,
                capturedOnOutput,
                sessionGeneration);

            if (!process.Start())
            {
                throw new InvalidOperationException("The redirected PowerShell terminal process could not be started.");
            }

            _process = process;
            _terminalWriter = process.StandardInput;
            _redirectedTerminalTransportActive = true;
            _terminalInputRouter.Activate(sessionGeneration, _terminalWriter);
            _readerCancellationTokenSource = new CancellationTokenSource();
            _stdoutReaderTask = Task.Run(() => ReadStreamLoopAsync(
                process.StandardOutput,
                ExecutionOutputStreamKind.StandardOutput,
                onOutput,
                sessionGeneration,
                _readerCancellationTokenSource.Token));
            _stderrReaderTask = Task.Run(() => ReadStreamLoopAsync(
                process.StandardError,
                ExecutionOutputStreamKind.StandardError,
                onOutput,
                sessionGeneration,
                _readerCancellationTokenSource.Token));
        }

        private void QueueTerminalProcessExitTeardown(
            string terminalMode,
            Process? exitedProcess,
            Action<ExecutionOutputRecord> capturedOnOutput,
            int sessionGeneration)
        {
            var teardownTask = HandleTerminalProcessExitedAsync(
                terminalMode,
                exitedProcess,
                capturedOnOutput,
                sessionGeneration);

            lock (_syncRoot)
            {
                _lastProcessExitTeardownTask = teardownTask;
            }

            _ = teardownTask.ContinueWith(
                static task =>
                {
                    if (task.Exception is not null)
                    {
                        AppLogger.Error(
                            "LiveConsole",
                            "Terminal process-exit teardown failed.",
                            task.Exception.GetBaseException());
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task HandleTerminalProcessExitedAsync(
            string terminalMode,
            Process? exitedProcess,
            Action<ExecutionOutputRecord> capturedOnOutput,
            int sessionGeneration)
        {
            bool commandInProgress;
            bool currentCommandIsScript;
            int pendingSnapshotCount;
            bool shouldIgnore;

            var processId = TryGetProcessId(exitedProcess);

            await _sessionLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_syncRoot)
                {
                    var currentProcessId = TryGetProcessId(_process);
                    var trackedCommandInProgress = _isCommandInProgress;
                    shouldIgnore = !TerminalSessionEventPolicy.IsCurrentSession(
                                       _terminalSessionGeneration,
                                       sessionGeneration,
                                       _terminalSessionTeardownInProgress) ||
                                   TerminalSessionEventPolicy.ShouldIgnoreProcessExit(
                                       _process is not null,
                                       trackedCommandInProgress,
                                       processId,
                                       currentProcessId,
                                       _handledTerminalExitProcessId);

                    if (shouldIgnore)
                    {
                        commandInProgress = false;
                        currentCommandIsScript = false;
                        pendingSnapshotCount = 0;
                    }
                    else
                    {
                        if (processId.HasValue)
                        {
                            _handledTerminalExitProcessId = processId.Value;
                        }

                        commandInProgress = _isCommandInProgress;
                        currentCommandIsScript = _currentCommandIsScript;
                        pendingSnapshotCount = _pendingSnapshotPaths.Count;
                    }
                }

                if (shouldIgnore)
                {
                    AppLogger.Debug(
                        "LiveConsole",
                        $"Ignored stale {terminalMode} PowerShell process-exit notification. SessionGeneration={sessionGeneration}, ProcessId={processId?.ToString() ?? "(unknown)"}.");
                    return;
                }

                var exitCode = TryGetExitCode(exitedProcess);
                var exitCodeText = exitCode.HasValue ? $" Exit code: {exitCode.Value}." : string.Empty;
                var activeWorkDescription = currentCommandIsScript ? "script" : "command";
                var userMessage = commandInProgress
                    ? $"PowerShell terminal process exited while a {activeWorkDescription} was running. The app detected the exit, cleared the running state, and will attempt to restart the embedded PowerShell session.{exitCodeText}"
                    : $"PowerShell terminal session exited. The app will attempt to restart the embedded PowerShell session.{exitCodeText}";

                AppLogger.Info(
                    "LiveConsole",
                    $"The {terminalMode} PowerShell terminal process exited. SessionGeneration={sessionGeneration}, ProcessId={processId?.ToString() ?? "(unknown)"}, ExitCode={exitCode?.ToString() ?? "(unknown)"}, CommandInProgress={commandInProgress}, CurrentCommandIsScript={currentCommandIsScript}, PendingSnapshots={pendingSnapshotCount}. Running centralized terminal teardown.");
                await StopConsoleCoreAsync(capturedOnOutput, "process-exit").ConfigureAwait(false);

                PublishLifecycleMessage(capturedOnOutput, userMessage);

                try
                {
                    SessionTerminated?.Invoke();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("LiveConsole", "A PowerShell terminal process-exit subscriber failed.", ex);
                }
            }
            finally
            {
                _sessionLifecycleGate.Release();
            }
        }

        private async Task ReadPseudoConsoleOutputLoopAsync(
            IntPtr outputReaderHandle,
            Action<ExecutionOutputRecord> onOutput,
            int sessionGeneration,
            CancellationToken cancellationToken)
        {
            try
            {
                using var stream = new FileStream(
                    new SafeFileHandle(outputReaderHandle, ownsHandle: false),
                    FileAccess.Read,
                    4096,
                    isAsync: false);
                using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

                char[] buffer = new char[2048];
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Anonymous pipes created for ConPTY are synchronous handles. Using
                    // StreamReader.ReadAsync on them can throw "Handle does not support
                    // asynchronous operations". This loop already runs on a background
                    // task, so a blocking read is the correct and stable choice.
                    int charsRead = reader.Read(buffer, 0, buffer.Length);
                    if (charsRead <= 0)
                    {
                        break;
                    }

                    if (!IsCurrentSessionGeneration(sessionGeneration))
                    {
                        break;
                    }

                    if (!_firstOutputLogged)
                    {
                        _firstOutputLogged = true;
                        var hasAnsi = Array.IndexOf(buffer, '\x1b', 0, charsRead) >= 0;
                        System.Diagnostics.Debug.WriteLine(
                            $"[LiveConsoleService] First ConPTY chunk — {charsRead} chars, hasAnsi={hasAnsi}, contentOmitted=true");
                    }

                    PublishTerminalChunkForSession(
                        new string(buffer, 0, charsRead),
                        ExecutionOutputStreamKind.StandardOutput,
                        onOutput,
                        sessionGeneration);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                if (IsCurrentSessionGeneration(sessionGeneration))
                {
                    AppLogger.Error("LiveConsole", "ConPTY terminal reader stopped unexpectedly.", ex);
                    onOutput(new ExecutionOutputRecord(
                        ExecutionOutputStreamKind.Lifecycle,
                        $"Terminal reader stopped unexpectedly: {ex.Message}",
                        DateTime.Now));
                }
            }
        }

        private async Task ReadStreamLoopAsync(
            StreamReader reader,
            ExecutionOutputStreamKind streamKind,
            Action<ExecutionOutputRecord> onOutput,
            int sessionGeneration,
            CancellationToken cancellationToken)
        {
            try
            {
                char[] buffer = new char[2048];
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Anonymous pipes created for ConPTY are synchronous handles. Using
                    // StreamReader.ReadAsync on them can throw "Handle does not support
                    // asynchronous operations". This loop already runs on a background
                    // task, so a blocking read is the correct and stable choice.
                    int charsRead = reader.Read(buffer, 0, buffer.Length);
                    if (charsRead <= 0)
                    {
                        break;
                    }

                    if (!IsCurrentSessionGeneration(sessionGeneration))
                    {
                        break;
                    }

                    PublishTerminalChunkForSession(
                        new string(buffer, 0, charsRead),
                        streamKind,
                        onOutput,
                        sessionGeneration);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                if (IsCurrentSessionGeneration(sessionGeneration))
                {
                    AppLogger.Error("LiveConsole", "Redirected terminal reader stopped unexpectedly.", ex);
                    onOutput(new ExecutionOutputRecord(
                        ExecutionOutputStreamKind.Lifecycle,
                        $"Terminal reader stopped unexpectedly: {ex.Message}",
                        DateTime.Now));
                }
            }
        }

        private void PublishTerminalChunk(
            string rawChunk,
            ExecutionOutputStreamKind streamKind,
            Action<ExecutionOutputRecord> onOutput)
        {
            PublishTerminalChunkCore(rawChunk, streamKind, onOutput, observedSessionGeneration: null);
        }

        private void PublishTerminalChunkForSession(
            string rawChunk,
            ExecutionOutputStreamKind streamKind,
            Action<ExecutionOutputRecord> onOutput,
            int observedSessionGeneration)
        {
            PublishTerminalChunkCore(rawChunk, streamKind, onOutput, observedSessionGeneration);
        }

        private void PublishTerminalChunkCore(
            string rawChunk,
            ExecutionOutputStreamKind streamKind,
            Action<ExecutionOutputRecord> onOutput,
            int? observedSessionGeneration)
        {
            if (string.IsNullOrEmpty(rawChunk))
            {
                return;
            }

            if (observedSessionGeneration.HasValue &&
                !IsCurrentSessionGeneration(observedSessionGeneration.Value))
            {
                AppLogger.Debug(
                    "LiveConsole",
                    $"Ignored stale terminal output. ObservedSessionGeneration={observedSessionGeneration.Value}.");
                return;
            }

            var dispatchGeneration = GetCurrentCommandDispatchGeneration();

            // ── Raw path (for xterm.js) ───────────────────────────────────────────
            // Strip only null bytes; preserve all ANSI/VT100 sequences so xterm.js
            // can render colors, cursor movement, progress bars, etc.
            var raw = rawChunk.Replace("\0", string.Empty, StringComparison.Ordinal);
            raw = FilterInternalTerminalOutput(raw, out var hasSentinel, observedSessionGeneration);

            // ── Cleaned path (for internal tracking) ─────────────────────────────
            // Strip OSC/ANSI sequences and normalise line endings so that the
            // current-directory regex and lifecycle checks work on plain text.
            var cleaned = OscRegex.Replace(raw, string.Empty);
            cleaned = AnsiRegex.Replace(cleaned, string.Empty);
            cleaned = cleaned.Replace("\r\n", "\n", StringComparison.Ordinal);
            cleaned = cleaned.Replace("\r", "\n", StringComparison.Ordinal);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseTerminalEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Terminal",
                    "ConPTY output chunk processed with terminal content omitted.",
                    new Dictionary<string, object?>
                    {
                        ["dispatchGeneration"] = dispatchGeneration,
                        ["streamKind"] = streamKind.ToString(),
                        ["rawBeforeFilterLength"] = rawChunk.Length,
                        ["rawAfterFilterLength"] = raw.Length,
                        ["cleanedAfterFilterLength"] = cleaned.Length,
                        ["containsAnsi"] = raw.Contains('\x1b'),
                        ["containsSentinel"] = hasSentinel,
                        ["contentOmitted"] = true
                    });
            }

            // Fire the completion event if the sentinel was present.
            if (hasSentinel)
            {
                AppLogger.Debug("LiveConsole", "Execution-done sentinel detected in terminal output and filtered before xterm.js.");
                CompleteCommandExecution(observedSessionGeneration);
            }

            if (!string.IsNullOrEmpty(cleaned))
            {
                UpdateCurrentDirectoryFromPromptCore(cleaned, observedSessionGeneration);
            }

            if (!_firstAnsiOutputLogged && raw.Contains('\x1b'))
            {
                _firstAnsiOutputLogged = true;
                AppLogger.Info("LiveConsole", "Observed first ANSI/VT chunk from PowerShell/ConPTY. Raw color path is reaching xterm.js.");
            }

            if (_rawOutputInfoLogCount < 4)
            {
                _rawOutputInfoLogCount++;
                AppLogger.Info(
                    "LiveConsole",
                    $"ConPTY raw output chunk #{_rawOutputInfoLogCount}. Stream={streamKind}, RawLength={raw.Length}, CleanLength={cleaned.Length}, HasAnsi={raw.Contains('\x1b')}, ContentOmitted=True.");
            }

            // ── Output routing ────────────────────────────────────────────────────
            // When a raw-output subscriber is registered (i.e. the xterm.js terminal
            // control is wired up), send the raw VT data there and skip the cleaned-
            // text path for display.  If no subscriber is present — e.g. during early
            // startup before the control initialises — fall back to the cleaned path
            // so text is not silently dropped.
            if (!string.IsNullOrEmpty(raw))
            {
                var meaningfulUserOutput = ContainsMeaningfulUserOutput(raw, cleaned);
                lock (_syncRoot)
                {
                    if (observedSessionGeneration.HasValue &&
                        !TerminalSessionEventPolicy.IsCurrentSession(
                            _terminalSessionGeneration,
                            observedSessionGeneration.Value,
                            _terminalSessionTeardownInProgress))
                    {
                        return;
                    }

                    MarkCurrentCommandVisibleOutputSeen(meaningfulUserOutput);
                    var rawHandler = RawOutputReceived;
                    if (rawHandler is not null)
                    {
                        rawHandler(observedSessionGeneration ?? _terminalSessionGeneration, raw);
                    }
                    else if (!string.IsNullOrEmpty(cleaned))
                    {
                        onOutput(new ExecutionOutputRecord(streamKind, cleaned, DateTime.Now));
                    }
                }
            }
        }

        private bool IsCurrentSessionGeneration(int observedSessionGeneration)
        {
            lock (_syncRoot)
            {
                return TerminalSessionEventPolicy.IsCurrentSession(
                    _terminalSessionGeneration,
                    observedSessionGeneration,
                    _terminalSessionTeardownInProgress);
            }
        }

        private bool TryLogInternalDispatchDiagnostic(string sanitizedSegment)
        {
            if (string.IsNullOrWhiteSpace(sanitizedSegment))
            {
                return false;
            }

            var normalized = TrimPromptPrefix(sanitizedSegment);
            var tokenIndex = normalized.IndexOf(DispatchDiagnosticTokenPrefix, StringComparison.Ordinal);
            if (tokenIndex < 0)
            {
                return false;
            }

            var message = normalized[(tokenIndex + DispatchDiagnosticTokenPrefix.Length)..].Trim();
            AppLogger.Info("LiveConsole", $"Internal script dispatch diagnostic observed. Length={message.Length}, ContentOmitted=True.");
            DeveloperDiagnostics.LogInfo(
                "Execution",
                "Internal script dispatch diagnostic observed from the hosted PowerShell process.",
                new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(message))
                {
                    ["dispatchGeneration"] = GetCurrentCommandDispatchGeneration()
                });
            return true;
        }

        private int GetCurrentCommandDispatchGeneration()
        {
            lock (_syncRoot)
            {
                return _commandDispatchGeneration;
            }
        }

        private void MarkCurrentCommandVisibleOutputSeen(bool meaningfulUserOutput)
        {
            if (!meaningfulUserOutput)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_isCommandInProgress)
                {
                    _currentDispatchVisibleOutputSeen = true;
                }
            }
        }

        private static bool ContainsMeaningfulUserOutput(string raw, string cleaned)
        {
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var text = OscRegex.Replace(raw, string.Empty);
            text = AnsiRegex.Replace(text, string.Empty);
            text = text.Replace("\0", string.Empty, StringComparison.Ordinal);
            return !string.IsNullOrWhiteSpace(text);
        }

        private void ScheduleCommandHealthMonitor(int dispatchGeneration, bool isScript, string displayName, Action<ExecutionOutputRecord> onOutput)
        {
            int sessionGeneration;
            lock (_syncRoot)
            {
                sessionGeneration = _terminalSessionGeneration;
            }

            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "Command health monitor scheduled.",
                new Dictionary<string, object?>
                {
                    ["dispatchGeneration"] = dispatchGeneration,
                    ["sessionGeneration"] = sessionGeneration,
                    ["isScript"] = isScript,
                    ["displayName"] = displayName,
                    ["pollIntervalMs"] = CommandHealthPollInterval.TotalMilliseconds,
                    ["startConfirmationDelayMs"] = ScriptStartConfirmationDelay.TotalMilliseconds
                });

            _ = Task.Run(async () =>
            {
                var startedAt = DateTime.UtcNow;
                var startConfirmationNoticePublished = false;
                var tickCount = 0;

                while (true)
                {
                    try
                    {
                        await Task.Delay(CommandHealthPollInterval).ConfigureAwait(false);
                        tickCount++;

                        Process? process;
                        bool commandInProgress;
                        bool startConfirmed;
                        bool meaningfulOutputSeen;
                        int currentGeneration;
                        DateTime? commandStartedUtc;

                        lock (_syncRoot)
                        {
                            process = _process;
                            commandInProgress = _isCommandInProgress;
                            currentGeneration = _commandDispatchGeneration;
                            startConfirmed = _currentDispatchStartConfirmed ||
                                             string.IsNullOrEmpty(_pendingStartToken);
                            meaningfulOutputSeen = _currentDispatchVisibleOutputSeen;
                            commandStartedUtc = _currentDispatchStartedUtc;
                        }

                        var processId = TryGetProcessId(process);
                        var processRunning = IsProcessRunningNoThrow(process);

                        if (tickCount == 1 || tickCount % 4 == 0)
                        {
                            var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
                            DeveloperDiagnostics.LogDebug(
                                "Terminal",
                                "Command health monitor tick.",
                                new Dictionary<string, object?>
                                {
                                    ["dispatchGeneration"] = dispatchGeneration,
                                    ["currentGeneration"] = currentGeneration,
                                    ["isScript"] = isScript,
                                    ["displayName"] = displayName,
                                    ["tickCount"] = tickCount,
                                    ["elapsedMs"] = elapsedMs,
                                    ["commandInProgress"] = commandInProgress,
                                    ["processId"] = processId,
                                    ["processRunning"] = processRunning,
                                    ["startConfirmed"] = startConfirmed,
                                    ["meaningfulOutputSeen"] = meaningfulOutputSeen,
                                    ["commandStartedUtc"] = commandStartedUtc
                                });
                        }

                        if (!TerminalSessionEventPolicy.IsCurrentDispatch(
                                commandInProgress,
                                currentGeneration,
                                dispatchGeneration))
                        {
                            DeveloperDiagnostics.LogDecision(
                                "Terminal",
                                "CommandHealthMonitorStop",
                                "Command health monitor stopped because command tracking ended or generation changed.",
                                "StopMonitor",
                                new Dictionary<string, object?>
                                {
                                    ["dispatchGeneration"] = dispatchGeneration,
                                    ["currentGeneration"] = currentGeneration,
                                    ["commandInProgress"] = commandInProgress,
                                    ["processId"] = processId,
                                    ["processRunning"] = processRunning,
                                    ["tickCount"] = tickCount
                                });
                            return;
                        }

                        if (!processRunning)
                        {
                            AppLogger.Warning(
                                "LiveConsole",
                                $"Command health monitor detected that the hosted PowerShell process is no longer running. DispatchGeneration={dispatchGeneration}, ProcessId={processId?.ToString() ?? "(none)"}, TickCount={tickCount}.");
                            DeveloperDiagnostics.LogDecision(
                                "Terminal",
                                "CommandHealthMonitorProcessExit",
                                "Command health monitor detected that the hosted PowerShell process is no longer running.",
                                "HandleProcessExit",
                                new Dictionary<string, object?>
                                {
                                    ["dispatchGeneration"] = dispatchGeneration,
                                    ["processId"] = processId,
                                    ["tickCount"] = tickCount,
                                    ["elapsedMs"] = (DateTime.UtcNow - startedAt).TotalMilliseconds
                                });
                            QueueTerminalProcessExitTeardown(
                                "ConPTY health monitor",
                                process,
                                onOutput,
                                sessionGeneration);
                            return;
                        }

                        if (!startConfirmed &&
                            !startConfirmationNoticePublished &&
                            DateTime.UtcNow - startedAt >= ScriptStartConfirmationDelay)
                        {
                            startConfirmationNoticePublished = true;
                            var targetName = GetDisplayNameForStatus(displayName);
                            AppLogger.Warning(
                                "LiveConsole",
                                $"Terminal dispatch start token was not observed within {ScriptStartConfirmationDelay.TotalSeconds:0.#} seconds. DispatchGeneration={dispatchGeneration}, ProcessId={processId?.ToString() ?? "(none)"}.");
                            DeveloperDiagnostics.LogDecision(
                                "Terminal",
                                "ScriptStartNotConfirmed",
                                "Terminal dispatch start token was not observed within the expected window.",
                                "RecoverDispatch",
                                new Dictionary<string, object?>
                                {
                                    ["dispatchGeneration"] = dispatchGeneration,
                                    ["processId"] = processId,
                                    ["delayMs"] = ScriptStartConfirmationDelay.TotalMilliseconds,
                                    ["tickCount"] = tickCount,
                                    ["elapsedMs"] = (DateTime.UtcNow - startedAt).TotalMilliseconds
                                });
                            RecoverUnconfirmedDispatch(
                                dispatchGeneration,
                                sessionGeneration,
                                isScript,
                                targetName,
                                onOutput);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("LiveConsole", $"Command health monitor stopped. Reason={ex.Message}");
                        DeveloperDiagnostics.LogException(
                            "Terminal",
                            ex,
                            "Command health monitor stopped because an exception occurred.",
                            new Dictionary<string, object?>
                            {
                                ["dispatchGeneration"] = dispatchGeneration,
                                ["isScript"] = isScript,
                                ["displayName"] = displayName,
                                ["tickCount"] = tickCount
                            });
                        return;
                    }
                }
            });
        }

        private void RecoverUnconfirmedDispatch(
            int dispatchGeneration,
            int sessionGeneration,
            bool expectedScript,
            string displayName,
            Action<ExecutionOutputRecord> onOutput)
        {
            string bufferedOutput;
            List<string> hiddenFragments;
            List<string> snapshotPaths;
            bool bufferWasTruncated;
            bool wasScript;

            lock (_syncRoot)
            {
                if (!TerminalSessionEventPolicy.IsCurrentSession(
                        _terminalSessionGeneration,
                        sessionGeneration,
                        _terminalSessionTeardownInProgress) ||
                    !TerminalSessionEventPolicy.IsCurrentDispatch(
                        _isCommandInProgress,
                        _commandDispatchGeneration,
                        dispatchGeneration) ||
                    string.IsNullOrEmpty(_pendingStartToken))
                {
                    return;
                }

                bufferedOutput = _hiddenOutputBuffer;
                hiddenFragments = new List<string>(_pendingHiddenOutputFragments);
                snapshotPaths = new List<string>(_pendingSnapshotPaths);
                bufferWasTruncated = _preStartBufferTruncated;
                wasScript = _currentCommandIsScript;

                _pendingSnapshotPaths.Clear();
                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingLocationToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
                _preStartBufferTruncated = false;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = false;
                _currentDispatchStartedUtc = null;
            }

            foreach (var snapshotPath in snapshotPaths)
            {
                TryDeleteSnapshot(snapshotPath);
            }

            var recoveredOutput = FilterRecoveredPreStartOutput(bufferedOutput, hiddenFragments);
            if (!string.IsNullOrEmpty(recoveredOutput) &&
                IsCurrentSessionGeneration(sessionGeneration))
            {
                PublishTerminalChunkForSession(
                    recoveredOutput,
                    ExecutionOutputStreamKind.StandardError,
                    onOutput,
                    sessionGeneration);
            }

            AppLogger.Warning(
                "LiveConsole",
                $"Recovered a terminal dispatch that did not reach its start token. DispatchGeneration={dispatchGeneration}, SessionGeneration={sessionGeneration}, WasScript={wasScript}, ExpectedScript={expectedScript}, BufferedLength={bufferedOutput.Length}, RecoveredLength={recoveredOutput.Length}, BufferTruncated={bufferWasTruncated}, DeletedSnapshotCount={snapshotPaths.Count}.");
            DeveloperDiagnostics.LogStateTransition(
                "Terminal",
                "DispatchPreStartRecovery",
                "WaitingForStartToken",
                "Idle",
                "A dispatch that never reached its start token was recovered and released.",
                new Dictionary<string, object?>
                {
                    ["dispatchGeneration"] = dispatchGeneration,
                    ["sessionGeneration"] = sessionGeneration,
                    ["wasScript"] = wasScript,
                    ["expectedScript"] = expectedScript,
                    ["bufferedLength"] = bufferedOutput.Length,
                    ["recoveredLength"] = recoveredOutput.Length,
                    ["bufferTruncated"] = bufferWasTruncated,
                    ["deletedSnapshotCount"] = snapshotPaths.Count,
                    ["contentOmitted"] = true
                });
            PublishLifecycleMessage(
                onOutput,
                $"'{displayName}' did not reach the PowerShell execution-start acknowledgement. The app released the hidden dispatch state so Run can be tried again. Reset Console if PowerShell is no longer accepting commands.");

            if (wasScript)
            {
                ScriptExecutionCompleted?.Invoke();
            }

            CommandExecutionCompleted?.Invoke();
        }

        private static string FilterRecoveredPreStartOutput(
            string bufferedOutput,
            IReadOnlyList<string> hiddenFragments)
        {
            if (string.IsNullOrEmpty(bufferedOutput))
            {
                return string.Empty;
            }

            var remaining = bufferedOutput;
            var recovered = new StringBuilder(bufferedOutput.Length);
            while (TryReadTerminalSegment(ref remaining, out var segment))
            {
                if (!ShouldSuppressRecoveredPreStartSegment(segment, hiddenFragments))
                {
                    recovered.Append(segment);
                }
            }

            if (!string.IsNullOrEmpty(remaining) &&
                !ShouldSuppressRecoveredPreStartSegment(remaining, hiddenFragments))
            {
                recovered.Append(remaining);
            }

            return recovered.ToString();
        }

        private static bool ShouldSuppressRecoveredPreStartSegment(
            string segment,
            IReadOnlyList<string> hiddenFragments)
        {
            var sanitized = RemoveControlSequences(segment);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return false;
            }

            var trimmed = sanitized.TrimStart();
            if (trimmed.StartsWith("PS ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(">>", StringComparison.Ordinal) ||
                trimmed.StartsWith(">", StringComparison.Ordinal) ||
                trimmed.Contains(ExecStartTokenPrefix, StringComparison.Ordinal) ||
                trimmed.Contains(ExecDoneTokenPrefix, StringComparison.Ordinal) ||
                trimmed.Contains(LocationTokenPrefix, StringComparison.Ordinal) ||
                trimmed.Contains(DispatchDiagnosticTokenPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var fragment in hiddenFragments)
            {
                if (!string.IsNullOrWhiteSpace(fragment) &&
                    (trimmed.Contains(fragment, StringComparison.Ordinal) ||
                     fragment.Contains(trimmed, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        private void ScheduleNoVisibleOutputFeedback(int dispatchGeneration, bool isScript, string displayName, Action<ExecutionOutputRecord> onOutput)
        {
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "No-visible-output feedback monitor scheduled.",
                new Dictionary<string, object?>
                {
                    ["dispatchGeneration"] = dispatchGeneration,
                    ["isScript"] = isScript,
                    ["displayName"] = displayName,
                    ["delayMs"] = NoVisibleOutputFeedbackDelay.TotalMilliseconds
                });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(NoVisibleOutputFeedbackDelay).ConfigureAwait(false);

                    bool shouldNotify;
                    bool commandInProgress;
                    bool meaningfulOutputSeen;
                    bool processRunning;
                    bool startConfirmed;
                    int currentGeneration;
                    int? processId;
                    Process? process;
                    lock (_syncRoot)
                    {
                        process = _process;
                        commandInProgress = _isCommandInProgress;
                        currentGeneration = _commandDispatchGeneration;
                        meaningfulOutputSeen = _currentDispatchVisibleOutputSeen;
                        processRunning = IsProcessRunningNoThrow(process);
                        processId = TryGetProcessId(process);
                        startConfirmed = _currentDispatchStartConfirmed ||
                                         string.IsNullOrEmpty(_pendingStartToken);
                        shouldNotify = TerminalSessionEventPolicy.IsCurrentDispatch(
                                           commandInProgress,
                                           currentGeneration,
                                           dispatchGeneration) &&
                                       !meaningfulOutputSeen &&
                                       processRunning;
                    }

                    var visibleWindowTitle = TryGetMainWindowTitleNoThrow(process);

                    DeveloperDiagnostics.LogDebug(
                        "Terminal",
                        "No-visible-output feedback monitor evaluated command state.",
                        new Dictionary<string, object?>
                        {
                            ["dispatchGeneration"] = dispatchGeneration,
                            ["currentGeneration"] = currentGeneration,
                            ["isScript"] = isScript,
                            ["displayName"] = displayName,
                            ["commandInProgress"] = commandInProgress,
                            ["processId"] = processId,
                            ["processRunning"] = processRunning,
                            ["startConfirmed"] = startConfirmed,
                            ["meaningfulOutputSeen"] = meaningfulOutputSeen,
                            ["visibleWindowTitle"] = visibleWindowTitle,
                            ["shouldNotify"] = shouldNotify
                        });
                    if (!shouldNotify)
                    {
                        return;
                    }

                    var workKind = isScript ? "Script" : "Command";
                    var targetName = GetDisplayNameForStatus(displayName);
                    AppLogger.Warning(
                        "LiveConsole",
                        $"{workKind} '{targetName}' is still tracked as running after {NoVisibleOutputFeedbackDelay.TotalSeconds:0.#} seconds with no meaningful terminal output. DispatchGeneration={dispatchGeneration}, ProcessId={processId?.ToString() ?? "(none)"}, StartConfirmed={startConfirmed}, VisibleWindowTitle='{visibleWindowTitle ?? string.Empty}'.");

                    if (isScript && !string.IsNullOrWhiteSpace(visibleWindowTitle))
                    {
                        PublishLifecycleMessage(
                            onOutput,
                            $"Script '{targetName}' has not written console output yet, but a PowerShell-owned window is open: \"{visibleWindowTitle}\". This usually means the script is running a GUI or waiting on a dialog. Use that window, close it when finished, or use Interrupt if it appears stuck.");
                    }
                    else
                    {
                        PublishLifecycleMessage(
                            onOutput,
                            $"{workKind} '{targetName}' is still running, but no console output has been received yet. This can be normal for GUI scripts, long startup work, or a command waiting for input. Use Interrupt if it appears stuck.");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("LiveConsole", $"No-output feedback watchdog failed. Reason={ex.Message}");
                    DeveloperDiagnostics.LogException(
                        "Terminal",
                        ex,
                        "No-visible-output feedback monitor failed.",
                        new Dictionary<string, object?>
                        {
                            ["dispatchGeneration"] = dispatchGeneration,
                            ["isScript"] = isScript,
                            ["displayName"] = displayName
                        });
                }
            });
        }

        private static void PublishLifecycleMessage(Action<ExecutionOutputRecord> onOutput, string text)
        {
            try
            {
                onOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    text,
                    DateTime.Now));
            }
            catch (Exception ex)
            {
                AppLogger.Error("LiveConsole", "Failed to publish a terminal lifecycle message.", ex);
            }
        }

        private static string GetDisplayNameForStatus(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "Untitled";
            }

            try
            {
                var fileName = Path.GetFileName(displayName);
                return string.IsNullOrWhiteSpace(fileName) ? displayName : fileName;
            }
            catch
            {
                return displayName;
            }
        }

        private bool TryBeginCommandDispatch(
            bool isScript,
            string? snapshotPath,
            out int sessionGeneration,
            out string? failureMessage)
        {
            lock (_syncRoot)
            {
                if (_terminalSessionTeardownInProgress || !IsProcessRunningNoThrow(_process))
                {
                    sessionGeneration = 0;
                    failureMessage = "The PowerShell terminal session is not running.";
                    return false;
                }

                if (_isCommandInProgress)
                {
                    sessionGeneration = 0;
                    failureMessage = "Another terminal operation is already running.";
                    return false;
                }

                _isCommandInProgress = true;
                _currentCommandIsScript = isScript;
                _commandDispatchGeneration++;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = !isScript;
                _currentDispatchStartedUtc = DateTime.UtcNow;
                if (isScript && !string.IsNullOrWhiteSpace(snapshotPath))
                {
                    _pendingSnapshotPaths.Enqueue(snapshotPath);
                }

                sessionGeneration = _terminalSessionGeneration;
                failureMessage = null;
                return true;
            }
        }

        private void AddPendingSnapshotPath(string snapshotPath)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                return;
            }

            lock (_syncRoot)
            {
                _pendingSnapshotPaths.Enqueue(snapshotPath);
            }
        }

        private void CancelPendingCommandDispatch(bool deleteSnapshot)
        {
            List<string> snapshotPaths = new();

            lock (_syncRoot)
            {
                if (_pendingSnapshotPaths.Count > 0)
                {
                    snapshotPaths.AddRange(_pendingSnapshotPaths);
                    _pendingSnapshotPaths.Clear();
                }

                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingLocationToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
                _preStartBufferTruncated = false;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = false;
                _currentDispatchStartedUtc = null;
            }

            if (deleteSnapshot)
            {
                foreach (var snapshotPath in snapshotPaths)
                {
                    TryDeleteSnapshot(snapshotPath);
                }
            }
        }

        private void CompleteCommandExecution(int? observedSessionGeneration = null)
        {
            bool wasScript;
            List<string> snapshotPaths = new();

            lock (_syncRoot)
            {
                if (observedSessionGeneration.HasValue &&
                    !TerminalSessionEventPolicy.IsCurrentSession(
                        _terminalSessionGeneration,
                        observedSessionGeneration.Value,
                        _terminalSessionTeardownInProgress))
                {
                    return;
                }

                if (!_isCommandInProgress)
                {
                    return;
                }

                wasScript = _currentCommandIsScript;
                if (_pendingSnapshotPaths.Count > 0)
                {
                    snapshotPaths.AddRange(_pendingSnapshotPaths);
                    _pendingSnapshotPaths.Clear();
                }

                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingLocationToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
                _preStartBufferTruncated = false;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = false;
                _currentDispatchStartedUtc = null;
            }

            foreach (var snapshotPath in snapshotPaths)
            {
                TryDeleteSnapshot(snapshotPath);
            }

            DeveloperDiagnostics.LogStateTransition(
                "Execution",
                "LiveConsoleCommandCompleted",
                "Running",
                "Idle",
                "Live console command completed and pending execution state was cleared.",
                new Dictionary<string, object?>
                {
                    ["wasScript"] = wasScript,
                    ["deletedSnapshotCount"] = snapshotPaths.Count
                });

            if (wasScript)
            {
                ScriptExecutionCompleted?.Invoke();
            }

            CommandExecutionCompleted?.Invoke();
        }

        private void ResetPendingCommandState(bool deleteSnapshots)
        {
            List<string> snapshotPaths;

            lock (_syncRoot)
            {
                snapshotPaths = new List<string>(_pendingSnapshotPaths);
                _pendingSnapshotPaths.Clear();
                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingLocationToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
                _preStartBufferTruncated = false;
                _currentDispatchVisibleOutputSeen = false;
                _currentDispatchStartConfirmed = false;
                _currentDispatchStartedUtc = null;
            }

            if (!deleteSnapshots)
            {
                return;
            }

            foreach (var snapshotPath in snapshotPaths)
            {
                TryDeleteSnapshot(snapshotPath);
            }
        }

        private static void TryDeleteSnapshot(string? snapshotPath)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                return;
            }

            if (!TryValidateManagedSnapshotPath(snapshotPath, out var normalizedRootDirectory, out var normalizedSnapshotPath))
            {
                return;
            }

            try
            {
                if (File.Exists(normalizedSnapshotPath))
                {
                    File.Delete(normalizedSnapshotPath);
                    AppLogger.Info("LiveConsole", $"Deleted terminal snapshot '{Path.GetFileName(normalizedSnapshotPath)}' from '{normalizedRootDirectory}'.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("LiveConsole", $"Failed to delete terminal snapshot '{normalizedSnapshotPath}'. {ex.Message}");
            }
        }

        private void RegisterHiddenOutputFragment(string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return;
            }

            var normalized = NormalizeHiddenOutputText(commandText);
            if (normalized.Count == 0)
            {
                return;
            }

            lock (_syncRoot)
            {
                _pendingHiddenOutputFragments.AddRange(normalized);
            }
        }

        private string FilterInternalTerminalOutput(
            string raw,
            out bool hasSentinel,
            int? observedSessionGeneration = null)
        {
            if (string.IsNullOrEmpty(raw))
            {
                hasSentinel = false;
                return string.Empty;
            }

            lock (_syncRoot)
            {
                hasSentinel = false;

                if (observedSessionGeneration.HasValue &&
                    !TerminalSessionEventPolicy.IsCurrentSession(
                        _terminalSessionGeneration,
                        observedSessionGeneration.Value,
                        _terminalSessionTeardownInProgress))
                {
                    return string.Empty;
                }

                var commandInProgress = _isCommandInProgress;
                var startToken = _pendingStartToken;
                var completionToken = _pendingCompletionToken;
                var locationToken = _pendingLocationToken;
                if (!commandInProgress && _pendingHiddenOutputFragments.Count == 0 && string.IsNullOrEmpty(_hiddenOutputBuffer))
                {
                    if (!string.IsNullOrEmpty(completionToken) &&
                        raw.Contains(completionToken, StringComparison.Ordinal))
                    {
                        hasSentinel = true;
                        return raw.Replace(completionToken, string.Empty, StringComparison.Ordinal);
                    }
                    return raw;
                }

                if (commandInProgress && !string.IsNullOrEmpty(startToken))
                {
                    AppendBoundedPreStartOutput(raw, startToken);
                }
                else
                {
                    _hiddenOutputBuffer += raw;
                }

                // Script dispatch is intentionally hidden. ConPTY echoes the full
                // submitted command before PowerShell executes it, and that echo can
                // wrap or arrive in fragments without a newline. Buffer everything
                // until PowerShell writes the private start token from inside the
                // command itself, then discard the echo and release only real script
                // output that follows the token.
                if (commandInProgress && !string.IsNullOrEmpty(startToken))
                {
                    var startIndex = _hiddenOutputBuffer.IndexOf(startToken, StringComparison.Ordinal);
                    if (startIndex < 0)
                    {
                        // Keep buffering. This prevents partial leaks such as
                        // "PS Z:\> $__psstudioDone=[" or "try { & 'C:\Users".
                        return string.Empty;
                    }

                    _hiddenOutputBuffer = _hiddenOutputBuffer[(startIndex + startToken.Length)..];
                    _pendingStartToken = null;
                    _currentDispatchStartConfirmed = true;
                    _preStartBufferTruncated = false;
                    AppLogger.Info("LiveConsole", "Script dispatch start token observed in terminal output; hidden command echo was filtered before display.");
                    DeveloperDiagnostics.LogStateTransition(
                        "Terminal",
                        "ScriptDispatchStartConfirmed",
                        "WaitingForStartToken",
                        "ScriptStarted",
                        "Script dispatch start token observed in terminal output.",
                        new Dictionary<string, object?>
                        {
                            ["hiddenBufferLengthAfterToken"] = _hiddenOutputBuffer.Length
                        });
                    // The start token is written only after PowerShell has accepted and begun
                    // executing the hidden dispatch command. Everything before it is
                    // programmatic command echo and was discarded above, so clear any
                    // registered echo fragments. Otherwise the final unterminated
                    // primary prompt can be held as a possible hidden fragment and
                    // cleared at completion, leaving no visible prompt until Enter.
                    _pendingHiddenOutputFragments.Clear();
                }

                // Completion detection must win over hidden-command buffering.
                // If the sentinel is split across the same buffered text as an echoed
                // internal dispatch command, observe and strip it before deciding what
                // to keep. This prevents stale busy state after a script has actually
                // returned to a normal PowerShell prompt.
                if (!string.IsNullOrEmpty(completionToken) &&
                    _hiddenOutputBuffer.Contains(completionToken, StringComparison.Ordinal))
                {
                    hasSentinel = true;
                    _hiddenOutputBuffer = _hiddenOutputBuffer.Replace(completionToken, string.Empty, StringComparison.Ordinal);
                }

                var filtered = new StringBuilder(_hiddenOutputBuffer.Length);
                var keepRemainder = new StringBuilder();

                while (TryReadTerminalSegment(ref _hiddenOutputBuffer, out var segment))
                {
                    var sanitizedSegment = RemoveControlSequences(segment);

                    if (TryConsumeLocationControlFrame(segment, locationToken))
                    {
                        continue;
                    }

                    if (TryLogInternalDispatchDiagnostic(sanitizedSegment))
                    {
                        continue;
                    }

                    if (IsInternalExecutionEcho(sanitizedSegment))
                    {
                        AppLogger.Debug("LiveConsole", $"Filtered internal terminal echo before xterm.js. Segment='{sanitizedSegment}'.");
                        continue;
                    }

                    var matchedIndex = FindHiddenFragmentIndex(sanitizedSegment);
                    if (matchedIndex >= 0)
                    {
                        AppLogger.Debug("LiveConsole", $"Filtered registered internal terminal echo before xterm.js. Fragment='{_pendingHiddenOutputFragments[matchedIndex]}'.");
                        _pendingHiddenOutputFragments.RemoveAt(matchedIndex);
                        continue;
                    }

                    // Long hidden dispatch commands can be echoed by ConPTY in wrapped
                    // chunks. Suppress visible prefixes such as:
                    //   PS Z:\> try { & 'C:\Users
                    // so the user never sees partial app-generated script dispatch text.
                    if (commandInProgress && IsPotentialHiddenFragmentPrefix(sanitizedSegment))
                    {
                        AppLogger.Debug("LiveConsole", $"Filtered wrapped internal terminal echo before xterm.js. Segment='{sanitizedSegment}'.");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(completionToken) &&
                        segment.Contains(completionToken, StringComparison.Ordinal))
                    {
                        hasSentinel = true;
                        segment = segment.Replace(completionToken, string.Empty, StringComparison.Ordinal);
                    }

                    // If removing the sentinel leaves only an echoed internal command,
                    // suppress that entire line.  This prevents visible leftovers like:
                    //     PS Z:\> Write-Host ''
                    if (IsInternalExecutionEcho(RemoveControlSequences(segment)))
                    {
                        AppLogger.Debug("LiveConsole", "Filtered internal terminal echo after sentinel removal.");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(segment))
                    {
                        filtered.Append(segment);
                    }
                }

                // Do not flush a partial line that looks like the beginning of our
                // sentinel command echo.  This prevents partial leaks when terminal
                // output chunks split the hidden command across reads.
                if (_hiddenOutputBuffer.Length > 0)
                {
                    var sanitizedRemainder = RemoveControlSequences(_hiddenOutputBuffer);
                    if (IsPotentialInternalExecutionEchoPrefix(sanitizedRemainder) ||
                        IsPotentialHiddenFragmentPrefix(sanitizedRemainder))
                    {
                        keepRemainder.Append(_hiddenOutputBuffer);
                    }
                    else if (_pendingHiddenOutputFragments.Count == 0 || !commandInProgress)
                    {
                        if (!string.IsNullOrEmpty(completionToken) &&
                            _hiddenOutputBuffer.Contains(completionToken, StringComparison.Ordinal))
                        {
                            hasSentinel = true;
                            var remainder = _hiddenOutputBuffer.Replace(completionToken, string.Empty, StringComparison.Ordinal);
                            if (!IsInternalExecutionEcho(RemoveControlSequences(remainder)))
                            {
                                filtered.Append(remainder);
                            }
                        }
                        else
                        {
                            filtered.Append(_hiddenOutputBuffer);
                        }
                    }
                    else
                    {
                        // A pending hidden fragment means the current remainder may still
                        // be an echoed internal dispatch command that has not reached a
                        // newline yet. Do not leak a "safe" prefix to xterm.js; that was
                        // the source of partial visible commands and apparent freezes.
                        keepRemainder.Append(_hiddenOutputBuffer);
                    }
                }

                _hiddenOutputBuffer = keepRemainder.ToString();
                return filtered.ToString();
            }
        }

        private void AppendBoundedPreStartOutput(string raw, string startToken)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            var combined = _hiddenOutputBuffer + raw;
            var startIndex = combined.IndexOf(startToken, StringComparison.Ordinal);
            if (startIndex >= 0)
            {
                // Preserve the complete acknowledged payload for immediate filtering,
                // even when one unusually large reader chunk contains both the start
                // frame and more than the pre-start cap of genuine script output.
                _hiddenOutputBuffer = combined[startIndex..];
                return;
            }

            if (combined.Length <= MaxPreStartBufferCharacters)
            {
                _hiddenOutputBuffer = combined;
                return;
            }

            _hiddenOutputBuffer = combined[^MaxPreStartBufferCharacters..];
            _preStartBufferTruncated = true;
        }

        private bool TryConsumeLocationControlFrame(string segment, string? locationToken)
        {
            if (string.IsNullOrEmpty(segment) || string.IsNullOrEmpty(locationToken))
            {
                return false;
            }

            var tokenIndex = segment.IndexOf(locationToken, StringComparison.Ordinal);
            if (tokenIndex < 0)
            {
                return false;
            }

            var encodedLocation = segment[(tokenIndex + locationToken.Length)..].Trim();
            try
            {
                var decodedBytes = Convert.FromBase64String(encodedLocation);
                var location = Encoding.UTF8.GetString(decodedBytes).Trim();
                if (!string.IsNullOrWhiteSpace(location))
                {
                    CurrentWorkingDirectory = location;
                    DeveloperDiagnostics.LogInfo(
                        "Terminal",
                        "Confirmed terminal working directory from an explicit dispatch control frame.",
                        new Dictionary<string, object?>
                        {
                            ["locationLength"] = location.Length,
                            ["dispatchGeneration"] = _commandDispatchGeneration,
                            ["contentOmitted"] = true
                        });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug(
                    "LiveConsole",
                    $"Ignored an invalid terminal location control frame. ExceptionType={ex.GetType().Name}.");
            }

            return true;
        }

        private static bool IsInternalExecutionEcho(string sanitizedSegment)
        {
            if (string.IsNullOrWhiteSpace(sanitizedSegment))
            {
                return false;
            }

            var normalized = sanitizedSegment.Trim();

            return normalized.Contains(ExecDoneTokenPrefix, StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("Write-Host ''", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("Write-Host \"\"", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPotentialInternalExecutionEchoPrefix(string sanitizedSegment)
        {
            if (string.IsNullOrWhiteSpace(sanitizedSegment))
            {
                return false;
            }

            var normalized = sanitizedSegment.Trim();

            return normalized.Contains(ExecDoneTokenPrefix, StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("Write-Host ''", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("Write-Host \"\"", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith("##PSSTUDIO_EXEC_DONE", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPotentialHiddenFragmentPrefix(string sanitizedSegment)
        {
            if (string.IsNullOrWhiteSpace(sanitizedSegment))
            {
                return false;
            }

            var trimmed = sanitizedSegment.Trim();
            var normalized = TrimPromptPrefix(trimmed);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var hadPromptPrefix = !string.Equals(trimmed, normalized, StringComparison.Ordinal);

            for (var index = 0; index < _pendingHiddenOutputFragments.Count; index++)
            {
                var fragment = _pendingHiddenOutputFragments[index];
                if (string.IsNullOrWhiteSpace(fragment))
                {
                    continue;
                }

                if (fragment.StartsWith(normalized, StringComparison.Ordinal) ||
                    normalized.StartsWith(fragment, StringComparison.Ordinal))
                {
                    return true;
                }

                var commonPrefixLength = GetCommonPrefixLength(normalized, fragment);
                if (commonPrefixLength >= (hadPromptPrefix ? 2 : 4))
                {
                    return true;
                }
            }

            return false;
        }

        private static int GetCommonPrefixLength(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return 0;
            }

            var maxLength = Math.Min(left.Length, right.Length);
            var index = 0;
            while (index < maxLength && left[index] == right[index])
            {
                index++;
            }

            return index;
        }

        private static string TrimPromptPrefix(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var trimmed = text.TrimStart();
            if (trimmed.StartsWith(">>", StringComparison.Ordinal))
            {
                return trimmed[2..].TrimStart();
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                return trimmed[1..].TrimStart();
            }

            var promptMatch = Regex.Match(trimmed, @"^PS\s+.+?>\s*", RegexOptions.Singleline);
            if (promptMatch.Success)
            {
                return trimmed[promptMatch.Length..].TrimStart();
            }

            return trimmed;
        }

        private int FindHiddenFragmentIndex(string sanitizedSegment)
        {
            if (string.IsNullOrWhiteSpace(sanitizedSegment) || _pendingHiddenOutputFragments.Count == 0)
            {
                return -1;
            }

            for (var index = 0; index < _pendingHiddenOutputFragments.Count; index++)
            {
                if (sanitizedSegment.Contains(_pendingHiddenOutputFragments[index], StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool TryReadTerminalSegment(ref string buffer, out string segment)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                if (buffer[index] != '\r' && buffer[index] != '\n')
                {
                    continue;
                }

                var terminatorLength = 1;
                if (buffer[index] == '\r' && index + 1 < buffer.Length && buffer[index + 1] == '\n')
                {
                    terminatorLength = 2;
                }

                var totalLength = index + terminatorLength;
                segment = buffer[..totalLength];
                buffer = buffer[totalLength..];
                return true;
            }

            segment = string.Empty;
            return false;
        }

        private static List<string> NormalizeHiddenOutputText(string text)
        {
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace('\r', '\n');
            var lines = new List<string>();
            foreach (var line in normalized.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    lines.Add(trimmed);
                }
            }

            return lines;
        }

        private static string QuoteCommandArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static string RemoveControlSequences(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var cleaned = OscRegex.Replace(text, string.Empty);
            cleaned = AnsiRegex.Replace(cleaned, string.Empty);
            cleaned = cleaned.Replace("\r", string.Empty, StringComparison.Ordinal)
                             .Replace("\n", string.Empty, StringComparison.Ordinal);
            return cleaned.Trim();
        }

        private void UpdateCurrentDirectoryFromPrompt(string text)
        {
            UpdateCurrentDirectoryFromPromptCore(text, observedSessionGeneration: null);
        }

        private void UpdateCurrentDirectoryFromPromptCore(
            string text,
            int? observedSessionGeneration)
        {
            var matches = PromptRegex.Matches(text);
            if (matches.Count == 0)
            {
                return;
            }

            var lastMatch = matches[matches.Count - 1];
            var path = lastMatch.Groups["path"].Value.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (_syncRoot)
            {
                if (observedSessionGeneration.HasValue &&
                    !TerminalSessionEventPolicy.IsCurrentSession(
                        _terminalSessionGeneration,
                        observedSessionGeneration.Value,
                        _terminalSessionTeardownInProgress))
                {
                    return;
                }

                _lastPromptHeuristicDirectory = path;
            }

            AppLogger.Debug(
                "LiveConsole",
                $"Observed a visible PowerShell prompt heuristic. PathLength={path.Length}, AuthoritativeStateChanged=False, ContentOmitted=True.");
            DeveloperDiagnostics.LogDebug(
                "Terminal",
                "Visible prompt text was observed as a non-authoritative heuristic.",
                new Dictionary<string, object?>
                {
                    ["pathLength"] = path.Length,
                    ["authoritativeStateChanged"] = false,
                    ["contentOmitted"] = true
                });
        }

        private async Task WriteTerminalInputAsync(
            string text,
            int sessionGeneration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppLogger.Debug("LiveConsole", $"Queueing terminal input. SessionGeneration={sessionGeneration}, Length={text.Length}, ContentOmitted=True.");
            var payload = NormalizeTerminalInputForActiveTransport(text);

            try
            {
                await _terminalInputRouter.WriteAsync(
                    sessionGeneration,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(
                    "LiveConsole",
                    $"Terminal input write failed. SessionGeneration={sessionGeneration}, Length={text.Length}, ExceptionType={ex.GetType().Name}, ContentOmitted=True.");
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Serialized terminal input write failed.",
                    new Dictionary<string, object?>
                    {
                        ["sessionGeneration"] = sessionGeneration,
                        ["inputLength"] = text.Length,
                        ["contentOmitted"] = true
                    });
                throw;
            }
        }

        private string NormalizeTerminalInputForActiveTransport(string text)
        {
            bool redirectedTransport;
            lock (_syncRoot)
            {
                redirectedTransport = _redirectedTerminalTransportActive;
            }

            if (!redirectedTransport || (!text.Contains('\r') && !text.Contains('\n')))
            {
                return text;
            }

            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        private bool TryGetWritableSessionGeneration(out int sessionGeneration)
        {
            lock (_syncRoot)
            {
                if (_terminalSessionTeardownInProgress ||
                    _terminalWriter is null ||
                    !IsProcessRunningNoThrow(_process))
                {
                    sessionGeneration = 0;
                    return false;
                }

                sessionGeneration = _terminalSessionGeneration;
                return true;
            }
        }

        private static bool IsClearScreenCommand(string commandText)
        {
            var trimmed = commandText.Trim();
            return trimmed.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("clear-host", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateTerminalSize(int width, int height)
        {
            // Consolas 14pt at 96 DPI is approximately 8.4 px wide and 19 px tall per
            // character.  Dividing by 8 would over-count columns and produce tables
            // slightly wider than the visible area.  Using 9 for columns and 19 for rows
            // gives a conservative estimate that keeps output within the display bounds.
            _terminalColumns = Math.Max(40, width / 9);
            _terminalRows = Math.Max(12, height / 19);
        }

        /// <inheritdoc />
        public void ResizeConsole(int cols, int rows)
        {
            ResizeRequest resizeRequest;
            lock (_syncRoot)
            {
                _terminalColumns = Math.Max(1, cols);
                _terminalRows    = Math.Max(1, rows);
                resizeRequest = new ResizeRequest(
                    _pseudoConsoleHandle,
                    _terminalSessionGeneration,
                    _terminalColumns,
                    _terminalRows,
                    _process);
            }

            if (resizeRequest.PseudoConsole != IntPtr.Zero)
            {
                var hResult = ResizePseudoConsole(
                    resizeRequest.PseudoConsole,
                    new COORD((short)resizeRequest.Columns, (short)resizeRequest.Rows));
                ObserveResizeResult("ResizeConsole", resizeRequest, hResult);
            }
        }

        private void ObserveResizeResult(string operation, ResizeRequest resizeRequest, int hResult)
        {
            bool shouldLog;
            bool rendererHandlePresent;
            bool teardownInProgress;
            bool processSessionRunning;

            lock (_syncRoot)
            {
                if (hResult == 0)
                {
                    if (_terminalSessionGeneration == resizeRequest.SessionGeneration)
                    {
                        _resizeFailureEpisode.RecordResult(resizeRequest.SessionGeneration, hResult);
                    }

                    return;
                }

                if (!IsCurrentResizeRequestNoLock(resizeRequest))
                {
                    return;
                }

                shouldLog = _resizeFailureEpisode.RecordResult(resizeRequest.SessionGeneration, hResult);
                rendererHandlePresent = _pseudoConsoleHandle != IntPtr.Zero;
                teardownInProgress = _terminalSessionTeardownInProgress;
                processSessionRunning = IsProcessRunningNoThrow(_process);
            }

            if (!shouldLog)
            {
                return;
            }

            var hResultHex = $"0x{hResult:X8}";
            var message = $"ConPTY resize failed. Operation={operation}, Columns={resizeRequest.Columns}, Rows={resizeRequest.Rows}, HRESULT={hResultHex}, SessionGeneration={resizeRequest.SessionGeneration}.";
            var metadata = new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["effectiveColumns"] = resizeRequest.Columns,
                ["effectiveRows"] = resizeRequest.Rows,
                ["hResult"] = hResult,
                ["hResultHex"] = hResultHex,
                ["sessionGeneration"] = resizeRequest.SessionGeneration,
                ["rendererHandlePresent"] = rendererHandlePresent,
                ["teardownInProgress"] = teardownInProgress,
                ["processSessionRunning"] = processSessionRunning,
                ["contentOmitted"] = true
            };

            AppLogger.Warning("LiveConsole", message);
            DeveloperDiagnostics.LogWarning("LiveConsole", "ConPTY resize failed.", metadata);
        }

        private bool IsCurrentResizeRequestNoLock(ResizeRequest resizeRequest)
        {
            return !_terminalSessionTeardownInProgress &&
                   _terminalSessionGeneration == resizeRequest.SessionGeneration &&
                   _pseudoConsoleHandle == resizeRequest.PseudoConsole &&
                   ReferenceEquals(_process, resizeRequest.Process) &&
                   resizeRequest.Process is not null &&
                   IsProcessRunningNoThrow(resizeRequest.Process);
        }

        /// <inheritdoc />
        public async Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            if (!TryGetWritableSessionGeneration(out var sessionGeneration))
            {
                AppLogger.Debug("LiveConsole", $"Rejecting raw terminal input because the session is not writable. Length={data.Length}, ContentOmitted=True.");
                throw new InvalidOperationException("The PowerShell terminal session is stopping or is not running.");
            }

            AppLogger.Debug("LiveConsole", $"Raw terminal input received. Length={data.Length}, ContentOmitted=True.");
            ObserveManualInteractiveInput(data, sessionGeneration);
            await WriteTerminalInputAsync(data, sessionGeneration, cancellationToken).ConfigureAwait(false);
        }

        private void ObserveManualInteractiveInput(string data, int sessionGeneration)
        {
            if (string.IsNullOrEmpty(data) || !data.Contains(TerminalEnterSequence, StringComparison.Ordinal))
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_terminalSessionTeardownInProgress ||
                    _terminalSessionGeneration != sessionGeneration ||
                    !IsProcessRunningNoThrow(_process))
                {
                    return;
                }
            }

            AppLogger.Debug(
                "LiveConsole",
                "Manual terminal Enter observed without creating authoritative command state; Ctrl+C remains available through direct terminal input.");
        }

        private async Task<InterruptRecoveryWaitOutcome> WaitForInterruptRecoveryAsync(
            Process expectedProcess,
            int expectedSessionGeneration,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                bool targetIsCurrent;
                bool commandInProgress;
                bool processRunning;
                lock (_syncRoot)
                {
                    targetIsCurrent = _terminalSessionGeneration == expectedSessionGeneration &&
                                      !_terminalSessionTeardownInProgress &&
                                      ReferenceEquals(_process, expectedProcess);
                    commandInProgress = _isCommandInProgress;
                    processRunning = IsProcessRunningNoThrow(expectedProcess);
                }

                if (!targetIsCurrent)
                {
                    return InterruptRecoveryWaitOutcome.Superseded;
                }

                var hasVisibleOwnedWindow = !string.IsNullOrWhiteSpace(TryGetMainWindowTitleNoThrow(expectedProcess));
                if (TerminalSessionEventPolicy.IsInterruptRecoveryComplete(
                        commandInProgress,
                        processRunning,
                        hasVisibleOwnedWindow))
                {
                    return InterruptRecoveryWaitOutcome.Recovered;
                }

                var remaining = timeout - stopwatch.Elapsed;
                var delay = remaining < InterruptRecoveryPollInterval
                    ? remaining
                    : InterruptRecoveryPollInterval;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            return InterruptRecoveryWaitOutcome.TimedOut;
        }

        private async Task<(bool TargetWasCurrent, bool Succeeded)> StopInterruptTargetAsync(
            Process expectedProcess,
            int expectedSessionGeneration,
            Action<ExecutionOutputRecord> onOutput,
            CancellationToken cancellationToken)
        {
            if (!await _sessionLifecycleGate
                    .WaitAsync(InterruptLifecycleGateTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                AppLogger.Warning("LiveConsole", $"Interrupt escalation could not enter the terminal lifecycle gate within {InterruptLifecycleGateTimeout.TotalSeconds:0.#} seconds. SessionGeneration={expectedSessionGeneration}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "InterruptEscalation",
                    "Interrupt escalation could not enter the terminal lifecycle gate within its bounded timeout.",
                    "LifecycleGateTimedOut",
                    new Dictionary<string, object?>
                    {
                        ["sessionGeneration"] = expectedSessionGeneration,
                        ["timeoutMs"] = InterruptLifecycleGateTimeout.TotalMilliseconds
                    });
                return (true, false);
            }

            try
            {
                lock (_syncRoot)
                {
                    if (_terminalSessionGeneration != expectedSessionGeneration ||
                        _terminalSessionTeardownInProgress ||
                        !ReferenceEquals(_process, expectedProcess))
                    {
                        return (false, false);
                    }
                }

                return (true, await StopConsoleCoreAsync(onOutput, "interrupt-escalation").ConfigureAwait(false));
            }
            finally
            {
                _sessionLifecycleGate.Release();
            }
        }

        private static int? TryGetProcessId(Process? process)
        {
            try
            {
                if (process is null)
                {
                    return null;
                }

                return process.Id;
            }
            catch
            {
                return null;
            }
        }


        private static int? TryGetExitCode(Process? process)
        {
            try
            {
                if (process is null || !process.HasExited)
                {
                    return null;
                }

                return process.ExitCode;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeWorkingDirectory(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }

            var currentDirectory = Environment.CurrentDirectory;
            if (Directory.Exists(currentDirectory))
            {
                return currentDirectory;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public static void CleanupStaleExecutionSnapshots()
        {
            try
            {
                var rootDirectory = GetSnapshotRootDirectory(createIfMissing: false);
                if (!Directory.Exists(rootDirectory))
                {
                    return;
                }

                AppLogger.Info("LiveConsole", $"Cleaning stale terminal snapshots from '{rootDirectory}'.");
                foreach (var file in Directory.EnumerateFiles(rootDirectory, "*.ps1", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (!TryValidateManagedSnapshotPath(file, out _, out var normalizedSnapshotPath))
                        {
                            continue;
                        }

                        var fileName = Path.GetFileName(file);
                        if (!IsManagedSnapshotFileName(fileName))
                        {
                            continue;
                        }

                        File.Delete(normalizedSnapshotPath);
                        AppLogger.Info("LiveConsole", $"Deleted stale terminal snapshot '{Path.GetFileName(normalizedSnapshotPath)}'.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("LiveConsole", $"Failed to delete stale terminal snapshot '{file}'. {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("LiveConsole", $"Stale terminal snapshot cleanup failed. {ex.Message}");
            }
        }

        private static (string Path, bool DeleteAfterRun) CreateExecutionTarget(string documentDisplayName, string scriptContent, bool executeInCurrentScope)
        {
            var normalizedContent = scriptContent ?? string.Empty;

            // Full editor Run for a clean, saved .ps1 should execute the real file in
            // place instead of a temp copy.  Visual Studio-generated installer scripts,
            // modules, and many real-world scripts depend on $PSScriptRoot /
            // $MyInvocation.MyCommand.Path to locate sibling resources such as .psd1,
            // .psm1, config, or template folders.  Snapshot execution is still used
            // for unsaved/dirty content and for Run Selection so current editor text is
            // never lost.
            if (!executeInCurrentScope &&
                TryResolveSavedScriptPath(documentDisplayName, out var savedScriptPath) &&
                TryReadText(savedScriptPath, out var savedContent) &&
                string.Equals(savedContent, normalizedContent, StringComparison.Ordinal))
            {
                AppLogger.Info("LiveConsole", $"Executing saved script in place so script-relative resources resolve correctly. ScriptPath={savedScriptPath}");
                return (savedScriptPath, false);
            }

            var snapshotPath = CreateExecutionSnapshot(documentDisplayName, normalizedContent);
            return (snapshotPath, true);
        }

        private static bool TryResolveSavedScriptPath(string candidatePath, out string savedScriptPath)
        {
            savedScriptPath = string.Empty;

            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidatePath);
                if (!File.Exists(fullPath))
                {
                    return false;
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                savedScriptPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadText(string filePath, out string content)
        {
            content = string.Empty;

            try
            {
                content = File.ReadAllText(filePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("LiveConsole", $"Unable to compare saved script content before run. Falling back to temp snapshot. ScriptPath={filePath}, Error={ex.Message}");
                return false;
            }
        }

        private static string CreateExecutionSnapshot(string documentDisplayName, string scriptContent)
        {
            var rootDirectory = GetSnapshotRootDirectory(createIfMissing: true);

            var fileName = $"{ScriptSnapshotFilePrefix}{Guid.NewGuid():N}.ps1";
            var fullPath = Path.Combine(rootDirectory, fileName);
            File.WriteAllText(fullPath, scriptContent ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fullPath;
        }

        private static string CreateDispatchInstructionSnapshot(
            string scriptPath,
            string startToken,
            string completionToken,
            string locationToken,
            bool executeInCurrentScope)
        {
            var rootDirectory = GetSnapshotRootDirectory(createIfMissing: true);

            var fileName = $"{DispatchInstructionFilePrefix}{Guid.NewGuid():N}.ps1";
            var fullPath = Path.Combine(rootDirectory, fileName);
            var lines = new[]
            {
                scriptPath,
                startToken,
                completionToken,
                executeInCurrentScope ? "true" : "false",
                locationToken
            };

            File.WriteAllLines(fullPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fullPath;
        }

        private static string CreateDispatchHelperSnapshot()
        {
            var rootDirectory = GetSnapshotRootDirectory(createIfMissing: true);
            var fileName = $"{DispatchHelperFilePrefix}{Guid.NewGuid():N}.ps1";
            var fullPath = Path.Combine(rootDirectory, fileName);
            var helperLines = new[]
            {
                "param([Parameter(Mandatory=$true)][string]$InstructionPath)",
                "$__pssdDone = $null",
                "$__pssdLocation = $null",
                "try {",
                "  $__pssdLines = [System.IO.File]::ReadAllLines($InstructionPath, [System.Text.Encoding]::UTF8)",
                "  if ($__pssdLines.Length -lt 5) { throw 'PS7 ScriptDesk dispatch instruction is incomplete.' }",
                "  $__pssdPath = $__pssdLines[0]",
                "  $__pssdStart = $__pssdLines[1]",
                "  $__pssdDone = $__pssdLines[2]",
                "  $__pssdCurrentScope = [System.Boolean]::Parse($__pssdLines[3])",
                "  $__pssdLocation = $__pssdLines[4]",
                "  [Console]::Out.WriteLine($__pssdStart)",
                "  [Console]::Out.WriteLine('##PSSTUDIO_DISPATCH_DIAG## begin pid=' + $PID + ' apartment=' + [System.Threading.Thread]::CurrentThread.GetApartmentState())",
                "  try { if ($__pssdCurrentScope) { . $__pssdPath } else { & $__pssdPath } }",
                "  catch { [Console]::Error.WriteLine('PS7 ScriptDesk: Script threw a terminating exception: ' + $_.Exception.Message); throw }",
                "  finally {",
                "    [Console]::Out.WriteLine('##PSSTUDIO_DISPATCH_DIAG## finally pid=' + $PID)",
                "    try {",
                "      $__pssdProviderPath = (Get-Location).ProviderPath",
                "      $__pssdEncodedLocation = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($__pssdProviderPath))",
                "      [Console]::Out.WriteLine($__pssdLocation + $__pssdEncodedLocation)",
                "    } catch { }",
                "    [Console]::Out.WriteLine($__pssdDone)",
                "  }",
                "} catch {",
                "  if ([string]::IsNullOrEmpty($__pssdDone)) { [Console]::Error.WriteLine('PS7 ScriptDesk dispatch helper failed before execution started: ' + $_.Exception.Message) } else { throw }",
                "} finally {",
                "  Remove-Variable -Name __pssdLines,__pssdPath,__pssdStart,__pssdDone,__pssdCurrentScope,__pssdLocation,__pssdProviderPath,__pssdEncodedLocation -ErrorAction SilentlyContinue",
                "}"
            };

            File.WriteAllLines(
                fullPath,
                helperLines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fullPath;
        }

        private static string CreateShortDispatchSnapshot(string scriptContent)
        {
            var rootDirectory = GetSnapshotRootDirectory(createIfMissing: true);

            // This wrapper path is the only file name typed into the interactive
            // PowerShell prompt. Keep it deliberately short so PSReadLine/ConPTY
            // does not wrap the hidden command echo and leave orphan continuation
            // prompts such as ">>" after execution. The user script snapshot can
            // keep its descriptive long name because it is referenced only inside
            // this wrapper file.
            var fileName = $"{DispatchSnapshotFilePrefix}{Guid.NewGuid():N}.ps1";
            var fullPath = Path.Combine(rootDirectory, fileName);
            File.WriteAllText(fullPath, scriptContent ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fullPath;
        }

        private static string GetSnapshotRootDirectory(bool createIfMissing)
        {
            if (!AppTemporaryStorage.TryGetManagedRootDirectory("TerminalSnapshots", createIfMissing, out var rootDirectory, out var failureReason))
            {
                throw new IOException($"Terminal snapshot storage is unavailable. {failureReason}");
            }

            return rootDirectory;
        }

        private static bool IsManagedSnapshotFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return fileName.StartsWith(TerminalSnapshotFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith(ScriptSnapshotFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith(DispatchSnapshotFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith(DispatchInstructionFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith(DispatchHelperFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                   LegacySnapshotFileNamePattern.IsMatch(fileName);
        }

        private static bool TryValidateManagedSnapshotPath(string snapshotPath, out string normalizedRootDirectory, out string normalizedSnapshotPath)
        {
            normalizedRootDirectory = string.Empty;
            normalizedSnapshotPath = string.Empty;

            try
            {
                var rootDirectory = GetSnapshotRootDirectory(createIfMissing: false);
                if (!AppTemporaryStorage.TryValidateManagedPath(rootDirectory, snapshotPath, out normalizedRootDirectory, out normalizedSnapshotPath, out var failureReason))
                {
                    AppLogger.Warning("LiveConsole", $"Skipped terminal snapshot deletion outside the managed temp root. Path='{snapshotPath}'. {failureReason}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("LiveConsole", $"Skipped terminal snapshot deletion because the managed temp root could not be resolved. Path='{snapshotPath}'. {ex.Message}");
                return false;
            }
        }

        public const string TerminalClearToken = "__PSSTUDIO_CLEAR_TERMINAL__";

        private const int HANDLE_FLAG_INHERIT = 0x00000001;
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

        private enum InterruptRecoveryWaitOutcome
        {
            Recovered,
            TimedOut,
            Superseded
        }

        private readonly struct ResizeRequest
        {
            public ResizeRequest(
                IntPtr pseudoConsole,
                int sessionGeneration,
                int columns,
                int rows,
                Process? process)
            {
                PseudoConsole = pseudoConsole;
                SessionGeneration = sessionGeneration;
                Columns = columns;
                Rows = rows;
                Process = process;
            }

            public IntPtr PseudoConsole { get; }

            public int SessionGeneration { get; }

            public int Columns { get; }

            public int Rows { get; }

            public Process? Process { get; }
        }

        private sealed class ResizeFailureEpisode
        {
            private int _sessionGeneration = -1;
            private bool _failureEpisodeActive;
            private int _firstHResult;
            private bool _alternateHResultRecorded;
            private int _alternateHResult;

            public bool RecordResult(int sessionGeneration, int hResult)
            {
                if (_sessionGeneration != sessionGeneration)
                {
                    ResetForSession(sessionGeneration);
                }

                if (hResult == 0)
                {
                    _failureEpisodeActive = false;
                    _alternateHResultRecorded = false;
                    return false;
                }

                if (!_failureEpisodeActive)
                {
                    _failureEpisodeActive = true;
                    _firstHResult = hResult;
                    return true;
                }

                if (hResult == _firstHResult ||
                    (_alternateHResultRecorded && hResult == _alternateHResult))
                {
                    return false;
                }

                if (_alternateHResultRecorded)
                {
                    return false;
                }

                _alternateHResultRecorded = true;
                _alternateHResult = hResult;
                return true;
            }

            public void ResetForSession(int sessionGeneration)
            {
                _sessionGeneration = sessionGeneration;
                _failureEpisodeActive = false;
                _firstHResult = 0;
                _alternateHResultRecorded = false;
                _alternateHResult = 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public COORD(short x, short y)
            {
                X = x;
                Y = y;
            }

            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
    }
}
