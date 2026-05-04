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
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.PowerShell.Services
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
        private const string TerminalSnapshotFilePrefix = "psstudio-terminal-";
        private const string ScriptSnapshotFilePrefix = "pss-";
        private const string DispatchSnapshotFilePrefix = "psd-";
        private const string DispatchInstructionFilePrefix = "psi-";
        // Interactive terminals submit Enter as carriage return (\r). Do not send CRLF into ConPTY/PSReadLine.
        private const string TerminalEnterSequence = "\r";

        private readonly object _syncRoot = new();
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
        private string? _pendingStartToken;
        private string? _pendingCompletionToken;
        private readonly Queue<string> _pendingSnapshotPaths = new();
        private readonly List<string> _pendingHiddenOutputFragments = new();
        private string _hiddenOutputBuffer = string.Empty;

        public bool IsSessionRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return _process is not null && !_process.HasExited;
                }
            }
        }

        public bool IsCommandInProgress
        {
            get
            {
                lock (_syncRoot)
                {
                    return _isCommandInProgress;
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
        public event Action<string>? RawOutputReceived;

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
            int columns;
            int rows;
            IntPtr pseudoConsole;

            lock (_syncRoot)
            {
                UpdateTerminalSize(width, height);
                columns = _terminalColumns;
                rows = _terminalRows;
                pseudoConsole = _pseudoConsoleHandle;
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                ResizePseudoConsole(pseudoConsole, new COORD((short)columns, (short)rows));
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

            bool shouldRestart;
            lock (_syncRoot)
            {
                shouldRestart = _process is null ||
                                _process.HasExited ||
                                ActiveRuntime is null ||
                                !string.Equals(ActiveRuntime.ExecutablePath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }

            if (!shouldRestart)
            {
                return;
            }

            await StopConsoleAsync(onOutput).ConfigureAwait(false);
            CleanupStaleExecutionSnapshots();

            var workingDirectory = NormalizeWorkingDirectory(startupWorkingDirectory);

            try
            {
                StartPseudoConsoleSession(runtime, workingDirectory, onOutput);
                AppLogger.Info("LiveConsole", $"ConPTY terminal session started with {runtime.DisplayName}; WorkingDirectory={workingDirectory}");
                onOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    $"ConPTY terminal session started with {runtime.DisplayName}.",
                    DateTime.Now));
            }
            catch (Exception ex)
            {
                AppLogger.Warning("LiveConsole", $"ConPTY startup failed for {runtime.DisplayName}; falling back to redirected terminal mode. Error={ex.Message}");
                onOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    $"ConPTY startup failed ({ex.Message}). Falling back to redirected terminal mode.",
                    DateTime.Now));

                StartRedirectedSession(runtime, workingDirectory, onOutput);
            }

            lock (_syncRoot)
            {
                ActiveRuntime = runtime;
                CurrentWorkingDirectory = workingDirectory;
            }

            await Task.CompletedTask.ConfigureAwait(false);
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

            if (!TryBeginCommandDispatch(isScript: false, snapshotPath: null, out var dispatchFailure))
            {
                throw new InvalidOperationException(dispatchFailure ?? "Another terminal operation is already running.");
            }

            var startedAt = DateTime.Now;
            AppLogger.Info("LiveConsole", $"Sending visible editor command to the live ConPTY terminal. CommandLength={commandText.Length}.");

            try
            {
                await WriteTerminalInputAsync(commandText + TerminalEnterSequence, cancellationToken).ConfigureAwait(false);

                // Option 3 behavior: editor commands are honestly sent to the real
                // interactive terminal. There is no hidden sentinel or output filter.
                // Mark dispatch complete as soon as the command is successfully sent
                // so the app never waits on fragile prompt/sentinel detection.
                CompleteCommandExecution();

                return new LiveConsoleCommandResult(
                    "Console command",
                    wasStopped: false,
                    CurrentWorkingDirectory,
                    startedAt,
                    DateTime.Now);
            }
            catch
            {
                CancelPendingCommandDispatch(deleteSnapshot: false);
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
            var instructionSnapshotPath = CreateDispatchInstructionSnapshot(scriptSnapshotPath, startToken, completionToken, executeInCurrentScope);

            if (!TryBeginCommandDispatch(isScript: true, snapshotPath: executionTarget.DeleteAfterRun ? scriptSnapshotPath : null, out var dispatchFailure))
            {
                if (executionTarget.DeleteAfterRun)
                {
                    TryDeleteSnapshot(scriptSnapshotPath);
                }

                TryDeleteSnapshot(instructionSnapshotPath);
                throw new InvalidOperationException(dispatchFailure ?? "Another terminal operation is already running.");
            }

            AddPendingSnapshotPath(instructionSnapshotPath);
            SetPendingExecutionTokens(startToken, completionToken);
            var dispatchCommand = BuildScriptDispatchCommand(instructionSnapshotPath, executeInCurrentScope);
            var scriptCommand = dispatchCommand + TerminalEnterSequence;
            RegisterHiddenOutputFragment(dispatchCommand);
            var scriptSnapshotExists = File.Exists(scriptSnapshotPath);
            var instructionSnapshotExists = File.Exists(instructionSnapshotPath);
            AppLogger.Info(
                "LiveConsole",
                $"Dispatching editor script to the live ConPTY terminal via preloaded session helper and instruction snapshot. ScriptPath={scriptSnapshotPath}, ScriptPathExists={scriptSnapshotExists}, DeleteScriptAfterRun={executionTarget.DeleteAfterRun}, InstructionSnapshotPath={instructionSnapshotPath}, InstructionSnapshotExists={instructionSnapshotExists}, ScriptLength={scriptContent?.Length ?? 0}, ExecuteInCurrentScope={executeInCurrentScope}, CommandLength={scriptCommand.Length}, EndsWithEnter={scriptCommand.EndsWith(TerminalEnterSequence, StringComparison.Ordinal)}.");
            AppLogger.Debug("LiveConsole", $"Dispatch command: {FormatDispatchCommandForLog(scriptCommand)}");

            try
            {
                AppLogger.Debug("LiveConsole", $"Sending helper dispatch command to terminal stdin. ScriptSnapshotPath={scriptSnapshotPath}, InstructionSnapshotPath={instructionSnapshotPath}");
                await WriteTerminalInputAsync(scriptCommand, cancellationToken).ConfigureAwait(false);

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

        private static string BuildScriptDispatchCommand(string instructionSnapshotPath, bool executeInCurrentScope)
        {
            var instructionFileName = Path.GetFileName(instructionSnapshotPath);
            var quotedInstructionFileName = QuotePowerShellSingleQuotedString(instructionFileName);

            // Keep the text submitted through the interactive terminal extremely
            // small and single-line. Per-run script path, scope mode, and private
            // tokens live in an instruction snapshot read by the preloaded session
            // helper. That avoids long command echoes and prevents encoded tokens
            // from ever being typed into PSReadLine.
            var invocationOperator = executeInCurrentScope ? "." : "&";
            return $"{invocationOperator} $__psstudioRun {quotedInstructionFileName}";
        }

        private static string BuildTerminalStartupCommand()
        {
            var quotedSnapshotRoot = QuotePowerShellSingleQuotedString(GetSnapshotRootDirectory(createIfMissing: true));
            var helperScriptBlock = string.Join(
                " ",
                "{",
                "param([string]$i)",
                "$__i=Join-Path $global:__psstudioSnapshotRoot $i;",
                "$__l=[System.IO.File]::ReadAllLines($__i,[System.Text.Encoding]::UTF8);",
                "if ($__l.Length -lt 4) { throw 'PS7 ScriptDesk dispatch instruction is incomplete.' }",
                "$__p=$__l[0];",
                "if (-not [System.IO.Path]::IsPathRooted($__p)) { $__p=Join-Path $global:__psstudioSnapshotRoot $__p }",
                "$__s=$__l[1];",
                "$__d=$__l[2];",
                "$__c=[System.Boolean]::Parse($__l[3]);",
                "[Console]::Out.WriteLine($__s);",
                "try { if ($__c) { . $__p } else { & $__p } }",
                "finally { [Console]::Out.WriteLine($__d); Remove-Variable -Name __i,__l,__p,__s,__d,__c -ErrorAction SilentlyContinue }",
                "}");

            return string.Join(
                "; ",
                "try { Set-PSReadLineOption -PredictionSource None -ErrorAction SilentlyContinue } catch { }",
                "$global:__psstudioSnapshotRoot = " + quotedSnapshotRoot,
                "$global:__psstudioRun = " + helperScriptBlock);
        }

        private static string CreateStartToken()
        {
            return ExecStartTokenPrefix + Guid.NewGuid().ToString("N");
        }

        private static string CreateCompletionToken()
        {
            return ExecDoneTokenPrefix + Guid.NewGuid().ToString("N");
        }

        private static string FormatDispatchCommandForLog(string commandText)
        {
            if (string.IsNullOrEmpty(commandText))
            {
                return string.Empty;
            }

            return commandText
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private void SetPendingExecutionTokens(string? startToken, string? completionToken)
        {
            lock (_syncRoot)
            {
                _pendingStartToken = startToken;
                _pendingCompletionToken = completionToken;
            }
        }

        public async Task SendInterruptAsync()
        {
            // Send Ctrl+C (ETX = 0x03) to the ConPTY process.  This is the standard way
            // to interrupt a running command in an interactive terminal without killing the
            // whole session.  If the session is not running we fall back to a no-op — the
            // caller should handle the case where there is nothing to interrupt.
            if (!IsSessionRunning)
            {
                return;
            }

            try
            {
                await WriteTerminalInputAsync("\x03", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best effort only — if the write fails the session is likely already gone.
            }
        }

        public async Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null)
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

            lock (_syncRoot)
            {
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
                ActiveRuntime = null;
                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _firstOutputLogged = false;
                _firstAnsiOutputLogged = false;
                _rawOutputInfoLogCount = 0;
                snapshotPathsToDelete = new List<string>(_pendingSnapshotPaths);
                _pendingSnapshotPaths.Clear();
            }

            var stopped = false;

            try
            {
                readerCancellation?.Cancel();
            }
            catch
            {
                // Best effort only.
            }

            try
            {
                writerToDispose?.Dispose();
            }
            catch
            {
                // Best effort only.
            }

            if (inputWriterHandle != IntPtr.Zero)
            {
                CloseHandle(inputWriterHandle);
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
                        using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            await processToStop.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Timed out — process should still die shortly; continue cleanup.
                        }
                    }

                    stopped = true;
                }
                catch
                {
                    stopped = false;
                }
                finally
                {
                    processToStop.Dispose();
                }
            }

            // Complete all potentially blocking handle teardown on a background thread.
            // This keeps Reset Console from freezing the WPF UI if ConPTY is still draining.
            _ = Task.Run(async () =>
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

                try
                {
                    if (stdoutReaderTask is not null)
                    {
                        await Task.WhenAny(stdoutReaderTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Best effort only.
                }

                try
                {
                    if (stderrReaderTask is not null)
                    {
                        await Task.WhenAny(stderrReaderTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Best effort only.
                }

                readerCancellation?.Dispose();
            });

            return stopped;
        }

        public void Dispose()
        {
            // Fire-and-forget: the process has already been killed and handles closed.
            // Blocking here (e.g. during Window_Closing on the UI thread) would freeze
            // the application for up to 2 seconds waiting for the drain timeout.
            _ = StopConsoleAsync();
        }

        private void StartPseudoConsoleSession(PowerShellRuntimeInfo runtime, string workingDirectory, Action<ExecutionOutputRecord> onOutput)
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
                var commandLine = "\"" + runtime.ExecutablePath + "\"" +
                    " -NoLogo -NoExit -ExecutionPolicy Bypass -Command " + QuoteCommandArgument(startupCommand);

                if (!CreateProcessW(
                        runtime.ExecutablePath,
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
                process.Exited += (_, _) =>
                {
                    AppLogger.Info("LiveConsole", "The ConPTY PowerShell terminal process exited.");
                    capturedOnOutput(new ExecutionOutputRecord(
                        ExecutionOutputStreamKind.Lifecycle,
                        "The PowerShell terminal session exited.",
                        DateTime.Now));
                    // Notify the ViewModel so IsExecutionRunning can be cleared if the
                    // script called 'exit' before the sentinel was echoed.
                    SessionTerminated?.Invoke();
                };

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

                _readerCancellationTokenSource = new CancellationTokenSource();
                _stdoutReaderTask = Task.Run(
                    () => ReadPseudoConsoleOutputLoopAsync(_outputReaderHandle, onOutput, _readerCancellationTokenSource.Token));

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

        private void StartRedirectedSession(PowerShellRuntimeInfo runtime, string workingDirectory, Action<ExecutionOutputRecord> onOutput)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtime.ExecutablePath,
                    Arguments = "-NoLogo -NoExit -ExecutionPolicy Bypass -Command " + QuoteCommandArgument(BuildTerminalStartupCommand()),
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

            var capturedOnOutput = onOutput;
            process.Exited += (_, _) =>
            {
                AppLogger.Info("LiveConsole", "The redirected PowerShell terminal process exited.");
                capturedOnOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    "The PowerShell terminal session exited.",
                    DateTime.Now));
                ResetPendingCommandState(deleteSnapshots: true);
                SessionTerminated?.Invoke();
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("The redirected PowerShell terminal process could not be started.");
            }

            _process = process;
            _terminalWriter = process.StandardInput;
            _readerCancellationTokenSource = new CancellationTokenSource();
            _stdoutReaderTask = Task.Run(() => ReadStreamLoopAsync(process.StandardOutput, ExecutionOutputStreamKind.StandardOutput, onOutput, _readerCancellationTokenSource.Token));
            _stderrReaderTask = Task.Run(() => ReadStreamLoopAsync(process.StandardError, ExecutionOutputStreamKind.StandardError, onOutput, _readerCancellationTokenSource.Token));
        }

        private async Task ReadPseudoConsoleOutputLoopAsync(IntPtr outputReaderHandle, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken)
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

                    if (!_firstOutputLogged)
                    {
                        _firstOutputLogged = true;
                        var preview = new string(buffer, 0, Math.Min(charsRead, 200));
                        var hasAnsi = preview.Contains('\x1b');
                        var escaped = preview.Replace("\x1b", "\\x1b").Replace("\r", "\\r").Replace("\n", "\\n");
                        // Diagnostic goes to the debug output only — NOT to the xterm.js terminal,
                        // because the escaped string contains literal "\x1b" sequences that xterm.js
                        // would render as visible text rather than as ANSI escape codes.
                        System.Diagnostics.Debug.WriteLine(
                            $"[LiveConsoleService] First ConPTY chunk — {charsRead} chars, hasAnsi={hasAnsi}: {escaped}");
                    }

                    PublishTerminalChunk(new string(buffer, 0, charsRead), ExecutionOutputStreamKind.StandardOutput, onOutput);
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
                AppLogger.Error("LiveConsole", "ConPTY terminal reader stopped unexpectedly.", ex);
                onOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    $"Terminal reader stopped unexpectedly: {ex.Message}",
                    DateTime.Now));
            }
        }

        private async Task ReadStreamLoopAsync(StreamReader reader, ExecutionOutputStreamKind streamKind, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken)
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

                    PublishTerminalChunk(new string(buffer, 0, charsRead), streamKind, onOutput);
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
                AppLogger.Error("LiveConsole", "Redirected terminal reader stopped unexpectedly.", ex);
                onOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    $"Terminal reader stopped unexpectedly: {ex.Message}",
                    DateTime.Now));
            }
        }

        private void PublishTerminalChunk(string rawChunk, ExecutionOutputStreamKind streamKind, Action<ExecutionOutputRecord> onOutput)
        {
            if (string.IsNullOrEmpty(rawChunk))
            {
                return;
            }

            // ── Raw path (for xterm.js) ───────────────────────────────────────────
            // Strip only null bytes; preserve all ANSI/VT100 sequences so xterm.js
            // can render colors, cursor movement, progress bars, etc.
            var raw = rawChunk.Replace("\0", string.Empty, StringComparison.Ordinal);
            raw = FilterInternalTerminalOutput(raw, out var hasSentinel);

            // ── Cleaned path (for internal tracking) ─────────────────────────────
            // Strip OSC/ANSI sequences and normalise line endings so that the
            // current-directory regex and lifecycle checks work on plain text.
            var cleaned = OscRegex.Replace(raw, string.Empty);
            cleaned = AnsiRegex.Replace(cleaned, string.Empty);
            cleaned = cleaned.Replace("\r\n", "\n", StringComparison.Ordinal);
            cleaned = cleaned.Replace("\r", "\n", StringComparison.Ordinal);

            // Fire the completion event if the sentinel was present.
            if (hasSentinel)
            {
                AppLogger.Debug("LiveConsole", "Execution-done sentinel detected in terminal output and filtered before xterm.js.");
                CompleteCommandExecution();
            }

            if (!string.IsNullOrEmpty(cleaned))
            {
                UpdateCurrentDirectoryFromPrompt(cleaned);
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
                    $"ConPTY raw output chunk #{_rawOutputInfoLogCount}. Stream={streamKind}, RawLength={raw.Length}, CleanLength={cleaned.Length}, HasAnsi={raw.Contains('\x1b')}, Preview='{FormatOutputForLog(raw)}'.");
            }

            // ── Output routing ────────────────────────────────────────────────────
            // When a raw-output subscriber is registered (i.e. the xterm.js terminal
            // control is wired up), send the raw VT data there and skip the cleaned-
            // text path for display.  If no subscriber is present — e.g. during early
            // startup before the control initialises — fall back to the cleaned path
            // so text is not silently dropped.
            if (!string.IsNullOrEmpty(raw))
            {
                var rawHandler = RawOutputReceived;
                if (rawHandler is not null)
                {
                    rawHandler(raw);
                }
                else if (!string.IsNullOrEmpty(cleaned))
                {
                    onOutput(new ExecutionOutputRecord(streamKind, cleaned, DateTime.Now));
                }
            }
        }

        private bool TryBeginCommandDispatch(bool isScript, string? snapshotPath, out string? failureMessage)
        {
            lock (_syncRoot)
            {
                if (_process is null || _process.HasExited)
                {
                    failureMessage = "The PowerShell terminal session is not running.";
                    return false;
                }

                if (_isCommandInProgress)
                {
                    failureMessage = "Another terminal operation is already running.";
                    return false;
                }

                _isCommandInProgress = true;
                _currentCommandIsScript = isScript;
                if (isScript && !string.IsNullOrWhiteSpace(snapshotPath))
                {
                    _pendingSnapshotPaths.Enqueue(snapshotPath);
                }

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
                if (_currentCommandIsScript && _pendingSnapshotPaths.Count > 0)
                {
                    snapshotPaths.AddRange(_pendingSnapshotPaths);
                    _pendingSnapshotPaths.Clear();
                }

                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
            }

            if (deleteSnapshot)
            {
                foreach (var snapshotPath in snapshotPaths)
                {
                    TryDeleteSnapshot(snapshotPath);
                }
            }
        }

        private void CompleteCommandExecution()
        {
            bool wasScript;
            List<string> snapshotPaths = new();

            lock (_syncRoot)
            {
                if (!_isCommandInProgress)
                {
                    return;
                }

                wasScript = _currentCommandIsScript;
                if (wasScript && _pendingSnapshotPaths.Count > 0)
                {
                    snapshotPaths.AddRange(_pendingSnapshotPaths);
                    _pendingSnapshotPaths.Clear();
                }

                _isCommandInProgress = false;
                _currentCommandIsScript = false;
                _pendingStartToken = null;
                _pendingCompletionToken = null;
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
            }

            foreach (var snapshotPath in snapshotPaths)
            {
                TryDeleteSnapshot(snapshotPath);
            }

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
                _pendingHiddenOutputFragments.Clear();
                _hiddenOutputBuffer = string.Empty;
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

        private string FilterInternalTerminalOutput(string raw, out bool hasSentinel)
        {
            if (string.IsNullOrEmpty(raw))
            {
                hasSentinel = false;
                return string.Empty;
            }

            lock (_syncRoot)
            {
                hasSentinel = false;

                var commandInProgress = _isCommandInProgress;
                var startToken = _pendingStartToken;
                var completionToken = _pendingCompletionToken;
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

                _hiddenOutputBuffer += raw;

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

            CurrentWorkingDirectory = path;
        }

        private async Task WriteTerminalInputAsync(string text, CancellationToken cancellationToken)
        {
            StreamWriter? writer;
            lock (_syncRoot)
            {
                writer = _terminalWriter;
            }

            if (writer is null)
            {
                throw new InvalidOperationException("The terminal input writer is not available.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            AppLogger.Debug("LiveConsole", $"Writing terminal input to ConPTY. Length={text.Length}, Data='{FormatInputForLog(text)}'.");
            await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
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
            IntPtr pseudoConsole;
            lock (_syncRoot)
            {
                _terminalColumns = Math.Max(1, cols);
                _terminalRows    = Math.Max(1, rows);
                pseudoConsole    = _pseudoConsoleHandle;
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                ResizePseudoConsole(pseudoConsole, new COORD((short)_terminalColumns, (short)_terminalRows));
            }
        }

        /// <inheritdoc />
        public async Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(data) || !IsSessionRunning)
            {
                if (!string.IsNullOrEmpty(data))
                {
                    AppLogger.Debug("LiveConsole", $"Ignoring raw terminal input because the session is not running. Length={data.Length}, Data='{FormatInputForLog(data)}'.");
                }
                return;
            }

            try
            {
                AppLogger.Debug("LiveConsole", $"Raw terminal input received. Length={data.Length}, Data='{FormatInputForLog(data)}'.");
                await WriteTerminalInputAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best effort — pipe may be broken if the session exited.
            }
        }

        private static string FormatInputForLog(string text, int maxLength = 80)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(text.Length * 2, maxLength + 8));
            foreach (var ch in text)
            {
                _ = ch switch
                {
                    '\r' => builder.Append("\\r"),
                    '\n' => builder.Append("\\n"),
                    '\t' => builder.Append("\\t"),
                    '\x1b' => builder.Append("\\x1b"),
                    _ when char.IsControl(ch) => builder.Append($"\\u{(int)ch:x4}"),
                    _ => builder.Append(ch)
                };

                if (builder.Length >= maxLength)
                {
                    builder.Append("...");
                    break;
                }
            }

            return builder.ToString();
        }

        private static string FormatOutputForLog(string text, int maxLength = 120)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(text.Length * 2, maxLength + 8));
            foreach (var ch in text)
            {
                _ = ch switch
                {
                    '\r' => builder.Append("\\r"),
                    '\n' => builder.Append("\\n"),
                    '\t' => builder.Append("\\t"),
                    '\x1b' => builder.Append("\\x1b"),
                    _ when char.IsControl(ch) => builder.Append($"\\u{(int)ch:x4}"),
                    _ => builder.Append(ch)
                };

                if (builder.Length >= maxLength)
                {
                    builder.Append("...");
                    break;
                }
            }

            return builder.ToString();
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

        private static string CreateDispatchInstructionSnapshot(string scriptPath, string startToken, string completionToken, bool executeInCurrentScope)
        {
            var rootDirectory = GetSnapshotRootDirectory(createIfMissing: true);

            var fileName = $"{DispatchInstructionFilePrefix}{Guid.NewGuid():N}.ps1";
            var fullPath = Path.Combine(rootDirectory, fileName);
            var lines = new[]
            {
                scriptPath,
                startToken,
                completionToken,
                executeInCurrentScope ? "true" : "false"
            };

            File.WriteAllLines(fullPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
