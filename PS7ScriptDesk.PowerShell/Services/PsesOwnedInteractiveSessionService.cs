using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Phase 2C foundation provider. PSES is the process/host owner and its
/// console-repl stdio is the terminal transport. This type intentionally does
/// not create a second PowerShell process for debugging.
/// </summary>
public sealed class PsesOwnedInteractiveSessionService : ILiveConsoleService, IAuthoritativeSessionStructuredExecution, IAuthoritativePsesDebugEndpoint, IAuthoritativeLiveCompletionProvider
{
    private const string FallbackPsesHostVersion = "2025.4.0";
    private const int StartupOutputCaptureLimit = 32 * 1024;
    private static readonly TimeSpan PromptRecoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex AnsiSequenceRegex = new("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // PSES can emit its startup banner and first prompt in one decoded read:
    // "...PowerShell Editor Services <=====PS C:\\...>". Treat the banner's
    // repeated '=' boundary as a valid prompt boundary, while requiring a
    // rooted filesystem/provider path so ordinary script output containing
    // "PS ..." is not promoted to readiness.
    private static readonly Regex PromptBoundaryRegex = new("(?:^|\\r?\\n|={2,})[ \\t]*PS[ \\t]+(?:\\r?\\n[ \\t]*)?(?:[A-Za-z]:[\\\\/]|/)[^\\r\\n>]{0,240}>[ \\t]*(?:\\r?\\n|$)|(?:^|\\r?\\n)[ \\t]*>>[ \\t]*(?:\\r?\\n|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _structuredExecutionGate = new(1, 1);
    private readonly SemaphoreSlim _terminalInputGate = new(1, 1);
    private ConPtyProcess? _conPty;
    private Task? _outputTask;
    private PsesLanguageServiceClient? _languageServiceClient;
    private Action<Exception>? _languageServiceFaultHandler;
    private EventHandler? _processExitedHandler;
    private Action<ExecutionOutputRecord>? _onOutput;
    private PowerShellRuntimeInfo? _activeRuntime;
    private string? _workingDirectory;
    private string? _sessionDetailsPath;
    private string? _languageServicePipeName;
    private string? _debugServicePipeName;
    private int _generation;
    private bool _disposed;
    private bool _shutdownRequested;
    private bool _sessionFaulted;
    private bool _commandInProgress;
    private CancellationTokenSource? _activeCommandCancellation;
    private StructuredEchoFilter? _activeStructuredEchoFilter;
    private StructuredExecutionState? _activeStructuredExecutionState;
    private long _promptReadyObservation;
    private int _promptRecoveryGeneration;
    private TaskCompletionSource<long>? _promptRecoveryWaiter;
    private string _promptRecognitionBuffer = string.Empty;
    private string _cursorVisibilitySequenceTail = string.Empty;
    private bool _cursorVisible;
    private event Action<int, bool>? CursorVisibilityChanged;
    private FileStream? _rawConPtyCapture;
    private StreamWriter? _rawConPtyMetadata;
    private long _rawConPtyOffset;
    private Guid _rawConPtyCorrelationId;

    public bool IsSessionRunning => _conPty is { HasExited: false } && !_sessionFaulted;
    public bool IsInputWritable => IsSessionRunning;
    public bool IsCommandInProgress
    {
        get
        {
            lock (_syncRoot)
            {
                return _commandInProgress && IsSessionRunning;
            }
        }
    }
    public bool IsHostAttached => IsSessionRunning;
    public PowerShellRuntimeInfo? ActiveRuntime => _activeRuntime;
    public int? ProcessId => _conPty is { HasExited: false } p ? p.ProcessId : null;
    public DateTimeOffset? ProcessStartTimeUtc => _conPty is { HasExited: false } p ? p.ProcessStartTimeUtc : null;
    public int SessionGeneration => _generation;
    public string? CurrentWorkingDirectory => _workingDirectory;
    public string? SessionDetailsPath => _sessionDetailsPath;
    public string? LanguageServicePipeName => _languageServicePipeName;
    public string? DebugServicePipeName => _debugServicePipeName;
    public bool IsLiveCompletionAvailable => _languageServiceClient is { IsInitialized: true } && IsSessionRunning;

    public Task<AuthoritativeCompletionResult?> GetLiveCompletionsAsync(
        string documentUri,
        string documentText,
        int cursorOffset,
        CancellationToken cancellationToken = default)
    {
        var client = _languageServiceClient;
        if (client is null || !IsLiveCompletionAvailable)
        {
            return Task.FromResult<AuthoritativeCompletionResult?>(null);
        }

        return client.GetLiveCompletionsAsync(documentUri, documentText, cursorOffset, cancellationToken);
    }

    public event Action? ScriptExecutionCompleted;
    public event Action? CommandExecutionCompleted;
    public event Action? SessionTerminated;
    public event Action<int>? TerminalSessionStarted;
    public event Action<int>? TerminalSessionStopping;
    public event Action<int, string>? PromptReadyObserved;
    public event Action<int, string>? RawOutputReceived;

    public void AttachHost(IntPtr hostHandle, int width, int height) { }
    public void ResizeHost(int width, int height) { }
    public void ResizeConsole(int cols, int rows)
    {
        try
        {
            _conPty?.Resize(cols, rows);
            DeveloperDiagnostics.LogInfo("PSES", "Resized authoritative PSES ConPTY.", new Dictionary<string, object?>
            {
                ["columns"] = cols, ["rows"] = rows, ["processId"] = ProcessId
            });
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Authoritative PSES ConPTY resize failed.", new Dictionary<string, object?>
            {
                ["columns"] = cols, ["rows"] = rows, ["processId"] = ProcessId
            });
        }
    }
    public void FocusConsole() { }

    public async Task StartSessionAsync(
        PowerShellRuntimeInfo runtime,
        Action<ExecutionOutputRecord> onOutput,
        string? startupWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        await ShutdownCoreAsync(cancellationToken).ConfigureAwait(false);

        var root = ResolvePsesRoot();
        var startScript = Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1");
        if (!File.Exists(startScript))
            throw new FileNotFoundException("PSES Start-EditorServices.ps1 was not found.", startScript);

        var sessionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PS7ScriptDesk", "PSES", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        var sessionDetailsPath = Path.Combine(sessionRoot, "session.json");
        var logDirectory = Path.Combine(sessionRoot, "Logs");
        Directory.CreateDirectory(logDirectory);
        var hostVersion = ResolvePsesHostVersion();
        var workingDirectory = startupWorkingDirectory ?? Environment.CurrentDirectory;
        var arguments = BuildArguments(root, startScript, sessionDetailsPath, logDirectory, hostVersion);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stdoutCapture = new BoundedStartupCapture(StartupOutputCaptureLimit);
        var stderrCapture = new BoundedStartupCapture(StartupOutputCaptureLimit);
        DeveloperDiagnostics.LogInfo("PSES", "Preparing PSES-owned interactive session launch.", new Dictionary<string, object?>
        {
            ["executable"] = DeveloperDiagnostics.SanitizePath(runtime.LaunchExecutablePath),
            ["arguments"] = arguments.Select(DeveloperDiagnostics.SanitizePreview).ToArray(),
            ["resolvedPsesRoot"] = DeveloperDiagnostics.SanitizePath(root),
            ["resolvedStartScript"] = DeveloperDiagnostics.SanitizePath(startScript),
            ["workingDirectory"] = DeveloperDiagnostics.SanitizePath(workingDirectory),
            ["sessionDetailsPath"] = DeveloperDiagnostics.SanitizePath(sessionDetailsPath),
            ["logDirectory"] = DeveloperDiagnostics.SanitizePath(logDirectory),
            ["hostVersion"] = hostVersion,
            ["startedAtUtc"] = startedAtUtc
        });

        var conPty = ConPtyProcess.Start(runtime.LaunchExecutablePath, arguments, workingDirectory);
        var process = conPty.Identity.Process;
        lock (_syncRoot)
        {
            _conPty = conPty;
            _onOutput = onOutput;
            _activeRuntime = runtime;
            _workingDirectory = workingDirectory;
            _sessionDetailsPath = sessionDetailsPath;
            _generation++;
            _shutdownRequested = false;
            _sessionFaulted = false;
            _promptReadyObservation = 0;
            _promptRecoveryGeneration = _generation;
            _promptRecoveryWaiter = null;
            _promptRecognitionBuffer = string.Empty;
            _cursorVisibilitySequenceTail = string.Empty;
            _cursorVisible = false;
        }

        var generation = _generation;
        StartRawConPtyCapture(generation, process.Id);
        EventHandler processExitedHandler = (_, _) => OnProcessExited(conPty, generation);
        process.EnableRaisingEvents = true;
        process.Exited += processExitedHandler;
        lock (_syncRoot) _processExitedHandler = processExitedHandler;
        ResizeForensicsTrace.SetAuthoritativeProcessId(process.Id);
        var stdoutTask = ReadStreamAsync(conPty, conPty.Output, ExecutionOutputStreamKind.StandardOutput, generation, stdoutCapture);
        _outputTask = stdoutTask;
        PsesLanguageServiceClient? languageServiceClient = null;
        try
        {
            await WaitForSessionDetailsAsync(sessionDetailsPath, process, cancellationToken).ConfigureAwait(false);
            LoadSessionDetails(sessionDetailsPath);
            var languageServicePipeName = _languageServicePipeName;
            if (string.IsNullOrWhiteSpace(languageServicePipeName))
                throw new InvalidOperationException("PSES did not publish a language-service pipe for the console session.");

            languageServiceClient = new PsesLanguageServiceClient();
            var languageServiceFaultHandler = new Action<Exception>(exception => OnLanguageServiceFaulted(languageServiceClient!, generation, exception));
            languageServiceClient.SessionFaulted += languageServiceFaultHandler;
            await languageServiceClient.InitializeAsync(languageServicePipeName, process.Id, generation,
                (data, token) => SendSyntheticTerminalInputAsync(generation, data, token), cancellationToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                _languageServiceClient = languageServiceClient;
                _languageServiceFaultHandler = languageServiceFaultHandler;
            }
            DeveloperDiagnostics.LogInfo("PSES", "PSES language-service LSP handshake completed; console REPL activation requested.", new Dictionary<string, object?>
            {
                ["processId"] = process.Id,
                ["generation"] = generation,
                ["languageServicePipeName"] = _languageServicePipeName,
                ["consoleReplActivation"] = true
            });
        }
        catch (Exception ex)
        {
            PreservePipeForensicsArtifacts(sessionDetailsPath, logDirectory);
            if (languageServiceClient is not null)
            {
                lock (_syncRoot)
                {
                    if (_languageServiceFaultHandler is not null)
                        languageServiceClient.SessionFaulted -= _languageServiceFaultHandler;
                    _languageServiceFaultHandler = null;
                }
                try { await languageServiceClient.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            process.Exited -= processExitedHandler;
            lock (_syncRoot)
            {
                if (ReferenceEquals(_processExitedHandler, processExitedHandler)) _processExitedHandler = null;
            }
            await DrainStartupStreamsAsync(stdoutTask).ConfigureAwait(false);
            try { await conPty.DisposeAsync().ConfigureAwait(false); } catch { }
            lock (_syncRoot)
            {
                if (ReferenceEquals(_conPty, conPty))
                {
                    _conPty = null;
                    _outputTask = null;
                    _languageServiceClient = null;
                    _languageServiceFaultHandler = null;
                    _activeRuntime = null;
                    _workingDirectory = null;
                    _sessionDetailsPath = null;
                    _languageServicePipeName = null;
                    _debugServicePipeName = null;
                    StopRawConPtyCapture();
                    _sessionFaulted = false;
                }
            }
            LogStartupFailure(process, startedAtUtc, sessionDetailsPath, logDirectory, arguments, stdoutCapture, stderrCapture, ex);
            throw;
        }
        var psesLogPath = ResolvePsesLogPath(logDirectory);
        DeveloperDiagnostics.LogInfo("PSES", "PSES session details published.", new Dictionary<string, object?>
        {
            ["processId"] = process.Id,
            ["sessionDetailsPath"] = DeveloperDiagnostics.SanitizePath(sessionDetailsPath),
            ["logDirectory"] = DeveloperDiagnostics.SanitizePath(logDirectory),
            ["psesLogPath"] = DeveloperDiagnostics.SanitizePath(psesLogPath),
            ["sessionDetailsAppeared"] = true,
            ["stdoutPreview"] = stdoutCapture.GetPreview(),
            ["stderrPreview"] = stderrCapture.GetPreview()
        });
        DeveloperDiagnostics.LogInfo("PSES", "PSES-owned interactive session became available.", new Dictionary<string, object?>
        {
            ["processId"] = process.Id,
            ["generation"] = generation,
            ["sessionDetailsPath"] = sessionDetailsPath,
            ["languageServicePipeName"] = _languageServicePipeName,
            ["debugServicePipeName"] = _debugServicePipeName,
            ["consoleRepl"] = true,
            ["transport"] = "ConPTY + PSES host console REPL + named LSP/DAP session details"
        });
        TerminalSessionStarted?.Invoke(generation);
        // The ConPTY reader starts before the LSP handshake. The first prompt can
        // therefore be observed before the session-start lifecycle event reaches
        // the ViewModel. Replay the sticky observation after PSES is fully ready
        // so WPF command enablement cannot remain in Starting indefinitely.
        long promptObservation;
        lock (_syncRoot) promptObservation = _promptReadyObservation;
        if (promptObservation > 0)
        {
            TerminalStartupTrace.Write("PSES_PROMPT_READY_REPLAY", $"generation={generation}; promptObservation={promptObservation}; processId={process.Id}");
            DeveloperDiagnostics.LogInfo("PSES", "Replayed prompt readiness after PSES/LSP startup because the ConPTY reader may have observed the prompt earlier.", new Dictionary<string, object?>
            {
                ["generation"] = generation,
                ["promptObservation"] = promptObservation,
                ["processId"] = process.Id,
                ["replayedAfterSessionStarted"] = true
            });
            PromptReadyObserved?.Invoke(generation, _workingDirectory ?? string.Empty);
        }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsInputWritable) throw new InvalidOperationException("The PSES console-repl input is not writable.");
        return WriteInputCoreAsync(data, cancellationToken);
    }

    private Task SendSyntheticTerminalInputAsync(int generation, string data, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_shutdownRequested || _sessionFaulted || generation != _generation || !IsSessionRunning)
            {
                DeveloperDiagnostics.LogWarning("PSES", "Rejected stale or unavailable synthetic terminal input.", new Dictionary<string, object?>
                {
                    ["inputGeneration"] = generation,
                    ["activeGeneration"] = _generation,
                    ["shutdownRequested"] = _shutdownRequested,
                    ["sessionFaulted"] = _sessionFaulted
                });
                return Task.CompletedTask;
            }
        }
        DeveloperDiagnostics.LogInfo("PSES", "Serializing VS Code-compatible synthetic terminal input.", new Dictionary<string, object?>
        {
            ["syntheticInput"] = "p",
            ["inputLength"] = data.Length,
            ["sessionGeneration"] = _generation,
            ["processId"] = ProcessId
        });
        return WriteInputCoreAsync(data, cancellationToken);
    }

    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(string commandText, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken = default)
    {
        // Direct terminal commands are intentionally interactive ConPTY input.
        // Only editor-owned Run File/Run Selection requests use structured LSP
        // evaluation; routing console commands through that path would change
        // PSReadLine, Read-Host, and terminal-input semantics.
        DeveloperDiagnostics.LogInfo("PSES", "Direct console command routed through interactive ConPTY input.", new Dictionary<string, object?>
        {
            ["commandLength"] = commandText.Length,
            ["sessionGeneration"] = SessionGeneration,
            ["processId"] = ProcessId,
            ["structuredEvaluation"] = false,
            ["contentOmitted"] = true
        });
        return ExecuteTextAsync(commandText, onOutput, cancellationToken);
    }

    public async Task<LiveConsoleCommandResult> ExecuteScriptAsync(string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scriptContent);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!IsSessionRunning || _languageServiceClient is null)
            throw new InvalidOperationException("The authoritative PSES session is not ready for editor execution.");

        var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_syncRoot)
        {
            if (_commandInProgress)
            {
                commandCancellation.Dispose();
                throw new InvalidOperationException("Another PSES editor operation is already running.");
            }

            _commandInProgress = true;
            _activeCommandCancellation = commandCancellation;
        }

        var requestId = Guid.NewGuid();
        var sessionGeneration = SessionGeneration;
        var startedAt = DateTimeOffset.UtcNow;
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "PSES-owned ExecuteScriptAsync entered through DAP staging and foreground PSES execution.",
            new Dictionary<string, object?>
            {
                ["providerType"] = GetType().FullName,
                ["documentDisplayName"] = DeveloperDiagnostics.SanitizePreview(documentDisplayName, 120),
                ["scriptLength"] = scriptContent.Length,
                ["executeInCurrentScope"] = executeInCurrentScope,
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["processId"] = ProcessId,
                ["inputWritable"] = IsInputWritable,
                ["rawMultilineTerminalInjection"] = false,
                ["contentOmitted"] = true
            });

        try
        {
            var result = await ExecuteStructuredScriptAsync(
                requestId,
                sessionGeneration,
                documentDisplayName,
                scriptContent,
                executeInCurrentScope,
                onOutput,
                commandCancellation.Token).ConfigureAwait(false);

            var wasStopped = result.Status == EditorExecutionStatus.Cancelled || commandCancellation.IsCancellationRequested;
            var commandResult = new LiveConsoleCommandResult(
                documentDisplayName,
                wasStopped,
                CurrentWorkingDirectory,
                startedAt.LocalDateTime,
                DateTime.Now);
            ScriptExecutionCompleted?.Invoke();
            CommandExecutionCompleted?.Invoke();
            return commandResult;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeCommandCancellation, commandCancellation))
                {
                    _activeCommandCancellation = null;
                    _commandInProgress = false;
                }
            }
            commandCancellation.Dispose();
            DeveloperDiagnostics.LogInfo(
                "Phase2CProof",
                "PSES-owned ExecuteScriptAsync exited after foreground execution and prompt observation.",
                new Dictionary<string, object?>
                {
                    ["providerType"] = GetType().FullName,
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["processId"] = ProcessId,
                    ["commandInProgressAfterExit"] = IsCommandInProgress
                });
        }
    }

    public async Task<AuthoritativeStructuredExecutionResult> ExecuteStructuredScriptAsync(
        Guid requestId,
        int sessionGeneration,
        string documentDisplayName,
        string scriptContent,
        bool executeInCurrentScope,
        Action<ExecutionOutputRecord> onOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDisplayName);
        ArgumentNullException.ThrowIfNull(scriptContent);
        ArgumentNullException.ThrowIfNull(onOutput);

        if (!IsSessionRunning || sessionGeneration != SessionGeneration)
        {
            return new AuthoritativeStructuredExecutionResult(
                EditorExecutionStatus.Rejected,
                CurrentWorkingDirectory,
                "The authoritative PSES session is unavailable or belongs to a different generation.",
                CompletionObserved: false);
        }

        var startedAt = DateTimeOffset.UtcNow;
        PsesStructuredEvaluationResult? protocolResult = null;
        var scriptOutcomeVerified = false;
        var foregroundRequested = false;
        var foregroundCompletionVerified = false;
        var executionGateAcquired = false;
        StructuredEchoFilter? requestEchoFilter = null;
        StructuredExecutionState? requestState = null;
        var statusMarkerId = requestId.ToString("N", CultureInfo.InvariantCulture);
        var statusVariable = "__ps7sd_status_" + statusMarkerId;
        var stagedScriptVariable = "__ps7sd_editor_" + statusMarkerId;
        var statusPath = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-" + statusMarkerId + ".status");
        var quotedStatusPath = QuotePowerShellSingleQuotedString(statusPath);
        // Capture $? inside this operation. A later operation can observe a
        // different PowerShell status than the editor script produced.
        var evaluatedExpression = $"${statusVariable} = $true\ntry {{\n" + scriptContent +
            $"\n${statusVariable} = $?\n}} catch {{\n${statusVariable} = $false\nthrow\n}} finally {{\n" +
            $"if (${statusVariable}) {{ [System.IO.File]::WriteAllText({quotedStatusPath}, 'OK') }} else {{ [System.IO.File]::WriteAllText({quotedStatusPath}, 'ERROR') }}\n" +
            $"Remove-Variable -Name '{statusVariable}' -ErrorAction SilentlyContinue\n}}";

        try
        {
            try
            {
                await _structuredExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                executionGateAcquired = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Structured execution canceled before admission; no remote operation was active.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["processId"] = ProcessId,
                    ["cancellationBeforeAdmission"] = true
                });
                return new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Cancelled, CurrentWorkingDirectory,
                    "Execution canceled before admission to the authoritative session.", false);
            }
            lock (_syncRoot)
            {
                if (_shutdownRequested || _sessionFaulted)
                    return new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Rejected, CurrentWorkingDirectory, "The authoritative PSES session is stopping or faulted.", false);
                if (sessionGeneration != _generation || _languageServiceClient is null)
                    return new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Rejected, CurrentWorkingDirectory, "The authoritative PSES session generation changed before execution admission.", false);
            }
            DeveloperDiagnostics.LogInfo("PSES", "Editor operation admitted for private DAP staging and foreground execution.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["scriptLength"] = scriptContent.Length,
                ["contentOmitted"] = true
            });
            DeveloperDiagnostics.LogInfo("PSES", "STRUCTURED_DAP_STAGE_REQUEST_CREATED.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["backendId"] = "authoritative-pses-session",
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration,
                ["scriptLength"] = scriptContent.Length,
                ["executeInCurrentScope"] = executeInCurrentScope,
                ["contentOmitted"] = true
            });
            // DAP REPL evaluation runs inside PSES's PSReadLine idle handler. It
            // must only stage the script: output there advances the console cursor
            // without moving PSReadLine's retained input anchor. The foreground
            // LSP evaluation then cancels that read before executing the script.
            var debugPipe = _debugServicePipeName ?? throw new InvalidOperationException("The PSES debug-service endpoint is unavailable for private editor execution.");
            var stagedExpression = "$global:" + stagedScriptVariable + " = {\ntry {\n" + evaluatedExpression +
                "\n} finally { Remove-Variable -Name '" + stagedScriptVariable + "' -Scope Global -ErrorAction SilentlyContinue }\n}";
            protocolResult = await PsesDapEvaluationClient.EvaluateAsync(debugPipe, requestId, sessionGeneration, stagedExpression, cancellationToken).ConfigureAwait(false);
            DeveloperDiagnostics.LogInfo("PSES", "Private DAP script staging returned before foreground host execution.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["stageStatus"] = protocolResult.Status.ToString(),
                ["stageResponseReceived"] = protocolResult.ResponseReceived,
                ["stagedExpressionLength"] = stagedExpression.Length,
                ["contentOmitted"] = true
            });
            var status = protocolResult.Status;
            var errorMessage = protocolResult.ErrorMessage ?? protocolResult.ProtocolError;
            if (protocolResult.ResponseReceived)
            {
                lock (_syncRoot)
                {
                    if (_shutdownRequested || _sessionFaulted || sessionGeneration != _generation)
                    {
                        status = EditorExecutionStatus.Unknown;
                        errorMessage = "The authoritative PSES session changed before script outcome verification; the script outcome is unknown.";
                    }
                }
                if (status == EditorExecutionStatus.Completed)
                {
                    var invocation = (executeInCurrentScope ? ". " : "& ") + "$global:" + stagedScriptVariable;
                    DeveloperDiagnostics.LogInfo("PSES", "Foreground PSES editor invocation requested after DAP staging.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["executeInCurrentScope"] = executeInCurrentScope,
                        ["invocationLength"] = invocation.Length,
                        ["contentOmitted"] = true
                    });
<<<<<<< Updated upstream
                    var foregroundCompleted = await RestoreInteractivePromptAsync(sessionGeneration, invocation, statusPath, cancellationToken).ConfigureAwait(false);
=======
                    foregroundRequested = true;
                    var foregroundCompleted = await RestoreInteractivePromptAsync(sessionGeneration, invocation, invocationEchoFilter, statusPath, cancellationToken).ConfigureAwait(false);
                    foregroundCompletionVerified = foregroundCompleted;
>>>>>>> Stashed changes
                    if (!foregroundCompleted)
                    {
                        DeveloperDiagnostics.LogWarning("PSES", "Foreground editor execution did not reach a post-script ConPTY prompt boundary.", new Dictionary<string, object?>
                        {
                            ["requestId"] = requestId,
                            ["sessionGeneration"] = sessionGeneration,
                            ["statusFileExists"] = File.Exists(statusPath)
                        });
                        status = EditorExecutionStatus.Unknown;
                        errorMessage = "PSES did not complete foreground editor execution with an observable interactive prompt.";
                        // A lost response or prompt cannot release ownership while pwsh
                        // might still be executing the foreground invocation.
                        await ShutdownAsync().ConfigureAwait(false);
                    }
                }
                if (status == EditorExecutionStatus.Completed)
                {
                    DeveloperDiagnostics.LogInfo("PSES", "Structured in-operation outcome verification reading private status file.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["processId"] = ProcessId,
                        ["scriptLength"] = scriptContent.Length,
                        ["contentOmitted"] = true
                    });
                    var statusFile = new FileInfo(statusPath);
                    var outcome = statusFile.Exists && statusFile.Length <= 16
                        ? (await File.ReadAllTextAsync(statusPath, cancellationToken).ConfigureAwait(false)).Trim()
                        : string.Empty;
                    DeveloperDiagnostics.LogInfo("PSES", "Structured in-operation PowerShell outcome verified from private status file.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["processId"] = ProcessId,
                        ["outcome"] = outcome,
                        ["contentOmitted"] = true
                    });
                    if (outcome.Equals("ERROR", StringComparison.Ordinal))
                    {
                        status = EditorExecutionStatus.Failed;
                        errorMessage = "PowerShell reported a final error status during structured execution.";
                        scriptOutcomeVerified = true;
                    }
                    else if (outcome.Equals("OK", StringComparison.Ordinal))
                    {
                        scriptOutcomeVerified = true;
                    }
                    else
                    {
                        status = EditorExecutionStatus.Unknown;
                        errorMessage ??= "PSES did not write a verifiable private execution status.";
                    }
                }
                protocolResult = protocolResult with { Status = status, ErrorMessage = errorMessage };
            }
            DeveloperDiagnostics.LogInfo("Phase2CProof", "STRUCTURED_RESULT_CREATED.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration,
                ["status"] = status.ToString(),
                ["dapStageResponseReceived"] = protocolResult.ResponseReceived,
                ["protocolError"] = protocolResult.ProtocolError,
                ["scriptOutcomeVerified"] = scriptOutcomeVerified,
                ["foregroundRequested"] = foregroundRequested,
                ["foregroundCompletionVerified"] = foregroundCompletionVerified,
                ["durationMs"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            });
            return new AuthoritativeStructuredExecutionResult(status, CurrentWorkingDirectory, errorMessage, foregroundCompletionVerified);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var promptRecovered = await StopConsoleForGenerationAsync(sessionGeneration, requirePromptRecovery: true).ConfigureAwait(false);
            var sessionTerminated = false;
            if (!promptRecovered)
            {
                // A sent Ctrl+C does not prove that the foreground pipeline stopped.
                // Dispose the owned session before releasing structured execution.
                await ShutdownAsync().ConfigureAwait(false);
                sessionTerminated = !IsSessionRunning;
            }
            DeveloperDiagnostics.LogInfo("PSES", "Structured cancellation recovery settled before execution ownership was released.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["promptRecovered"] = promptRecovered,
                ["sessionTerminated"] = sessionTerminated,
                ["pipelineInterruptionProven"] = promptRecovered || sessionTerminated
            });
            return new AuthoritativeStructuredExecutionResult(promptRecovered || sessionTerminated ? EditorExecutionStatus.Cancelled : EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                promptRecovered ? "Execution canceled; the interactive prompt recovered." : sessionTerminated ? "Execution canceled; the owned session was terminated after prompt recovery failed." : "Execution cancellation could not be verified.", false);
        }
        catch (Exception ex)
        {
            if (foregroundRequested && !foregroundCompletionVerified)
            {
                try { await ShutdownAsync().ConfigureAwait(false); }
                catch (Exception shutdownException)
                {
                    DeveloperDiagnostics.LogException("PSES", shutdownException, "Owned session termination after an unverified foreground failure failed.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration
                    });
                }
            }
            DeveloperDiagnostics.LogException("Phase2CProof", ex, "Authoritative structured execution failed.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration
            });
            return new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                "Structured execution could not verify the script outcome: " + ex.Message, false);
        }
        finally
        {
            TryDeleteFile(statusPath);
            string filteredEchoRemainder;
            lock (_syncRoot)
            {
                var discardEcho = cancellationToken.IsCancellationRequested || protocolResult?.Status == EditorExecutionStatus.Cancelled;
                var filteredRemainder = string.Empty;
                if (requestEchoFilter is not null && ReferenceEquals(_activeStructuredEchoFilter, requestEchoFilter))
                {
                    filteredRemainder = discardEcho ? string.Empty : requestEchoFilter.Flush();
                    _activeStructuredEchoFilter = null;
                }
                var stateRemainder = string.Empty;
                if (requestState is not null && ReferenceEquals(_activeStructuredExecutionState, requestState))
                {
                    if (!discardEcho && filteredRemainder.Length > 0)
                        stateRemainder = string.Concat(requestState.Process(filteredRemainder));
                    if (!discardEcho) stateRemainder += requestState.Flush();
                    _activeStructuredExecutionState = null;
                }
                filteredEchoRemainder = stateRemainder.Length > 0 ? stateRemainder : filteredRemainder;
            }
            if (!string.IsNullOrEmpty(filteredEchoRemainder))
            {
                PublishVisibleOutput(ExecutionOutputStreamKind.StandardOutput, sessionGeneration, filteredEchoRemainder);
            }
            if (executionGateAcquired) _structuredExecutionGate.Release();
            DeveloperDiagnostics.LogInfo("Phase2CProof", "STRUCTURED_GATE_RELEASED.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration
            });
        }
    }

    public async Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null)
    {
        if (!IsInputWritable) return true;
        int generation;
        lock (_syncRoot) generation = _generation;
        return await StopConsoleForGenerationAsync(generation).ConfigureAwait(false);
    }

    private async Task<bool> StopConsoleForGenerationAsync(int expectedGeneration, bool requirePromptRecovery = false)
    {
        ConPtyProcess? expectedConPty;
        TaskCompletionSource<long> promptRecoveryWaiter;
        long promptObservationBeforeInterrupt;
        lock (_syncRoot)
        {
            expectedConPty = _conPty;
            if (_shutdownRequested || _sessionFaulted || expectedGeneration != _generation || expectedConPty is null || expectedConPty.HasExited)
                return false;

            promptObservationBeforeInterrupt = _promptReadyObservation;
            _promptRecognitionBuffer = string.Empty;
            promptRecoveryWaiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _promptRecoveryGeneration = expectedGeneration;
            _promptRecoveryWaiter = promptRecoveryWaiter;
        }
        try
        {
            await WriteInputCoreAsync("\u0003", CancellationToken.None, expectedConPty, expectedGeneration).ConfigureAwait(false);
            var promptRecovered = false;
            try
            {
                var observed = await promptRecoveryWaiter.Task.WaitAsync(PromptRecoveryTimeout).ConfigureAwait(false);
                promptRecovered = true;
                DeveloperDiagnostics.LogInfo("PSES", "PSES console-repl interrupt reached a post-interrupt prompt boundary.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["promptObservationBeforeInterrupt"] = promptObservationBeforeInterrupt,
                    ["promptObservationAfterInterrupt"] = observed,
                    ["promptRecoveryObserved"] = true
                });
            }
            catch (TimeoutException)
            {
                DeveloperDiagnostics.LogWarning("PSES", "PSES console-repl interrupt was sent but no post-interrupt prompt boundary was observed within the bounded recovery interval.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["promptObservationBeforeInterrupt"] = promptObservationBeforeInterrupt,
                    ["promptRecoveryObserved"] = false,
                    ["recoveryTimeoutMilliseconds"] = (int)PromptRecoveryTimeout.TotalMilliseconds
                });
            }
            return !requirePromptRecovery || promptRecovered;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "PSES console-repl interrupt failed.");
            return false;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_promptRecoveryWaiter, promptRecoveryWaiter))
                {
                    _promptRecoveryWaiter = null;
                    _promptRecoveryGeneration = 0;
                }
            }
        }
    }

    private async Task<bool> RestoreInteractivePromptAsync(int expectedGeneration, string invocation, string statusPath, CancellationToken cancellationToken)
    {
        PsesLanguageServiceClient? client;
<<<<<<< Updated upstream
        var promptWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postScriptPromptObserved = false;
        var invocationObserved = false;
        var invocationTail = string.Empty;
        Action<int, string> rawHandler = (generation, text) =>
        {
            if (generation != expectedGeneration) return;
            lock (_syncRoot)
            {
                if (invocationObserved) return;
                var scan = invocationTail + text;
                invocationObserved = scan.Contains(invocation, StringComparison.Ordinal);
                invocationTail = scan.Length >= invocation.Length ? scan[^(invocation.Length - 1)..] : scan;
            }
        };
=======
        var promptSignal = new PromptRestorationSignal(expectedGeneration);
>>>>>>> Stashed changes
        Action<int, string> promptHandler = (generation, _) =>
        {
            // The foreground invocation itself is printed after a prompt. A
            // chunk ending there can look like a ready prompt, so only accept
            // one after the script's private completion file has been written.
            bool cursorVisible;
            bool statusFileExists = File.Exists(statusPath);
            bool invocationEchoRemoved;
            bool acceptedPrompt;
            lock (_syncRoot)
            {
<<<<<<< Updated upstream
                if (!invocationObserved) return;
                postScriptPromptObserved = true;
=======
>>>>>>> Stashed changes
                cursorVisible = _cursorVisible;
                invocationEchoRemoved = invocationEchoFilter.EchoRemoved;
                acceptedPrompt = promptSignal.ObservePrompt(generation, statusFileExists, invocationEchoRemoved, cursorVisible);
            }
            if (acceptedPrompt && generation == expectedGeneration && invocationEchoRemoved && !statusFileExists)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Foreground prompt boundary observed before the script status file; retaining it for completion verification.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["processId"] = ProcessId,
                    ["cursorVisibleAtPrompt"] = cursorVisible,
                    ["promptObservation"] = Interlocked.Read(ref _promptReadyObservation),
                    ["contentOmitted"] = true
                });
            }
        };
        Action<int, bool> cursorHandler = (generation, visible) =>
        {
            lock (_syncRoot) promptSignal.ObserveCursor(generation, visible);
        };
        long observationBeforeHandoff;
        lock (_syncRoot)
        {
            client = _languageServiceClient;
            if (_shutdownRequested || _sessionFaulted || expectedGeneration != _generation || client is null || !client.IsInitialized)
                return false;

            observationBeforeHandoff = _promptReadyObservation;
            _promptRecognitionBuffer = string.Empty;
        }

        PromptReadyObserved += promptHandler;
        RawOutputReceived += rawHandler;
        CursorVisibilityChanged += cursorHandler;
        try
        {
            // PSES owns the foreground transition and writes the invocation before
            // user output. Its REPL then writes the next prompt at the new cursor.
            var handoff = await client.EvaluateAsync(Guid.NewGuid(), invocation, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(statusPath))
            {
                DeveloperDiagnostics.LogWarning("PSES", "Foreground evaluate response arrived before the script status boundary; execution ownership remains active.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["processId"] = ProcessId,
                    ["handoffStatus"] = handoff.Status.ToString(),
                    ["handoffResponseReceived"] = handoff.ResponseReceived,
                    ["executionBusyObserved"] = handoff.ExecutionBusyObserved,
                    ["protocolErrorLength"] = handoff.ProtocolError?.Length,
                    ["errorMessageLength"] = handoff.ErrorMessage?.Length,
                    ["contentOmitted"] = true
                });
            }
            // PSES can respond after its own long request interval while the
            // foreground PowerShell command continues. Its response is not the
            // script completion boundary; the private status file is written
            // only after the user script exits. Cancellation or session failure
            // ends this wait without inventing another execution deadline.
            while (!File.Exists(statusPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSessionRunning || expectedGeneration != SessionGeneration) return false;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            if (promptSignal.ObserveStatusFileVisible())
            {
                bool cursorVisibleAtStatus;
                lock (_syncRoot) cursorVisibleAtStatus = _cursorVisible;
                DeveloperDiagnostics.LogInfo("PSES", "Foreground status file appeared after a qualified prompt boundary; restoration signal is now eligible.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["processId"] = ProcessId,
                    ["cursorVisibleAtStatus"] = cursorVisibleAtStatus,
                    ["contentOmitted"] = true
                });
            }
            await promptSignal.Completion.WaitAsync(PromptRecoveryTimeout, cancellationToken).ConfigureAwait(false);
            long observationAfterHandoff;
            lock (_syncRoot) observationAfterHandoff = _promptReadyObservation;
            DeveloperDiagnostics.LogInfo("PSES", "Structured execution completed on the foreground PSES host and restored the interactive prompt.", new Dictionary<string, object?>
            {
                ["sessionGeneration"] = expectedGeneration,
                ["processId"] = ProcessId,
                ["promptObservationBeforeHandoff"] = observationBeforeHandoff,
                ["promptObservationAfterHandoff"] = observationAfterHandoff,
                ["handoffResponseReceived"] = handoff.ResponseReceived,
                ["handoffStatus"] = handoff.Status.ToString(),
                ["promptObservedAfterScriptStatus"] = true,
                ["foregroundInvocationObserved"] = invocationObserved,
                ["cursorVisibleAtCompletion"] = true,
                ["foregroundInvocationLength"] = invocation.Length,
                ["contentOmitted"] = true
            });
            return true;
        }
        catch (TimeoutException)
        {
            DeveloperDiagnostics.LogWarning("PSES", "Foreground PSES editor execution completed without an observable ConPTY prompt boundary.", new Dictionary<string, object?>
            {
                ["sessionGeneration"] = expectedGeneration,
                ["processId"] = ProcessId,
                ["promptObservationBeforeHandoff"] = observationBeforeHandoff,
                ["contentOmitted"] = true
            });
            return false;
        }
        finally
        {
            PromptReadyObserved -= promptHandler;
            RawOutputReceived -= rawHandler;
            CursorVisibilityChanged -= cursorHandler;
        }
    }

    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ShutdownCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<bool> ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        ConPtyProcess? conPty;
        int generation;
        EventHandler? processExitedHandler;
        lock (_syncRoot)
        {
            conPty = _conPty;
            generation = _generation;
            processExitedHandler = _processExitedHandler;
            _processExitedHandler = null;
            if (conPty is not null)
            {
                _shutdownRequested = true;
            }
        }
        if (conPty is null) return true;
        var processId = conPty.ProcessId;
        var outputTask = _outputTask;
        var executionGateHeld = false;
        var executionDrained = false;
        var cleanupSucceeded = true;
        DeveloperDiagnostics.LogInfo("PSES", "SHUTDOWN_BEGIN.", new Dictionary<string, object?>
        {
            ["ownedProcessId"] = processId,
            ["sessionGeneration"] = generation,
            ["shutdownRequested"] = true
        });
        TerminalSessionStopping?.Invoke(generation);
        try
        {
            try
            {
                executionGateHeld = await _structuredExecutionGate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (executionGateHeld)
                {
                    _structuredExecutionGate.Release();
                    executionDrained = true;
                }
                executionGateHeld = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            if (!executionDrained)
            {
                DeveloperDiagnostics.LogWarning("PSES", "Structured execution did not drain within the bounded shutdown interval; transport disposal will fault remaining requests.", new Dictionary<string, object?>
                {
                    ["ownedProcessId"] = processId,
                    ["sessionGeneration"] = generation
                });
            }
            if (!conPty.HasExited)
            {
                DeveloperDiagnostics.LogInfo("PSES", "EXIT_COMMAND_WRITE_BEGIN.", new Dictionary<string, object?>
                {
                    ["ownedProcessId"] = processId,
                    ["sessionGeneration"] = generation,
                    ["characterCount"] = 5,
                    ["contentOmitted"] = true
                });
                await WriteInputCoreAsync("exit\r", cancellationToken).ConfigureAwait(false);
                DeveloperDiagnostics.LogInfo("PSES", "EXIT_COMMAND_WRITE_COMPLETE.", new Dictionary<string, object?>
                {
                    ["ownedProcessId"] = processId,
                    ["sessionGeneration"] = generation,
                    ["flushed"] = true,
                    ["contentOmitted"] = true
                });
                await conPty.Identity.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                DeveloperDiagnostics.LogInfo("PSES", "Graceful owned-process wait completed.", new Dictionary<string, object?>
                {
                    ["ownedProcessId"] = processId,
                    ["processHasExited"] = conPty.HasExited
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Graceful owned-process wait timed out; bounded owned-process fallback will run.", new Dictionary<string, object?>
            {
                ["ownedProcessId"] = processId,
                ["processHasExited"] = conPty.HasExited
            });
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Graceful PSES shutdown request failed; bounded owned-process fallback will run.", new Dictionary<string, object?>
            {
                ["ownedProcessId"] = processId,
                ["processHasExited"] = conPty.HasExited
            });
        }
        finally
        {
            PsesLanguageServiceClient? languageServiceClient;
            Action<Exception>? languageServiceFaultHandler;
            lock (_syncRoot)
            {
                languageServiceClient = _languageServiceClient;
                languageServiceFaultHandler = _languageServiceFaultHandler;
                _languageServiceClient = null;
                _languageServiceFaultHandler = null;
            }
            if (languageServiceClient is not null)
            {
                if (languageServiceFaultHandler is not null)
                    languageServiceClient.SessionFaulted -= languageServiceFaultHandler;
                try { await languageServiceClient.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (processExitedHandler is not null)
                conPty.Identity.Process.Exited -= processExitedHandler;
            try { await conPty.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                cleanupSucceeded = false;
                DeveloperDiagnostics.LogException("PSES", ex, "PSES ConPTY disposal failed during shutdown.", new Dictionary<string, object?>
                {
                    ["ownedProcessId"] = processId,
                    ["sessionGeneration"] = generation
                });
            }
            cleanupSucceeded &= conPty.HasExited;
            if (outputTask is not null)
            {
                try
                {
                    await outputTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    DeveloperDiagnostics.LogInfo("PSES", "PSES output reader task completed during shutdown.", new Dictionary<string, object?>
                    {
                        ["ownedProcessId"] = processId,
                        ["sessionGeneration"] = generation
                    });
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException("PSES", ex, "PSES output reader task did not complete within the bounded shutdown interval.", new Dictionary<string, object?>
                    {
                        ["ownedProcessId"] = processId,
                        ["sessionGeneration"] = generation
                    });
                }
            }
            lock (_syncRoot)
            {
                if (ReferenceEquals(_conPty, conPty))
                {
                    _conPty = null;
                    _outputTask = null;
                    _activeRuntime = null;
                    _workingDirectory = null;
                    _sessionDetailsPath = null;
                    _languageServicePipeName = null;
                    _debugServicePipeName = null;
                    StopRawConPtyCapture();
                    _shutdownRequested = false;
                    _sessionFaulted = false;
                }
            }
            DeveloperDiagnostics.LogInfo("PSES", "SHUTDOWN_END.", new Dictionary<string, object?>
            {
                ["ownedProcessId"] = processId,
                ["sessionGeneration"] = generation,
                ["processHasExited"] = conPty.HasExited,
                ["cleanupSucceeded"] = cleanupSucceeded,
                ["replacementAllowed"] = false
            });
        }
        return executionDrained && cleanupSucceeded;
    }

    public Task SendInterruptAsync() => StopConsoleAsync();

    public async Task<LiveConsoleInterruptResult> InterruptOrRestartAsync(Action<ExecutionOutputRecord>? onOutput = null, CancellationToken cancellationToken = default)
    {
        var processId = ProcessId;
        var attempted = IsInputWritable;
        CancellationTokenSource? activeCommandCancellation;
        lock (_syncRoot) activeCommandCancellation = _activeCommandCancellation;
        try { activeCommandCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        var completed = await StopConsoleAsync(onOutput).ConfigureAwait(false);
        return new LiveConsoleInterruptResult(
            attempted,
            completed,
            escalationRequired: false,
            processTerminationSucceeded: false,
            sessionRestarted: false,
            processId,
            TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        _terminalInputGate.Dispose();
        _structuredExecutionGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task<LiveConsoleCommandResult> ExecuteTextAsync(string text, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var command = text.TrimEnd('\r', '\n') + "\r";
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "PSES-owned ExecuteTextAsync prepared terminal command.",
            new Dictionary<string, object?>
            {
                ["textLength"] = text?.Length ?? 0,
                ["commandLength"] = command.Length,
                ["carriageReturnAppended"] = command.EndsWith('\r'),
                ["sessionGeneration"] = _generation,
                ["processId"] = ProcessId,
                ["inputWritable"] = IsInputWritable,
                ["contentOmitted"] = true
            });
        await WriteInputCoreAsync(command, cancellationToken).ConfigureAwait(false);
        CommandExecutionCompleted?.Invoke();
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "PSES-owned ExecuteTextAsync declared command completion after input flush.",
            new Dictionary<string, object?>
            {
                ["commandLength"] = command.Length,
                ["sessionGeneration"] = _generation,
                ["processId"] = ProcessId
            });
        return new LiveConsoleCommandResult("PSES console-repl command", false, CurrentWorkingDirectory, started, DateTime.UtcNow);
    }

    private async Task WriteInputCoreAsync(string data, CancellationToken cancellationToken, ConPtyProcess? expectedConPty = null, int? expectedGeneration = null)
    {
        ConPtyProcess conPty;
        lock (_syncRoot)
        {
            conPty = expectedConPty ?? _conPty ?? throw new InvalidOperationException("PSES host is not running.");
            if (expectedConPty is not null && (!ReferenceEquals(_conPty, expectedConPty) || expectedGeneration != _generation || _shutdownRequested))
                throw new InvalidOperationException("The authoritative PSES session changed before the input write was admitted.");
        }
        var bytes = Encoding.UTF8.GetBytes(data);
        await _terminalInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_syncRoot)
            {
                if (expectedConPty is not null && (!ReferenceEquals(_conPty, expectedConPty) || expectedGeneration != _generation || _shutdownRequested))
                    throw new InvalidOperationException("The authoritative PSES session changed while the input write was waiting for admission.");
            }
            DeveloperDiagnostics.LogInfo(
                "Phase2CProof",
                "PSES-owned ConPTY write attempted.",
                new Dictionary<string, object?>
                {
                    ["characterCount"] = data.Length,
                    ["byteCount"] = bytes.Length,
                    ["sessionGeneration"] = _generation,
                    ["processId"] = ProcessId,
                    ["processAlive"] = !conPty.HasExited,
                    ["contentOmitted"] = true
                });
            await conPty.Input.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await conPty.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
            DeveloperDiagnostics.LogInfo(
                "Phase2CProof",
                "PSES-owned ConPTY write flushed successfully.",
                new Dictionary<string, object?>
                {
                    ["characterCount"] = data.Length,
                    ["byteCount"] = bytes.Length,
                    ["sessionGeneration"] = _generation,
                    ["processId"] = ProcessId,
                    ["processAliveAfterFlush"] = !conPty.HasExited
                });
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "Phase2CProof",
                ex,
                "PSES-owned ConPTY write or flush failed.",
                new Dictionary<string, object?>
                {
                    ["characterCount"] = data.Length,
                    ["byteCount"] = bytes.Length,
                    ["sessionGeneration"] = _generation,
                    ["processId"] = ProcessId,
                    ["contentOmitted"] = true
                });
            throw;
        }
        finally
        {
            _terminalInputGate.Release();
        }
    }

    private async Task ReadStreamAsync(ConPtyProcess owner, Stream stream, ExecutionOutputStreamKind kind, int generation, BoundedStartupCapture capture)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                CaptureRawConPtyBytes(buffer, count, generation, owner.ProcessId, kind);
                var text = Encoding.UTF8.GetString(buffer, 0, count);
                var cursorVisibility = ObserveCursorVisibility(text, generation);
                ResizeForensicsTrace.Record(
                    $"PsesReceive.{kind}",
                    generation,
                    text,
                    processId: ProcessId,
                    file: "pses-fingerprints.jsonl");
                capture.Append(text);
                PublishOutput(kind, generation, text);
                if (cursorVisibility.HasValue)
                {
                    try { CursorVisibilityChanged?.Invoke(generation, cursorVisibility.Value); } catch { }
                }
                if (ObservePromptBoundary(text, generation))
                {
                    TaskCompletionSource<long>? promptRecoveryWaiter;
                    long promptObservation;
                    lock (_syncRoot)
                    {
                        promptObservation = ++_promptReadyObservation;
                        promptRecoveryWaiter = generation == _promptRecoveryGeneration
                            ? this._promptRecoveryWaiter
                            : null;
                    }
                    promptRecoveryWaiter?.TrySetResult(promptObservation);
                    try { PromptReadyObserved?.Invoke(generation, text); } catch { }
                }
            }
        }
        catch (ObjectDisposedException ex)
        {
            if (!IsExpectedTeardown(owner, generation)) OnOutputTransportFault(owner, generation, ex);
        }
        catch (IOException ex)
        {
            OnOutputTransportFault(owner, generation, ex);
        }
    }

    private bool ObservePromptBoundary(string text, int generation)
    {
        var normalized = AnsiSequenceRegex.Replace(text, string.Empty);
        lock (_syncRoot)
        {
            if (generation != _generation || _shutdownRequested || _sessionFaulted)
            {
                return false;
            }

            _promptRecognitionBuffer += normalized;
            if (_promptRecognitionBuffer.Length > 1024)
            {
                _promptRecognitionBuffer = _promptRecognitionBuffer[^1024..];
            }

            var boundaryObserved = PromptBoundaryRegex.IsMatch(_promptRecognitionBuffer);
            if (boundaryObserved)
            {
                _promptRecognitionBuffer = string.Empty;
            }

            return boundaryObserved;
        }
    }

    private bool? ObserveCursorVisibility(string text, int generation)
    {
        lock (_syncRoot)
        {
            if (generation != _generation || _shutdownRequested || _sessionFaulted) return null;
            var scan = _cursorVisibilitySequenceTail + text;
            var show = scan.LastIndexOf("\u001b[?25h", StringComparison.Ordinal);
            var hide = scan.LastIndexOf("\u001b[?25l", StringComparison.Ordinal);
            _cursorVisibilitySequenceTail = scan.Length > 5 ? scan[^5..] : scan;
            if (show < 0 && hide < 0) return null;
            _cursorVisible = show > hide;
            return _cursorVisible;
        }
    }

    private void StartRawConPtyCapture(int generation, int processId)
    {
        var directory = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_RAW_CONPTY_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
            _rawConPtyCorrelationId = Guid.NewGuid();
            var stem = $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_g{generation}_p{processId}_{_rawConPtyCorrelationId:N}";
            _rawConPtyCapture = new FileStream(Path.Combine(directory, $"{stem}_Raw_ConPTY_Output.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.WriteThrough);
            _rawConPtyMetadata = new StreamWriter(new FileStream(Path.Combine(directory, $"{stem}_Raw_ConPTY_Boundaries.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough), new UTF8Encoding(false)) { AutoFlush = true };
            _rawConPtyOffset = 0;
            DeveloperDiagnostics.LogInfo("PSES", "Raw ConPTY capture enabled at the pre-decode boundary.", new Dictionary<string, object?>
            {
                ["captureDirectory"] = DeveloperDiagnostics.SanitizePath(directory),
                ["correlationId"] = _rawConPtyCorrelationId,
                ["generation"] = generation,
                ["processId"] = processId,
                ["binaryCaptureBeforeDecode"] = true
            });
        }
        catch (Exception ex)
        {
            StopRawConPtyCapture();
            DeveloperDiagnostics.LogException("PSES", ex, "Raw ConPTY capture could not be enabled; normal transport is unaffected.", new Dictionary<string, object?>
            {
                ["captureDirectory"] = DeveloperDiagnostics.SanitizePath(directory),
                ["generation"] = generation,
                ["processId"] = processId
            });
        }
    }

    private void CaptureRawConPtyBytes(byte[] buffer, int count, int generation, int processId, ExecutionOutputStreamKind kind)
    {
        try
        {
            lock (_syncRoot)
            {
                if (_rawConPtyCapture is null || _rawConPtyMetadata is null) return;
                var offset = _rawConPtyOffset;
                _rawConPtyCapture.Write(buffer, 0, count);
                _rawConPtyCapture.Flush();
                _rawConPtyOffset += count;
                _rawConPtyMetadata.WriteLine(JsonSerializer.Serialize(new
                {
                    correlationId = _rawConPtyCorrelationId,
                    monotonicTimestamp = Stopwatch.GetTimestamp(),
                    utc = DateTimeOffset.UtcNow,
                    generation,
                    processId,
                    stream = kind.ToString(),
                    offset,
                    length = count
                }));
            }
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Raw ConPTY capture write failed; normal transport is unaffected.", new Dictionary<string, object?>
            {
                ["correlationId"] = _rawConPtyCorrelationId,
                ["generation"] = generation,
                ["processId"] = processId,
                ["byteCount"] = count
            });
            StopRawConPtyCapture();
        }
    }

    private void StopRawConPtyCapture()
    {
        try { _rawConPtyMetadata?.Dispose(); } catch { }
        try { _rawConPtyCapture?.Dispose(); } catch { }
        _rawConPtyMetadata = null;
        _rawConPtyCapture = null;
        _rawConPtyOffset = 0;
        _rawConPtyCorrelationId = Guid.Empty;
    }

    private async Task WaitForSessionDetailsAsync(string path, Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"PSES exited before publishing session details (exit code {process.ExitCode}).");
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("PSES did not publish session details within 30 seconds.");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LoadSessionDetails(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        _languageServicePipeName = ReadString(root, "languageServicePipeName", "languageServicePipe");
        _debugServicePipeName = ReadString(root, "debugServicePipeName", "debugServicePipe");
        if (string.IsNullOrWhiteSpace(_debugServicePipeName) &&
            string.Equals(ReadString(root, "debugServiceTransport"), "NamedPipe", StringComparison.OrdinalIgnoreCase))
        {
            _debugServicePipeName = ReadString(root, "debugServicePipeName");
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static string ResolvePsesRoot()
    {
        var configured = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        var candidates = Directory.Exists(extensions)
            ? Directory.GetDirectories(extensions, "ms-vscode.powershell-*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
        foreach (var candidate in candidates)
        {
            var root = Path.Combine(candidate, "modules");
            if (File.Exists(Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1"))) return root;
        }
        throw new DirectoryNotFoundException("No installed PSES bundle was found. Set PS7SCRIPTDESK_PSES_ROOT to its modules directory.");
    }

    internal static IReadOnlyList<string> BuildArguments(string root, string startScript, string sessionDetailsPath, string logDirectory, string hostVersion)
    {
        var arguments = new List<string>
        {
            // PSES owns an interactive console REPL.  -NonInteractive makes the
            // PowerShell host reject console input even when the child is attached
            // to a valid ConPTY, which surfaces downstream as invalid-handle/readline
            // failures.  Keep the launch contract interactive, as the maintained
            // VS Code PSES host does.
            "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", startScript,
            "-HostName", "Visual Studio Code Host", "-HostProfileId", "Microsoft.VSCode", "-HostVersion", hostVersion,
            "-BundledModulesPath", root, "-EnableConsoleRepl", "-SessionDetailsPath", sessionDetailsPath,
            "-LogPath", logDirectory, "-LogLevel", "Warning"
        };
        return arguments;
    }

    internal static string ResolvePsesHostVersion()
    {
        var configured = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_HOST_VERSION");
        return string.IsNullOrWhiteSpace(configured) ? FallbackPsesHostVersion : configured;
    }

    private static async Task DrainStartupStreamsAsync(Task stdoutTask)
    {
        try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }
    }

    private static void LogStartupFailure(Process process, DateTimeOffset startedAtUtc, string sessionDetailsPath, string logDirectory, IReadOnlyList<string> arguments, BoundedStartupCapture stdout, BoundedStartupCapture stderr, Exception failure)
    {
        DateTimeOffset? exitAtUtc = null;
        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitAtUtc = DateTimeOffset.UtcNow;
                exitCode = process.ExitCode;
            }
        }
        catch { }

        DeveloperDiagnostics.LogException("PSES", failure, "PSES-owned interactive session startup failed with bounded launch evidence.", new Dictionary<string, object?>
        {
            ["executable"] = DeveloperDiagnostics.SanitizePath(process.StartInfo.FileName),
            ["arguments"] = arguments.Select(DeveloperDiagnostics.SanitizePreview).ToArray(),
            ["workingDirectory"] = DeveloperDiagnostics.SanitizePath(process.StartInfo.WorkingDirectory),
            ["sessionDetailsPath"] = DeveloperDiagnostics.SanitizePath(sessionDetailsPath),
            ["logDirectory"] = DeveloperDiagnostics.SanitizePath(logDirectory),
            ["psesLogPath"] = DeveloperDiagnostics.SanitizePath(ResolvePsesLogPath(logDirectory)),
            ["processId"] = SafeProcessId(process),
            ["startTimestampUtc"] = startedAtUtc,
            ["exitTimestampUtc"] = exitAtUtc,
            ["exitCode"] = exitCode,
            ["sessionDetailsAppeared"] = File.Exists(sessionDetailsPath),
            ["stdoutPreview"] = stdout.GetPreview(),
            ["stderrPreview"] = stderr.GetPreview(),
            ["stdoutLengthCapped"] = stdout.IsCapped,
            ["stderrLengthCapped"] = stderr.IsCapped
        });
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; } catch { return null; }
    }

    private static string? ResolvePsesLogPath(string logDirectory)
    {
        try
        {
            return Directory.Exists(logDirectory)
                ? Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    private static void PreservePipeForensicsArtifacts(string sessionDetailsPath, string logDirectory)
    {
        var destination = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PIPE_FORENSICS_DIR");
        if (string.IsNullOrWhiteSpace(destination)) return;
        try
        {
            Directory.CreateDirectory(destination);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            if (File.Exists(sessionDetailsPath))
                File.Copy(sessionDetailsPath, Path.Combine(destination, $"{stamp}_PSES_session.json"), overwrite: true);
            if (Directory.Exists(logDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly))
                    File.Copy(file, Path.Combine(destination, $"{stamp}_PSES_{Path.GetFileName(file)}"), overwrite: true);
            }
        }
        catch
        {
            // Forensic preservation is opt-in and must never alter startup cleanup.
        }
    }

    private sealed class BoundedStartupCapture
    {
        private readonly int _limit;
        private readonly StringBuilder _buffer = new();
        private readonly object _syncRoot = new();
        public BoundedStartupCapture(int limit) => _limit = limit;
        public bool IsCapped { get; private set; }
        public void Append(string text)
        {
            lock (_syncRoot)
            {
                if (_buffer.Length >= _limit) { IsCapped = true; return; }
                var remaining = _limit - _buffer.Length;
                if (text.Length > remaining) { _buffer.Append(text.AsSpan(0, remaining)); IsCapped = true; }
                else _buffer.Append(text);
            }
        }
        public string GetPreview()
        {
            lock (_syncRoot) return DeveloperDiagnostics.SanitizePreview(_buffer.ToString(), _limit);
        }
    }

    private void OnProcessExited(ConPtyProcess owner, int generation)
    {
        bool expectedShutdown;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_conPty, owner) || generation != _generation)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Ignored stale PSES process-exit callback.", new Dictionary<string, object?>
                {
                    ["callbackGeneration"] = generation,
                    ["activeGeneration"] = _generation,
                    ["processId"] = owner.ProcessId
                });
                return;
            }
            expectedShutdown = _shutdownRequested;
            if (!expectedShutdown) _sessionFaulted = true;
        }

        DeveloperDiagnostics.LogInfo(
            "PSES",
            expectedShutdown
                ? "PSES-owned process exited during requested shutdown; unexpected-session recovery suppressed."
                : "PSES-owned process exited unexpectedly; session termination notification published.",
            new Dictionary<string, object?>
            {
                ["processId"] = ProcessId,
                ["expectedShutdown"] = expectedShutdown,
                ["sessionGeneration"] = _generation
            });

        if (expectedShutdown)
        {
            return;
        }

        try { SessionTerminated?.Invoke(); } catch { }
    }

    private void OnLanguageServiceFaulted(PsesLanguageServiceClient client, int generation, Exception exception)
    {
        bool publish;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_languageServiceClient, client) || generation != _generation)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Ignored stale PSES language-service fault callback.", new Dictionary<string, object?>
                {
                    ["callbackGeneration"] = generation,
                    ["activeGeneration"] = _generation,
                    ["clientGeneration"] = client.SessionGeneration
                });
                return;
            }
            publish = !_shutdownRequested && !_sessionFaulted;
            _sessionFaulted = true;
        }

        DeveloperDiagnostics.LogException("PSES", exception, "PSES language-service transport fault invalidated the authoritative session generation.", new Dictionary<string, object?>
        {
            ["sessionGeneration"] = _generation,
            ["processId"] = ProcessId,
            ["replacementAllowed"] = false
        });
        if (publish)
        {
            try { SessionTerminated?.Invoke(); } catch { }
            _ = RecoverFromLanguageServiceFaultAsync();
        }
    }

    private async Task RecoverFromLanguageServiceFaultAsync()
    {
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Automatic cleanup after a language-service transport fault failed; manual restart may be required.", new Dictionary<string, object?>
            {
                ["sessionGeneration"] = _generation,
                ["processId"] = ProcessId
            });
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PsesOwnedInteractiveSessionService));
    }

    private void PublishOutput(ExecutionOutputStreamKind kind, int generation, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Action<ExecutionOutputRecord>? onOutput;
        string visibleText;
        lock (_syncRoot)
        {
            if (_shutdownRequested || generation != _generation || _sessionFaulted)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Rejected stale authoritative PSES output.", new Dictionary<string, object?>
                {
                    ["outputGeneration"] = generation,
                    ["activeGeneration"] = _generation,
                    ["textLength"] = text.Length
                });
                return;
            }
            onOutput = _onOutput;
            visibleText = _activeStructuredExecutionState is { } executionState
                ? _activeStructuredEchoFilter is { } echoFilter
                    ? echoFilter.Filter(string.Concat(executionState.Process(text)))
                    : string.Concat(executionState.Process(text))
                : _activeStructuredEchoFilter?.Filter(text) ?? text;
        }
        PublishVisibleOutput(kind, generation, visibleText);
    }

    private void PublishVisibleOutput(ExecutionOutputStreamKind kind, int generation, string visibleText)
    {
        if (string.IsNullOrEmpty(visibleText)) return;
        Action<ExecutionOutputRecord>? onOutput;
        lock (_syncRoot)
        {
            if (_shutdownRequested || generation != _generation || _sessionFaulted) return;
            onOutput = _onOutput;
        }
        var record = new ExecutionOutputRecord(kind, visibleText, DateTime.UtcNow);
        try { onOutput?.Invoke(record); } catch { }
        try { RawOutputReceived?.Invoke(generation, visibleText); } catch { }
    }

    private bool IsExpectedTeardown(ConPtyProcess owner, int generation)
    {
        lock (_syncRoot) return _shutdownRequested || !ReferenceEquals(_conPty, owner) || generation != _generation;
    }

    private void OnOutputTransportFault(ConPtyProcess owner, int generation, Exception exception)
    {
        bool publish;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_conPty, owner) || generation != _generation || _shutdownRequested || _sessionFaulted)
                return;
            _sessionFaulted = true;
            publish = true;
        }
        DeveloperDiagnostics.LogException("PSES", exception, "Authoritative PSES ConPTY output transport faulted.", new Dictionary<string, object?>
        {
            ["processId"] = owner.ProcessId,
            ["sessionGeneration"] = generation
        });
        if (publish)
        {
            try { SessionTerminated?.Invoke(); } catch { }
            _ = RecoverFromLanguageServiceFaultAsync();
        }
    }

    private static string BuildStructuredWrapper(string scriptPath, string beginMarker, string endPrefix, bool executeInCurrentScope)
    {
        var target = QuotePowerShellSingleQuotedString(scriptPath);
        var invocation = executeInCurrentScope ? "." : "&";
        var begin = QuotePowerShellSingleQuotedString(beginMarker);
        var end = QuotePowerShellSingleQuotedString(endPrefix);
        return $"Write-Output {begin}\r\n" +
               "$__ps7sd_structured_error = $false\r\n" +
               "try {\r\n" +
               $"    {invocation} {target}\r\n" +
               "    if (-not $?) { $__ps7sd_structured_error = $true }\r\n" +
               "} catch {\r\n" +
               "    $__ps7sd_structured_error = $true\r\n" +
               "    Write-Error -ErrorRecord $_\r\n" +
               "} finally {\r\n" +
               $"    if ($__ps7sd_structured_error) {{ Write-Output ({end} + 'ERROR') }} else {{ Write-Output ({end} + 'OK') }}\r\n" +
               "    Remove-Variable -Name '__ps7sd_structured_error' -ErrorAction SilentlyContinue\r\n" +
               "}\r\n";
    }

    private static string QuotePowerShellSingleQuotedString(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup; the bounded path is retained for diagnostics if deletion fails.
        }
    }

    private async Task CancelStructuredExecutionAsync(StructuredExecutionState state)
    {
        if (state.Completion.Task.IsCompleted)
        {
            return;
        }

        state.RequestCancellation();

        DeveloperDiagnostics.LogInfo("Phase2CProof", "STRUCTURED_CANCEL_REQUESTED.", new Dictionary<string, object?>
        {
            ["requestId"] = state.RequestId,
            ["processId"] = ProcessId,
            ["sessionGeneration"] = state.SessionGeneration
        });
        try
        {
            await SendInterruptAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("Phase2CProof", ex, "Authoritative structured cancellation interrupt failed.", new Dictionary<string, object?>
            {
                ["requestId"] = state.RequestId,
                ["processId"] = ProcessId,
                ["sessionGeneration"] = state.SessionGeneration
            });
        }
    }

    internal sealed class StructuredExecutionState
    {
        private readonly object _syncRoot = new();
        private readonly StringBuilder _pending = new();
        private bool _begun;
        private bool _completed;
        private bool _suppressBeginLineEnding;

        public StructuredExecutionState(Guid requestId, int sessionGeneration, string beginMarker, string endPrefix, string expectedInvocation)
        {
            RequestId = requestId;
            SessionGeneration = sessionGeneration;
            BeginMarker = beginMarker;
            EndPrefix = endPrefix;
            ExpectedInvocation = expectedInvocation;
            _begun = string.IsNullOrEmpty(beginMarker);
            Completion = new TaskCompletionSource<StructuredMarkerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Guid RequestId { get; }
        public int SessionGeneration { get; }
        public string BeginMarker { get; }
        public string EndPrefix { get; }
        public string ExpectedInvocation { get; }
        public TaskCompletionSource<StructuredMarkerResult> Completion { get; }

        public bool CancellationRequested { get; private set; }

        public void RequestCancellation()
        {
            lock (_syncRoot)
            {
                CancellationRequested = true;
            }
        }

        public IReadOnlyList<string> Process(string text)
        {
            lock (_syncRoot)
            {
                _pending.Append(text);
                var output = new List<string>();
                while (true)
                {
                    if (_suppressBeginLineEnding)
                    {
                        RemoveFollowingLineBreak();
                        if (_pending.Length == 0) break;
                        _suppressBeginLineEnding = false;
                    }

                    if (_completed)
                    {
                        if (_pending.Length > 0)
                        {
                            output.Add(_pending.ToString());
                            _pending.Clear();
                        }
                        break;
                    }

                    var snapshot = _pending.ToString();
                    if (!_begun)
                    {
                        var beginIndex = snapshot.IndexOf(BeginMarker, StringComparison.Ordinal);
                        if (beginIndex < 0)
                        {
                            break;
                        }

                        var preMarker = snapshot[..beginIndex];
                        var visiblePreMarker = RemoveExpectedInvocation(preMarker);
                        if (visiblePreMarker.Length > 0) output.Add(visiblePreMarker);
                        _pending.Remove(0, beginIndex + BeginMarker.Length);
                        _suppressBeginLineEnding = true;
                        RemoveFollowingLineBreak();
                        _begun = true;
                        continue;
                    }

                    var endIndex = snapshot.IndexOf(EndPrefix, StringComparison.Ordinal);
                    if (endIndex < 0)
                    {
                        EmitSafePrefix(output, EndPrefix.Length + 8);
                        break;
                    }

                    var statusStart = endIndex + EndPrefix.Length;
                    var lineEnd = snapshot.IndexOfAny(['\r', '\n'], statusStart);
                    if (lineEnd < 0)
                    {
                        break;
                    }

                    var status = snapshot[statusStart..lineEnd].Trim();
                    AddAndRemove(output, endIndex, lineEnd - endIndex + 1);
                    if (lineEnd + 1 < snapshot.Length && snapshot[lineEnd] == '\r' && snapshot[lineEnd + 1] == '\n')
                    {
                        _pending.Remove(0, 1);
                    }
                    Completion.TrySetResult(new StructuredMarkerResult(status, null));
                    _completed = true;
                    if (_pending.Length > 0)
                    {
                        output.Add(_pending.ToString());
                        _pending.Clear();
                    }
                    break;
                }

                return output;
            }
        }

        public string Flush()
        {
            lock (_syncRoot)
            {
                var remainder = _pending.ToString();
                _pending.Clear();
                _suppressBeginLineEnding = false;
                return remainder;
            }
        }

        private void EmitSafePrefix(List<string> output, int suffixLength)
        {
            var count = Math.Max(0, _pending.Length - suffixLength);
            if (count == 0) return;
            output.Add(_pending.ToString(0, count));
            _pending.Remove(0, count);
        }

        private void AddAndRemove(List<string> output, int index, int length)
        {
            if (index > 0) output.Add(_pending.ToString(0, index));
            _pending.Remove(0, Math.Min(_pending.Length, index + length));
        }

        private string RemoveExpectedInvocation(string text)
        {
            var removedOccurrenceCount = 0;
            var removedControlSequenceCount = 0;
            while (TryFindExpectedInvocation(text, out var invocationIndex, out var invocationEnd, out var controlSequenceCount))
            {
                text = text.Remove(invocationIndex, invocationEnd - invocationIndex);
                removedOccurrenceCount++;
                removedControlSequenceCount += controlSequenceCount;
            }

            if (removedOccurrenceCount == 0)
            {
                return text;
            }

            DeveloperDiagnostics.LogInfo("Phase2CProof", "STRUCTURED_INVOCATION_ECHO_SUPPRESSED.", new Dictionary<string, object?>
            {
                ["requestId"] = RequestId,
                ["sessionGeneration"] = SessionGeneration,
                ["invocationLength"] = ExpectedInvocation.Length,
                ["suppressedOccurrenceCount"] = removedOccurrenceCount,
                ["interleavedControlSequenceCount"] = removedControlSequenceCount,
                ["contentOmitted"] = true
            });

            // Preserve the Enter-generated line transition. It is terminal semantics,
            // not part of the internal invocation text being hidden.
            return text;
        }

        private bool TryFindExpectedInvocation(string text, out int invocationIndex, out int invocationEnd, out int controlSequenceCount)
        {
            invocationIndex = -1;
            invocationEnd = -1;
            controlSequenceCount = 0;

            if (text.Length < ExpectedInvocation.Length)
            {
                return false;
            }

            for (var candidate = 0; candidate < text.Length; candidate++)
            {
                if (text[candidate] != ExpectedInvocation[0])
                {
                    continue;
                }

                var textIndex = candidate;
                var expectedIndex = 0;
                var skippedControls = 0;
                while (expectedIndex < ExpectedInvocation.Length && textIndex < text.Length)
                {
                    if (text[textIndex] == '\u001b' && TryGetAnsiControlSequenceLength(text, textIndex, out var controlLength))
                    {
                        textIndex += controlLength;
                        skippedControls++;
                        continue;
                    }

                    if (text[textIndex] != ExpectedInvocation[expectedIndex])
                    {
                        break;
                    }

                    textIndex++;
                    expectedIndex++;
                }

                if (expectedIndex == ExpectedInvocation.Length)
                {
                    invocationIndex = candidate;
                    invocationEnd = textIndex;
                    controlSequenceCount = skippedControls;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetAnsiControlSequenceLength(string text, int start, out int length)
        {
            length = 0;
            if (start + 1 >= text.Length || text[start] != '\u001b' || text[start + 1] != '[')
            {
                return false;
            }

            for (var index = start + 2; index < text.Length; index++)
            {
                var character = text[index];
                if (character >= '@' && character <= '~')
                {
                    length = index - start + 1;
                    return true;
                }
            }

            return false;
        }

        private void RemoveFollowingLineBreak()
        {
            while (_pending.Length > 0 && (_pending[0] == '\r' || _pending[0] == '\n'))
            {
                _pending.Remove(0, 1);
            }
        }
    }

    internal sealed class StructuredEchoFilter
    {
        private readonly Guid _requestId;
        private readonly string[] _expectedForms;
        private readonly string[] _normalizedExpectedForms;
        private readonly int _retainedSuffixLength;
        private readonly StringBuilder _pending = new();
        private int _suppressedCharacters;
        private int _promptPrefixedSuppressedCharacters;
        private int _traceCallCount;

        public StructuredEchoFilter(Guid requestId, params string[] scriptContents)
        {
            _requestId = requestId;
            _expectedForms = scriptContents
                .Where(static value => !string.IsNullOrEmpty(value))
                .SelectMany(static value =>
                {
                    var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                    return new[] { value, normalized, normalized.Replace("\n", "\r\n", StringComparison.Ordinal) };
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            _normalizedExpectedForms = _expectedForms
                .Select(static value => NormalizeEchoText(value))
                .Where(static value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            _retainedSuffixLength = Math.Max(32, _expectedForms.DefaultIfEmpty(string.Empty).Max(static value => value.Length));
        }

        public string Filter(string text)
        {
            if (_traceCallCount++ < 8)
            {
                TerminalStartupTrace.Write("PSES_ECHO_FILTER_INPUT", $"requestId={_requestId}; length={text.Length}; preview={DeveloperDiagnostics.SanitizePreview(text, 120)}");
            }
            _pending.Append(text);
            SuppressCompleteEchoes();
            if (IsAwaitingPromptPrefixedNormalizedEcho(_pending.ToString()))
            {
                return string.Empty;
            }
            var emitLength = Math.Max(0, _pending.Length - _retainedSuffixLength);
            if (emitLength == 0)
            {
                return string.Empty;
            }

            var visible = _pending.ToString(0, emitLength);
            _pending.Remove(0, emitLength);
            if (_traceCallCount <= 8)
            {
                TerminalStartupTrace.Write("PSES_ECHO_FILTER_OUTPUT", $"requestId={_requestId}; length={visible.Length}; preview={DeveloperDiagnostics.SanitizePreview(visible, 120)}");
            }
            return visible;
        }

        public string Flush()
        {
            SuppressCompleteEchoes();
            var visible = _pending.ToString();
            _pending.Clear();
            if (_suppressedCharacters > 0)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Structured editor echo was suppressed from the terminal renderer.", new Dictionary<string, object?>
                {
                    ["requestId"] = _requestId,
                    ["suppressedCharacterCount"] = _suppressedCharacters,
                    ["promptPrefixedSuppressedCharacterCount"] = _promptPrefixedSuppressedCharacters,
                    ["contentOmitted"] = true
                });
            }
            return visible;
        }

        private void SuppressCompleteEchoes()
        {
            // Never remove a script body merely because it matches the submitted
            // editor text. A legitimate script is allowed to print its own source.
            // The only proven non-user payload is the prompt-prefixed PSReadLine
            // redraw emitted by the interactive PSES host.
            while (true)
            {
                var promptPrefixedMatch = FindPromptPrefixedEcho(_pending.ToString());
                if (promptPrefixedMatch.Length == 0)
                {
                    promptPrefixedMatch = FindPromptPrefixedNormalizedEcho(_pending.ToString());
                    if (promptPrefixedMatch.Length == 0)
                    {
                        return;
                    }
                }

                _pending.Remove(promptPrefixedMatch.Index, promptPrefixedMatch.Length);
                _suppressedCharacters += promptPrefixedMatch.Length;
                _promptPrefixedSuppressedCharacters += promptPrefixedMatch.Length;
            }
        }

        private (int Index, int Length) FindPromptPrefixedEcho(string pending)
        {
            foreach (var expected in _expectedForms)
            {
                var firstLineLength = expected.IndexOf('\n');
                var firstLine = firstLineLength >= 0 ? expected[..firstLineLength] : expected;
                if (firstLine.Length == 0)
                {
                    continue;
                }

                var searchFrom = 0;
                while (searchFrom < pending.Length)
                {
                    var firstLineIndex = pending.IndexOf(firstLine, searchFrom, StringComparison.Ordinal);
                    if (firstLineIndex < 0)
                    {
                        break;
                    }

                    var lineStart = pending.LastIndexOf('\n', Math.Max(0, firstLineIndex - 1));
                    lineStart = lineStart < 0 ? 0 : lineStart + 1;
                    var prefix = pending[lineStart..firstLineIndex];
                    if (prefix.Contains('>') && pending.AsSpan(firstLineIndex).StartsWith(expected.AsSpan(), StringComparison.Ordinal))
                    {
                        return (lineStart, firstLineIndex + expected.Length - lineStart);
                    }

                    searchFrom = firstLineIndex + firstLine.Length;
                }
            }

            return (0, 0);
        }

        private (int Index, int Length) FindPromptPrefixedNormalizedEcho(string pending)
        {
            foreach (var expected in _normalizedExpectedForms)
            {
                var firstToken = expected[..Math.Min(5, expected.Length)];
                if (firstToken.Length == 0) continue;

                var searchFrom = 0;
                while (searchFrom < pending.Length)
                {
                    var tokenIndex = pending.IndexOf(firstToken, searchFrom, StringComparison.Ordinal);
                    if (tokenIndex < 0) break;
                    var lineStart = pending.LastIndexOf('\n', Math.Max(0, tokenIndex - 1));
                    lineStart = lineStart < 0 ? 0 : lineStart + 1;
                    if (!pending[lineStart..tokenIndex].Contains('>'))
                    {
                        searchFrom = tokenIndex + firstToken.Length;
                        continue;
                    }

                    var normalized = NormalizeEchoText(pending[tokenIndex..]);
                    if (!normalized.StartsWith(expected, StringComparison.Ordinal))
                    {
                        // ConPTY/PSReadLine may redraw a submitted line with cursor
                        // movement and wrapped fragments that do not preserve a
                        // byte-for-byte normalized prefix. Once the prompt and the
                        // first script token are proven, the editor payload length
                        // still gives us a safe boundary for removing that echo.
                        var lengthByPayload = CountRawCharactersForNormalizedLength(pending[tokenIndex..], expected.Length);
                        if (lengthByPayload > 0)
                        {
                            return (lineStart, tokenIndex + lengthByPayload - lineStart);
                        }

                        searchFrom = tokenIndex + firstToken.Length;
                        continue;
                    }

                    var rawLength = CountRawCharactersForNormalizedLength(pending[tokenIndex..], expected.Length);
                    if (rawLength > 0)
                    {
                        return (lineStart, tokenIndex + rawLength - lineStart);
                    }
                    searchFrom = tokenIndex + firstToken.Length;
                }
            }

            return (0, 0);
        }

        private bool IsAwaitingPromptPrefixedNormalizedEcho(string pending)
        {
            foreach (var expected in _normalizedExpectedForms)
            {
                var firstToken = expected[..Math.Min(5, expected.Length)];
                var tokenIndex = pending.IndexOf(firstToken, StringComparison.Ordinal);
                if (tokenIndex < 0) continue;

                var lineStart = pending.LastIndexOf('\n', Math.Max(0, tokenIndex - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                if (!pending[lineStart..tokenIndex].Contains('>')) continue;

                var normalized = NormalizeEchoText(pending[tokenIndex..]);
                if (normalized.Length < expected.Length && expected.StartsWith(normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeEchoText(string value)
        {
            var builder = new StringBuilder(value.Length);
            var inAnsiSequence = false;
            foreach (var character in value)
            {
                if (inAnsiSequence)
                {
                    if (character is >= '@' and <= '~') inAnsiSequence = false;
                    continue;
                }

                if (character == '\u001b')
                {
                    inAnsiSequence = true;
                    continue;
                }

                if (!char.IsControl(character) && !char.IsWhiteSpace(character)) builder.Append(character);
            }
            return builder.ToString();
        }

        private static int CountRawCharactersForNormalizedLength(string value, int normalizedLength)
        {
            var count = 0;
            var rawIndex = 0;
            var inAnsiSequence = false;
            while (rawIndex < value.Length && count < normalizedLength)
            {
                var character = value[rawIndex];
                if (inAnsiSequence)
                {
                    if (character is >= '@' and <= '~') inAnsiSequence = false;
                    rawIndex++;
                    continue;
                }

                if (character == '\u001b')
                {
                    inAnsiSequence = true;
                    rawIndex++;
                    continue;
                }

                if (!char.IsControl(character) && !char.IsWhiteSpace(character)) count++;
                rawIndex++;
            }
            return count == normalizedLength ? rawIndex : 0;
        }
    }

    internal sealed record StructuredMarkerResult(string Status, string? ErrorMessage);
}
