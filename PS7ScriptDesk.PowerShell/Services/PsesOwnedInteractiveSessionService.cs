using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Phase 2C foundation provider. PSES is the process/host owner and its
/// console-repl stdio is the terminal transport. This type intentionally does
/// not create a second PowerShell process for debugging.
/// </summary>
public sealed class PsesOwnedInteractiveSessionService : ILiveConsoleService, ICorrelatedLiveConsoleScriptExecution, IRuntimeSessionIdentityAware, IStructuredPromptReadinessSource, IAuthoritativeSessionStructuredExecution, IAuthoritativePsesDebugEndpoint, IAuthoritativeLiveCompletionProvider
{
    private const string FallbackPsesHostVersion = "2025.4.0";
    private const int StartupOutputCaptureLimit = 32 * 1024;
    private static readonly TimeSpan PromptRecoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex AnsiSequenceRegex = new("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CursorColumnOneRegex = new("\\x1B\\[[0-9]+;1[Hf]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
    // Read once when the shared PSES session is created. A request cannot switch
    // bridge midway through execution if the process environment is changed.
    private readonly ExperimentalRunFileBridgeFeatureGate _experimentalRunFileBridge;
    private readonly ExperimentalRunSelectionBridgeFeatureGate _experimentalRunSelectionBridge;
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
    private InputOutputProbe? _pendingInputOutputProbe;
    private StructuredEchoFilter? _activeStructuredEchoFilter;
    private Guid? _activeStructuredRequestId;
    private Guid _runtimeSessionIdentity;
    private long? _runtimeGeneration;
    private DebuggerOwnershipSnapshot _debuggerOwnership = new(
        Guid.Empty, null, 0, null, null, DebuggerLifecycleObservation.Unknown, false);
    private StructuredExecutionState? _activeStructuredExecutionState;
    private long _promptReadyObservation;
    private readonly PromptRecoveryFrameDiagnostics _promptFrameDiagnostics = new();
    private int _promptRecoveryGeneration;
    private TaskCompletionSource<long>? _promptRecoveryWaiter;
    private string _promptRecognitionBuffer = string.Empty;
    private string _promptAnsiCarry = string.Empty;
    private string _cursorVisibilitySequenceTail = string.Empty;
    private bool _interactiveReadinessUnverified;
    private PendingInteractiveCommand? _pendingInteractiveCommand;
    private bool _cursorVisible;
    private event Action<int, bool>? CursorVisibilityChanged;
    private FileStream? _rawConPtyCapture;
    private StreamWriter? _rawConPtyMetadata;
    private long _rawConPtyOffset;
    private Guid _rawConPtyCorrelationId;
    private long _resizeDiagnosticSequence;
    private int _lastDiagnosticColumns;
    private int _lastDiagnosticRows;
    private int _lastResizeDiagnosticGeneration = -1;

    public PsesOwnedInteractiveSessionService(
        ExperimentalRunFileBridgeFeatureGate? experimentalRunFileBridge = null,
        ExperimentalRunSelectionBridgeFeatureGate? experimentalRunSelectionBridge = null)
    {
        _experimentalRunFileBridge = experimentalRunFileBridge ?? ExperimentalRunFileBridgeFeatureGate.FromEnvironment();
        _experimentalRunSelectionBridge = experimentalRunSelectionBridge ?? ExperimentalRunSelectionBridgeFeatureGate.FromEnvironment();
    }

    public bool IsSessionRunning => _conPty is { HasExited: false } && !_sessionFaulted;
    public bool IsInputWritable => IsSessionRunning && !_shutdownRequested && !_interactiveReadinessUnverified;
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
    public int CurrentSessionGeneration => SessionGeneration;
    public void SetRuntimeSessionIdentity(Guid sessionId, long generation)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A nonempty runtime session identity is required.", nameof(sessionId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_syncRoot)
        {
            var identityChanged = _runtimeSessionIdentity != sessionId || _runtimeGeneration != generation;
            _runtimeSessionIdentity = sessionId;
            _runtimeGeneration = generation;
            if (identityChanged)
            {
                _debuggerOwnership = new(sessionId, generation, _generation, ProcessId, null,
                    DebuggerLifecycleObservation.Unknown, false);
            }
            else if (_debuggerOwnership.TerminalGeneration != _generation &&
                     _debuggerOwnership.State is DebuggerLifecycleObservation.Unknown or DebuggerLifecycleObservation.Stale or DebuggerLifecycleObservation.None or DebuggerLifecycleObservation.Terminated)
            {
                _debuggerOwnership = new(sessionId, generation, _generation, ProcessId, null,
                    DebuggerLifecycleObservation.None, true);
            }
            _languageServiceClient?.SetRuntimeSessionIdentity(sessionId, generation);
        }
    }
    public DateTimeOffset? ProcessStartTimeUtc => _conPty is { HasExited: false } p ? p.ProcessStartTimeUtc : null;
    public int SessionGeneration => _generation;
    public string? CurrentWorkingDirectory => _workingDirectory;
    public string? SessionDetailsPath => _sessionDetailsPath;

    /// <summary>Filters only the prompt-prefixed invocation used to launch one debugger snapshot.</summary>
    public IDisposable RegisterDebuggerLaunchEcho(Guid debugSessionId, string snapshotPath)
    {
        if (debugSessionId == Guid.Empty) throw new ArgumentException("A debugger session ID is required.", nameof(debugSessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        var generation = CurrentSessionGeneration;
        var invocation = ". " + QuotePowerShellSingleQuotedString(snapshotPath);
        var filter = new StructuredEchoFilter(debugSessionId, preservePromptPrefix: true, BuildInvocationEchoForms(invocation, executeInCurrentScope: true));
        filter.MarkDebuggerLaunchEcho();
        lock (_syncRoot)
        {
            if (_activeStructuredEchoFilter is not null)
                throw new InvalidOperationException("A terminal echo filter is already active.");
            _activeStructuredEchoFilter = filter;
        }
        DeveloperDiagnostics.LogInfo("Debugger", "Registered exact prompt-anchored debugger launch echo filter.", new Dictionary<string, object?>
        {
            ["debugSessionId"] = debugSessionId,
            ["expectedCommandIdentity"] = debugSessionId,
            ["terminalGeneration"] = generation,
            ["snapshotPathLength"] = snapshotPath.Length,
            ["echoFormCount"] = BuildInvocationEchoForms(invocation, executeInCurrentScope: true).Length,
            ["contentOmitted"] = true
        });
        return new DebuggerLaunchEchoRegistration(this, filter, generation);
    }

    private void ReleaseDebuggerLaunchEchoFilter(StructuredEchoFilter filter, int generation)
    {
        string remainder = string.Empty;
        var wasActive = false;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeStructuredEchoFilter, filter))
            {
                wasActive = true;
                remainder = filter.Flush();
                _activeStructuredEchoFilter = null;
            }
        }
        DeveloperDiagnostics.LogInfo("Debugger", "Debugger launch echo filter disposed.", new Dictionary<string, object?>
        {
            ["debugSessionId"] = filter.RequestId,
            ["terminalGeneration"] = generation,
            ["filterWasActive"] = wasActive,
            ["matchedEchoCount"] = filter.MatchedEchoCount,
            ["candidateEchoObserved"] = filter.CandidateEchoObserved,
            ["candidateMatchOutcome"] = filter.CandidateEchoObserved
                ? filter.MatchedEchoCount > 0 ? "Match" : "UnmatchedAtDispose"
                : "NoCandidate",
            ["unmatchedRemainderLength"] = remainder.Length,
            ["unmatchedRemainderPublished"] = !string.IsNullOrEmpty(remainder),
            ["contentOmitted"] = true
        });
        if (!string.IsNullOrEmpty(remainder))
            PublishVisibleOutput(ExecutionOutputStreamKind.StandardOutput, generation, remainder);
    }

    private sealed class DebuggerLaunchEchoRegistration : IDisposable
    {
        private PsesOwnedInteractiveSessionService? _owner;
        private readonly StructuredEchoFilter _filter;
        private readonly int _generation;
        public DebuggerLaunchEchoRegistration(PsesOwnedInteractiveSessionService owner, StructuredEchoFilter filter, int generation)
        { _owner = owner; _filter = filter; _generation = generation; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseDebuggerLaunchEchoFilter(_filter, _generation);
    }
    public string? LanguageServicePipeName => _languageServicePipeName;
    public string? DebugServicePipeName => _debugServicePipeName;
    public DebuggerOwnershipSnapshot GetDebuggerOwnershipSnapshot()
    {
        lock (_syncRoot)
        {
            var snapshot = _debuggerOwnership;
            var matchesCurrent = snapshot.TerminalGeneration == _generation &&
                snapshot.RuntimeSessionId == _runtimeSessionIdentity && snapshot.RuntimeGeneration == _runtimeGeneration &&
                snapshot.ProcessId == ProcessId;
            return matchesCurrent ? snapshot : snapshot with { State = DebuggerLifecycleObservation.Stale, IsValid = false };
        }
    }

    public bool ReportDebuggerLifecycle(Guid debuggerSessionId, DebuggerLifecycleObservation state)
    {
        if (debuggerSessionId == Guid.Empty) return false;
        lock (_syncRoot)
        {
            var current = _debuggerOwnership;
            if (!IsSessionRunning || _runtimeGeneration is not > 0 ||
                !DebuggerOwnershipTransitionPolicy.TryApplyObservation(current, _runtimeSessionIdentity,
                    _runtimeGeneration.Value, _generation, ProcessId, debuggerSessionId, state, out var next))
            {
                DeveloperDiagnostics.LogDecision("Debugger", "DebuggerOwnershipObservation",
                    "Lifecycle observation was rejected because its identity was stale or another debugger still owned the current session.", "Rejected",
                    new Dictionary<string, object?>
                    {
                        ["incomingDebuggerSessionId"] = debuggerSessionId,
                        ["incomingState"] = state.ToString(),
                        ["currentDebuggerSessionId"] = current.DebuggerSessionId,
                        ["currentState"] = current.State.ToString(),
                        ["currentSnapshotValid"] = current.IsValid,
                        ["terminalGeneration"] = _generation,
                        ["runtimeGeneration"] = _runtimeGeneration,
                        ["processId"] = ProcessId
                    });
                return false;
            }
            _debuggerOwnership = next;
        }

        if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
        {
            var snapshot = GetDebuggerOwnershipSnapshot();
            ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("DebuggerOwnershipObserved",
                DebuggerSessionId: debuggerSessionId, RuntimeSessionId: snapshot.RuntimeSessionId,
                RuntimeGeneration: snapshot.RuntimeGeneration, TerminalGeneration: snapshot.TerminalGeneration,
                ProcessId: snapshot.ProcessId, Outcome: snapshot.State.ToString(), DebuggerState: snapshot.State.ToString(),
                DebuggerMayOwnSession: snapshot.MayOwnSession, IdentityValid: snapshot.IsValid,
                EventSource: "DAP", EventType: "DebuggerLifecycleObservation",
                CorrelationConfidence: snapshot.IsValid ? "GenerationBound" : "Stale"));
        }
        return true;
    }
    public bool IsLiveCompletionAvailable => _languageServiceClient is { IsInitialized: true } && IsSessionRunning;

    public async Task<AuthoritativeCompletionResult?> GetLiveCompletionsAsync(
        string documentUri,
        string documentText,
        int cursorOffset,
        CancellationToken cancellationToken = default)
    {
        var client = _languageServiceClient;
        var generation = SessionGeneration;
        if (client is null || !IsLiveCompletionAvailable)
        {
            return null;
        }

        var result = await client.GetLiveCompletionsAsync(documentUri, documentText, cursorOffset, cancellationToken)
            .ConfigureAwait(false);
        if (generation != SessionGeneration || !ReferenceEquals(client, _languageServiceClient) || !IsSessionRunning)
        {
            DeveloperDiagnostics.LogInfo("EditorCompletion", "Discarded completion from a replaced PSES session.",
                new Dictionary<string, object?>
                {
                    ["requestGeneration"] = generation,
                    ["currentGeneration"] = SessionGeneration
                });
            return null;
        }

        return result;
    }

    public Task CloseDocumentAsync(string documentUri, CancellationToken cancellationToken = default)
    {
        var client = _languageServiceClient;
        return client is { IsInitialized: true }
            ? client.CloseDocumentAsync(documentUri, cancellationToken)
            : Task.CompletedTask;
    }

    public event Action? ScriptExecutionCompleted;
    public event Action<int>? CommandExecutionCompleted;
    public event Action<LiveConsoleCommandCompletion>? InteractiveCommandCompleted;
    public event Action? SessionTerminated;
    public event Action<int>? TerminalSessionStarted;
    public event Action<int>? TerminalSessionStopping;
    public event Action<int, string>? PromptReadyObserved;
    public event Action<StructuredPromptReadyObservation>? StructuredPromptReadyObserved;
    public event Action<int, string>? RawOutputReceived;
    internal event Action<int, string>? OutputObservedBeforeDisplayFiltering;

    public void AttachHost(IntPtr hostHandle, int width, int height) { }
    public void ResizeHost(int width, int height) { }
    public void ResizeConsole(int cols, int rows)
    {
        var resizeSequence = Interlocked.Increment(ref _resizeDiagnosticSequence);
        var generation = SessionGeneration;
        var processId = ProcessId;
        var resizeOwner = _conPty;
        try
        {
            resizeOwner?.Resize(cols, rows);
            if (DeveloperDiagnostics.IsEnabled)
            {
                DeveloperDiagnostics.LogInfo("PSES", "NativeConPtyResizeResult", new Dictionary<string, object?>
                {
                    ["resizeSequence"] = resizeSequence,
                    ["sessionGeneration"] = generation,
                    ["requestedColumns"] = cols,
                    ["requestedRows"] = rows,
                    ["previousColumns"] = _lastResizeDiagnosticGeneration == generation ? _lastDiagnosticColumns : null,
                    ["previousRows"] = _lastResizeDiagnosticGeneration == generation ? _lastDiagnosticRows : null,
                    ["nativeResizeAttempted"] = resizeOwner is not null,
                    ["nativeResizeSucceeded"] = resizeOwner is not null,
                    ["processId"] = processId,
                    ["errorType"] = null,
                    ["errorCode"] = null,
                    ["contentOmitted"] = true
                });
            }
            if (resizeOwner is not null)
            {
                _lastDiagnosticColumns = cols;
                _lastDiagnosticRows = rows;
                _lastResizeDiagnosticGeneration = generation;
            }
            DeveloperDiagnostics.LogInfo("PSES", "Resized authoritative PSES ConPTY.", new Dictionary<string, object?>
            {
                ["columns"] = cols, ["rows"] = rows, ["processId"] = ProcessId
            });
        }
        catch (Exception ex)
        {
            if (DeveloperDiagnostics.IsEnabled)
            {
                DeveloperDiagnostics.LogInfo("PSES", "NativeConPtyResizeResult", new Dictionary<string, object?>
                {
                    ["resizeSequence"] = resizeSequence,
                    ["sessionGeneration"] = generation,
                    ["requestedColumns"] = cols,
                    ["requestedRows"] = rows,
                    ["previousColumns"] = _lastResizeDiagnosticGeneration == generation ? _lastDiagnosticColumns : null,
                    ["previousRows"] = _lastResizeDiagnosticGeneration == generation ? _lastDiagnosticRows : null,
                    ["nativeResizeAttempted"] = resizeOwner is not null,
                    ["nativeResizeSucceeded"] = false,
                    ["processId"] = processId,
                    ["errorType"] = ex.GetType().FullName,
                    ["errorCode"] = ex.HResult,
                    ["contentOmitted"] = true
                });
            }
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
            _pendingInteractiveCommand = null;
            _commandInProgress = false;
            _shutdownRequested = false;
            _sessionFaulted = false;
            _debuggerOwnership = _runtimeSessionIdentity != Guid.Empty && _runtimeGeneration is > 0
                ? new(_runtimeSessionIdentity, _runtimeGeneration, _generation, process.Id, null,
                    DebuggerLifecycleObservation.None, true)
                : new(_runtimeSessionIdentity, _runtimeGeneration, _generation, process.Id, null,
                    DebuggerLifecycleObservation.Unknown, false);
            _promptReadyObservation = 0;
            _promptRecoveryGeneration = _generation;
            _promptRecoveryWaiter = null;
            _promptRecognitionBuffer = string.Empty;
            _promptAnsiCarry = string.Empty;
            _cursorVisibilitySequenceTail = string.Empty;
            _interactiveReadinessUnverified = false;
            _cursorVisible = false;
            _pendingInputOutputProbe = null;
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
            var languageServiceFaultHandler = new Action<Exception>(exception =>
            {
                if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                    ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("PsesSessionFaulted",
                        TerminalGeneration: generation, ProcessId: process.Id, EventSource: "PSES/LSP",
                        EventType: "SessionFault", EventOutcome: "Faulted", CorrelationConfidence: "GlobalObservation",
                        Reason: exception.GetType().Name));
                OnLanguageServiceFaulted(languageServiceClient!, generation, exception);
            });
            languageServiceClient.SessionFaulted += languageServiceFaultHandler;
            await languageServiceClient.InitializeAsync(languageServicePipeName, process.Id, generation,
                (data, token) => SendSyntheticTerminalInputAsync(generation, data, token), cancellationToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (_runtimeSessionIdentity != Guid.Empty && _runtimeGeneration is { } runtimeGeneration)
                    languageServiceClient.SetRuntimeSessionIdentity(_runtimeSessionIdentity, runtimeGeneration);
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
        if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
        {
            Guid runtimeSessionIdentity; long? runtimeGeneration;
            lock (_syncRoot) { runtimeSessionIdentity = _runtimeSessionIdentity; runtimeGeneration = _runtimeGeneration; }
            ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("TerminalGenerationCreated",
                RuntimeSessionId: runtimeSessionIdentity == Guid.Empty ? null : runtimeSessionIdentity,
                RuntimeGeneration: runtimeGeneration, TerminalGeneration: generation, ProcessId: process.Id,
                ProcessStartUtcTicks: ProcessStartTimeUtc?.UtcTicks, EventSource: "ConPTY/PSES",
                EventType: "TerminalGenerationCreated", CorrelationConfidence: "GenerationBound"));
        }
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
            NotifyPromptReadyObserved(generation, _workingDirectory ?? string.Empty);
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
        ConPtyProcess owner;
        int generation;
        lock (_syncRoot)
        {
            if (!IsInputWritable || _conPty is null)
                throw new InvalidOperationException("The PSES console-repl input is not writable.");
            owner = _conPty;
            generation = _generation;
        }
        return WriteInputCoreAsync(data, cancellationToken, owner, generation);
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

    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(string commandText, Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken = default) =>
        ExecuteConsoleCommandAsync(commandText, onOutput, Guid.NewGuid(), cancellationToken);

    public Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(string commandText, Action<ExecutionOutputRecord> onOutput, Guid requestId, CancellationToken cancellationToken = default)
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
        return ExecuteTextAsync(commandText, onOutput, requestId, cancellationToken);
    }

    public Task<LiveConsoleCommandResult> ExecuteScriptAsync(string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope = false, CancellationToken cancellationToken = default)
        => ExecuteScriptForRequestAsync(Guid.NewGuid(), documentDisplayName, scriptContent, onOutput, executeInCurrentScope, cancellationToken);

    private async Task<AuthoritativeStructuredExecutionResult> ExecuteNativeSelectionAsync(
        Guid requestId, int sessionGeneration, string scriptContent,
        Action<ExecutionOutputRecord> onOutput, CancellationToken cancellationToken)
    {
        if (sessionGeneration != SessionGeneration || !IsSessionRunning)
            return new(EditorExecutionStatus.Rejected, CurrentWorkingDirectory,
                "The interactive session changed before Run Selection.", false);

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution");
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotPath = Path.Combine(snapshotDirectory, "selection-" + requestId.ToString("N", CultureInfo.InvariantCulture) + ".ps1");
        var completionMarkerPath = Path.Combine(snapshotDirectory, "selection-" + requestId.ToString("N", CultureInfo.InvariantCulture) + ".completed");
        TryDeleteFile(completionMarkerPath);
        TryDeleteFile(completionMarkerPath + ".tmp");
        var completion = new TaskCompletionSource<LiveConsoleCommandCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var markerWatcher = new FileSystemWatcher(snapshotDirectory, Path.GetFileName(completionMarkerPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        FileSystemEventHandler onMarkerChanged = (_, _) => ObserveNativeSelectionCompletionMarker(requestId, sessionGeneration);
        RenamedEventHandler onMarkerRenamed = (_, _) => ObserveNativeSelectionCompletionMarker(requestId, sessionGeneration);
        markerWatcher.Created += onMarkerChanged;
        markerWatcher.Changed += onMarkerChanged;
        markerWatcher.Renamed += onMarkerRenamed;
        markerWatcher.EnableRaisingEvents = true;
        using var cancellationRegistration = cancellationToken.Register(() => cancellationRequested.TrySetResult());
        var submitted = false;
        var submissionAttempted = false;
        var settled = false;
        StructuredEchoFilter? requestEchoFilter = null;
        void OnCompleted(LiveConsoleCommandCompletion observed)
        {
            if (observed.RequestId == requestId && observed.SessionGeneration == sessionGeneration)
                completion.TrySetResult(observed);
        }
        void OnStopping(int generation)
        {
            if (generation == sessionGeneration) sessionStopped.TrySetResult();
        }
        InteractiveCommandCompleted += OnCompleted;
        TerminalSessionStopping += OnStopping;
        try
        {
            await File.WriteAllTextAsync(snapshotPath, scriptContent, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            // A prompt can be observed while a submitted line is still waiting in
            // PSReadLine's input path. Keep the snapshot until PowerShell has
            // returned from the dot-source command and written this request marker.
            // try/finally does not introduce a scriptblock invocation scope, so the
            // selected code still runs in the interactive console scope.
            var invocation = BuildNativeSelectionInvocation(snapshotPath, completionMarkerPath, requestId);
            requestEchoFilter = new StructuredEchoFilter(requestId, preservePromptPrefix: true,
                BuildInvocationEchoForms(invocation, executeInCurrentScope: true));
            lock (_syncRoot)
            {
                if (_activeStructuredEchoFilter is not null)
                    throw new InvalidOperationException("A terminal echo filter is already active.");
                _activeStructuredEchoFilter = requestEchoFilter;
            }
            DeveloperDiagnostics.LogInfo("PSES", "Run Selection submitted through native interactive input.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId, ["sessionGeneration"] = sessionGeneration,
                ["processId"] = ProcessId, ["scriptLength"] = scriptContent.Length,
                ["snapshotPath"] = DeveloperDiagnostics.SanitizePath(snapshotPath),
                ["completionMarkerPath"] = DeveloperDiagnostics.SanitizePath(completionMarkerPath),
                ["completionGuard"] = "request-scoped-result-marker-and-fresh-prompt",
                ["echoFilter"] = "request-scoped-prompt-anchored-generated-invocation", ["contentOmitted"] = true
            });
            submissionAttempted = true;
            var submission = await ExecuteTextAsync(invocation, onOutput, requestId, cancellationToken, completionMarkerPath).ConfigureAwait(false);
            submitted = submission.Outcome == LiveConsoleCommandOutcome.Submitted;
            if (!submitted || submission.SessionGeneration != sessionGeneration)
                return new(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                    "Native Run Selection submission was not verified.", false, true);

            var winner = await Task.WhenAny(completion.Task, sessionStopped.Task, cancellationRequested.Task).ConfigureAwait(false);
            if (winner == completion.Task || completion.Task.IsCompletedSuccessfully)
            {
                var observed = await completion.Task.ConfigureAwait(false);
                settled = true;
                var interrupted = observed.Outcome == LiveConsoleCommandOutcome.Interrupted;
                var failed = observed.Outcome == LiveConsoleCommandOutcome.ExecutionFailed;
                return new(interrupted ? EditorExecutionStatus.Cancelled : failed ? EditorExecutionStatus.Failed : EditorExecutionStatus.Completed,
                    CurrentWorkingDirectory,
                    interrupted ? "Run Selection was interrupted." : failed ? "Run Selection completed with an error." : null,
                    true);
            }
            if (winner == sessionStopped.Task)
                return new(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                    "The native session stopped before Run Selection completed.", false, true);

            bool ownsPendingCommand;
            lock (_syncRoot)
                ownsPendingCommand = _pendingInteractiveCommand is { } pending &&
                    pending.RequestId == requestId && pending.Generation == sessionGeneration;
            if (!ownsPendingCommand)
                return new(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                    "Run Selection cancellation raced completion; native readiness was not verified.", false, true);

            var recovery = await StopConsoleForGenerationAsync(sessionGeneration).ConfigureAwait(false);
            if (recovery.Outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt)
            {
                settled = true;
                return new(EditorExecutionStatus.Cancelled, CurrentWorkingDirectory,
                    "Run Selection was interrupted in the native console.", true);
            }
            return new(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                "Run Selection cancellation could not verify prompt recovery.", false, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!submitted) return submissionAttempted
                ? new(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                    "Run Selection cancellation raced native submission.", false, true)
                : new(EditorExecutionStatus.Cancelled, CurrentWorkingDirectory,
                    "Run Selection was cancelled before native submission.", false);
            var recovery = await StopConsoleForGenerationAsync(sessionGeneration).ConfigureAwait(false);
            settled = recovery.Outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt;
            return new(settled ? EditorExecutionStatus.Cancelled : EditorExecutionStatus.Unknown,
                CurrentWorkingDirectory, settled ? "Run Selection was interrupted." : "Native cancellation was not verified.", settled, !settled);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Native Run Selection failed before verified completion.",
                new Dictionary<string, object?> { ["requestId"] = requestId, ["sessionGeneration"] = sessionGeneration,
                    ["submissionAttempted"] = submissionAttempted, ["contentOmitted"] = true });
            return new(EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                "Native Run Selection completion was not verified: " + ex.Message, false, submissionAttempted);
        }
        finally
        {
            InteractiveCommandCompleted -= OnCompleted;
            TerminalSessionStopping -= OnStopping;
            string echoRemainder = string.Empty;
            lock (_syncRoot)
            {
                if (requestEchoFilter is not null && ReferenceEquals(_activeStructuredEchoFilter, requestEchoFilter))
                {
                    echoRemainder = requestEchoFilter.Flush();
                    _activeStructuredEchoFilter = null;
                }
            }
            if (!string.IsNullOrEmpty(echoRemainder))
                PublishVisibleOutput(ExecutionOutputStreamKind.StandardOutput, sessionGeneration, echoRemainder);
            if (!submissionAttempted || settled)
            {
                TryDeleteFile(snapshotPath);
                TryDeleteFile(completionMarkerPath);
                TryDeleteFile(completionMarkerPath + ".tmp");
            }
            else DeveloperDiagnostics.LogWarning("PSES", "Run Selection snapshot retained because native completion was unverified.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId, ["sessionGeneration"] = sessionGeneration,
                ["snapshotPath"] = DeveloperDiagnostics.SanitizePath(snapshotPath),
                ["completionMarkerPath"] = DeveloperDiagnostics.SanitizePath(completionMarkerPath)
            });
            DeveloperDiagnostics.LogInfo("PSES", "Native Run Selection request observers were released.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["markerWatcherDisposed"] = true,
                ["matchedEchoCount"] = requestEchoFilter?.MatchedEchoCount ?? 0,
                ["unmatchedEchoRemainderLength"] = echoRemainder.Length,
                ["settled"] = settled,
                ["contentOmitted"] = true
            });
        }
    }

    public async Task<LiveConsoleCommandResult> ExecuteScriptForRequestAsync(Guid requestId, string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A nonempty execution request ID is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(scriptContent);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!IsSessionRunning || _languageServiceClient is null)
            throw new InvalidOperationException("The authoritative PSES session is not ready for editor execution.");

        if (executeInCurrentScope)
        {
            var started = DateTime.Now;
            var generation = SessionGeneration;
            var native = await ExecuteNativeSelectionAsync(requestId, generation, scriptContent, onOutput, cancellationToken).ConfigureAwait(false);
            if (native.Status is EditorExecutionStatus.Unknown or EditorExecutionStatus.Rejected)
                throw new InvalidOperationException(native.ErrorMessage ?? "Native Run Selection completion was not verified.");
            if (native.Status == EditorExecutionStatus.Failed)
                throw new InvalidOperationException(native.ErrorMessage ?? "Native Run Selection reported an execution error.");
            ScriptExecutionCompleted?.Invoke();
            CommandExecutionCompleted?.Invoke(generation);
            return new LiveConsoleCommandResult(documentDisplayName,
                native.Status == EditorExecutionStatus.Cancelled, CurrentWorkingDirectory,
                started, DateTime.Now, requestId, generation, ProcessId,
                native.Status switch
                {
                    EditorExecutionStatus.Completed => LiveConsoleCommandOutcome.Completed,
                    EditorExecutionStatus.Cancelled => LiveConsoleCommandOutcome.Interrupted,
                    _ => LiveConsoleCommandOutcome.RecoveryUnverified
                });
        }

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
                commandCancellation.Token,
                mode: EditorExecutionMode.ScriptCall).ConfigureAwait(false);

            var wasStopped = result.Status == EditorExecutionStatus.Cancelled || commandCancellation.IsCancellationRequested;
            var commandResult = new LiveConsoleCommandResult(
                documentDisplayName,
                wasStopped,
                CurrentWorkingDirectory,
                startedAt.LocalDateTime,
                DateTime.Now);
            ScriptExecutionCompleted?.Invoke();
            CommandExecutionCompleted?.Invoke(sessionGeneration);
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
        CancellationToken cancellationToken = default,
        EditorExecutionSourceInfo? sourceInfo = null,
        EditorExecutionMode mode = EditorExecutionMode.ScriptCall,
        bool directRunSelectionApproved = false,
        string? savedScriptPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDisplayName);
        ArgumentNullException.ThrowIfNull(scriptContent);
        ArgumentNullException.ThrowIfNull(onOutput);

        var correlation = ExecutionCorrelationTelemetry.Shared;
        if (correlation.IsEnabled)
            correlation.Record(new ExecutionCorrelationEvent("ServiceAdmissionRequested", RequestId: requestId,
                RequestCorrelation: "authoritative", TerminalGeneration: sessionGeneration,
                ProcessId: ProcessId, ProcessStartUtcTicks: ProcessStartTimeUtc?.UtcTicks));

        if (!IsSessionRunning || sessionGeneration != SessionGeneration)
        {
            if (correlation.IsEnabled)
                correlation.Record(new ExecutionCorrelationEvent("ServiceAdmissionDenied", RequestId: requestId,
                    TerminalGeneration: sessionGeneration, ProcessId: ProcessId, Reason: "session-unavailable-or-stale"));
            return new AuthoritativeStructuredExecutionResult(
                EditorExecutionStatus.Rejected,
                CurrentWorkingDirectory,
                "The authoritative PSES session is unavailable or belongs to a different generation.",
                CompletionObserved: false);
        }

        if (mode == EditorExecutionMode.RunSelection && executeInCurrentScope &&
            !_experimentalRunSelectionBridge.ShouldUseDirectBridge(mode, executeInCurrentScope, directRunSelectionApproved))
            return await ExecuteNativeSelectionAsync(requestId, sessionGeneration, scriptContent, onOutput, cancellationToken).ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        PsesStructuredEvaluationResult? protocolResult = null;
        var scriptOutcomeVerified = false;
        var foregroundRequested = false;
        var foregroundCompletionVerified = false;
        var foregroundHandoffStarted = false;
        var foregroundTerminationVerified = false;
        var foregroundEvaluationSubmitted = false;
        var stagedLauncherMayExist = false;
        var stagingOperationSettled = false;
        var phase = new StructuredExecutionPhaseTracker();
        var executionGateAcquired = false;
        string? snapshotPath = null;
        var snapshotCreated = false;
        FileStream? savedScriptReadLock = null;
        StructuredEchoFilter? requestEchoFilter = null;
        StructuredExecutionState? requestState = null;
        var statusMarkerId = requestId.ToString("N", CultureInfo.InvariantCulture);
        var statusVariable = "__ps7sd_status_" + statusMarkerId;
        var stagedScriptVariable = "__ps7sd_editor_" + statusMarkerId;
        var statusPath = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-" + statusMarkerId + ".status");
        var quotedStatusPath = QuotePowerShellSingleQuotedString(statusPath);

        try
        {
            try
            {
                await _structuredExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                executionGateAcquired = true;
                phase.AdvanceTo(StructuredExecutionPhase.DapStaging);
                if (correlation.IsEnabled)
                    correlation.Record(new ExecutionCorrelationEvent("ServiceGateAcquired", RequestId: requestId,
                        TerminalGeneration: sessionGeneration, ProcessId: ProcessId));
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
                _activeStructuredRequestId = requestId;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var savedFilePath = mode == EditorExecutionMode.ScriptCall && !executeInCurrentScope
                ? TryOpenMatchingSavedScript(savedScriptPath, scriptContent, out savedScriptReadLock)
                : null;
            if (savedFilePath is not null)
            {
                snapshotPath = savedFilePath;
                DeveloperDiagnostics.LogInfo("PSES", "Clean saved Run File selected its verified source path for execution.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["sourcePath"] = DeveloperDiagnostics.SanitizePath(savedFilePath),
                    ["sourceLength"] = scriptContent.Length,
                    ["sourceMatchesRequest"] = true,
                    ["writeLockHeld"] = true,
                    ["contentOmitted"] = true
                });
            }
            else
            {
                // The user text is an intact file-level PowerShell execution unit. In
                // particular, #Requires and advanced-script headers stay at the top.
                var snapshotDirectory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution");
                Directory.CreateDirectory(snapshotDirectory);
                snapshotPath = Path.Combine(snapshotDirectory, "editor-" + statusMarkerId + ".ps1");
                await using (var snapshotStream = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.Asynchronous))
                {
                    snapshotCreated = true;
                    await using var writer = new StreamWriter(snapshotStream, new UTF8Encoding(false));
                    await writer.WriteAsync(scriptContent.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                DeveloperDiagnostics.LogInfo("PSES", "Request-owned editor snapshot created for foreground execution.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["snapshotPath"] = DeveloperDiagnostics.SanitizePath(snapshotPath),
                    ["scriptLength"] = scriptContent.Length,
                    ["executeInCurrentScope"] = executeInCurrentScope,
                    ["savedPathCandidateProvided"] = !string.IsNullOrWhiteSpace(savedScriptPath),
                    ["contentOmitted"] = true
                });
            }
            var quotedSnapshotPath = QuotePowerShellSingleQuotedString(snapshotPath);
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
            DeveloperDiagnostics.LogInfo("PSES", "Request-scoped editor history exclusion was included in the private DAP stage before foreground submission.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["historyOwner"] = "PSReadLine",
                ["historyHandlerChained"] = true,
                ["matchScope"] = "exact-generated-editor-invocation",
                ["contentOmitted"] = true
            });
            var useExperimentalDirectRunSelection = _experimentalRunSelectionBridge.ShouldUseDirectBridge(mode, executeInCurrentScope, directRunSelectionApproved);
            var useExperimentalDirectRunFile = !useExperimentalDirectRunSelection &&
                mode != EditorExecutionMode.RunSelection &&
                _experimentalRunFileBridge.ShouldUseDirectBridge(executeInCurrentScope);
            var useExperimentalDirectBridge = useExperimentalDirectRunFile || useExperimentalDirectRunSelection;
            string? stagedExpression = null;
            if (!useExperimentalDirectBridge)
            {
                // DAP REPL evaluation runs inside PSES's PSReadLine idle handler.
                // It only stages this launcher; foreground LSP execution owns user IO.
                var innerInvocation = (executeInCurrentScope ? ". " : "& ") + quotedSnapshotPath;
                stagedExpression = PsesInteractiveHistoryExclusion.BuildRegistrationScript() +
                    "$global:" + stagedScriptVariable + " = {\n" +
                    $"${statusVariable} = $true\ntry {{\n" +
                    $"if (-not [System.IO.File]::Exists({quotedSnapshotPath})) {{ throw 'The staged editor snapshot is missing.' }}\n" +
                    innerInvocation + "\n" +
                    $"${statusVariable} = $?\n}} catch {{\n${statusVariable} = $false\nthrow\n}} finally {{\n" +
                    "try {\n" +
                    $"if (${statusVariable}) {{ [System.IO.File]::WriteAllText({quotedStatusPath}, 'OK') }} else {{ [System.IO.File]::WriteAllText({quotedStatusPath}, 'ERROR') }}\n" +
                    "} finally {\n" +
                    $"Remove-Variable -Name '{statusVariable}' -ErrorAction SilentlyContinue\n" +
                    $"Remove-Variable -Name '{stagedScriptVariable}' -Scope Global -ErrorAction SilentlyContinue\n" +
                    "}\n}\n}";
                if (sourceInfo is not null && RunSelectionEvidenceCapture.IsEnabled)
                {
                    RunSelectionEvidenceCapture.TryQueue(
                        requestId, sessionGeneration, documentDisplayName, scriptContent,
                        scriptContent, snapshotPath, sourceInfo, stagedExpression);
                }
            }
            if (useExperimentalDirectBridge)
            {
                protocolResult = new PsesStructuredEvaluationResult(EditorExecutionStatus.Completed,
                    ResponseReceived: true, ExecutionBusyObserved: false, ProtocolError: null, ErrorMessage: null);
                DeveloperDiagnostics.LogInfo("PSES", "Direct editor bridge selected at request admission.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId, ["sessionGeneration"] = sessionGeneration,
                    ["processId"] = ProcessId, ["bridge"] = useExperimentalDirectRunSelection ? "direct-lsp-run-selection" : "direct-lsp-run-file",
                    ["snapshotLength"] = scriptContent.Length,
                    ["documentId"] = sourceInfo?.DocumentId,
                    ["documentRevision"] = sourceInfo?.DocumentRevision,
                    ["selectionStart"] = sourceInfo?.SelectionStart,
                    ["selectionLength"] = sourceInfo?.SelectionLength,
                    ["executeInCurrentScope"] = executeInCurrentScope,
                    ["directRunSelectionApproved"] = directRunSelectionApproved,
                    ["contentOmitted"] = true
                });
            }
            else
            {
                var debugPipe = _debugServicePipeName ?? throw new InvalidOperationException("The PSES debug-service endpoint is unavailable for private editor execution.");
                protocolResult = await PsesDapEvaluationClient.EvaluateAsync(debugPipe, requestId, sessionGeneration, stagedExpression!, cancellationToken,
                () => stagedLauncherMayExist = true,
                evaluated =>
                {
                    stagingOperationSettled = true;
                    phase.AdvanceTo(StructuredExecutionPhase.DapStageSettled);
                    if (evaluated) phase.AdvanceTo(StructuredExecutionPhase.DapStaged);
                }).ConfigureAwait(false);
            }
            if (!useExperimentalDirectBridge && correlation.IsEnabled)
                correlation.Record(new ExecutionCorrelationEvent("DapStageResponse", RequestId: requestId,
                    TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                    Outcome: protocolResult.Status.ToString(), Reason: protocolResult.ResponseReceived ? null : "no-response"));
            if (!useExperimentalDirectBridge)
                DeveloperDiagnostics.LogInfo("PSES", "Private DAP script staging returned before foreground host execution.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["stageStatus"] = protocolResult.Status.ToString(),
                ["stageResponseReceived"] = protocolResult.ResponseReceived,
                    ["stagedExpressionLength"] = stagedExpression!.Length,
                ["contentOmitted"] = true
            });
            // A canceled stage may return its terminal response after the user
            // cancellation. Record that settled outcome, then route through cleanup
            // before foreground admission.
            cancellationToken.ThrowIfCancellationRequested();
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
                // DAP staging must be acknowledged before requesting foreground
                // evaluation. The private status file is produced by executing
                // the staged script and cannot be an admission prerequisite.
                if (status == EditorExecutionStatus.Completed && protocolResult.ResponseReceived)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var invocation = useExperimentalDirectBridge
                        ? BuildExperimentalDirectInvocation(quotedSnapshotPath, quotedStatusPath, statusVariable, executeInCurrentScope)
                        : (executeInCurrentScope ? ". " : "& ") + "$global:" + stagedScriptVariable;
                    DeveloperDiagnostics.LogInfo("PSES", "Foreground PSES editor invocation requested after DAP staging.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["executeInCurrentScope"] = executeInCurrentScope,
                        ["invocationLength"] = invocation.Length,
                        ["contentOmitted"] = true
                    });
                    foregroundRequested = true;
                    if (correlation.IsEnabled)
                        correlation.Record(new ExecutionCorrelationEvent("ForegroundCommandSubmitted", RequestId: requestId,
                            TerminalGeneration: sessionGeneration, ProcessId: ProcessId));
                    var invocationEchoForms = BuildInvocationEchoForms(invocation, executeInCurrentScope);
                    requestEchoFilter = new StructuredEchoFilter(requestId, preservePromptPrefix: true, invocationEchoForms);
                    var echoFilterInstalled = false;
                    lock (_syncRoot)
                    {
                        if (_shutdownRequested || _sessionFaulted || sessionGeneration != _generation)
                        {
                            requestEchoFilter = null;
                            status = EditorExecutionStatus.Unknown;
                            errorMessage = "The authoritative PSES session changed before foreground execution; the script outcome is unknown.";
                        }
                        else
                        {
                            _activeStructuredEchoFilter = requestEchoFilter;
                            echoFilterInstalled = true;
                        }
                    }
                    DeveloperDiagnostics.LogInfo("PSES", "Request-scoped generated invocation echo filter registration evaluated before foreground evaluation.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["processId"] = ProcessId,
                        ["executeInCurrentScope"] = executeInCurrentScope,
                        ["echoFormCount"] = invocationEchoForms.Length,
                        ["installed"] = echoFilterInstalled,
                        ["invocationLength"] = invocation.Length,
                        ["contentOmitted"] = true
                    });
                    var foregroundCompleted = false;
                    if (requestEchoFilter is not null)
                    {
                        foregroundCompleted = await RestoreInteractivePromptAsync(requestId, sessionGeneration, invocation, statusPath, cancellationToken,
                            () =>
                            {
                                foregroundHandoffStarted = true;
                                foregroundEvaluationSubmitted = true;
                                phase.AdvanceTo(StructuredExecutionPhase.ForegroundSubmitted);
                            }, () => phase.AdvanceTo(StructuredExecutionPhase.EvaluationResponded),
                            experimentalDirectRunFile: useExperimentalDirectBridge).ConfigureAwait(false);
                    }
                    foregroundCompletionVerified = foregroundCompleted;
                    if (foregroundCompleted) phase.AdvanceTo(StructuredExecutionPhase.PromptReconciled);
                    if (!foregroundCompleted)
                    {
                        DeveloperDiagnostics.LogWarning("PSES", "Foreground editor execution did not satisfy request-specific prompt restoration predicates.", new Dictionary<string, object?>
                        {
                            ["requestId"] = requestId,
                            ["sessionGeneration"] = sessionGeneration,
                            ["statusFileExists"] = File.Exists(statusPath)
                        });
                        status = EditorExecutionStatus.Unknown;
                        errorMessage = "PSES request-specific prompt restoration was not verified; inspect correlated prompt predicate diagnostics.";
                        // A missing request-specific prompt is not proof that the session
                        // can never recover. Keep readiness conservative, but only latch
                        // it when the request/session was actually invalidated; the prompt
                        // waiter remains active until its correlated event arrives.
                        lock (_syncRoot) _interactiveReadinessUnverified = true;
                        DeveloperDiagnostics.LogWarning("PSES", "Interactive readiness remains unverified after request-correlated prompt verification failed; preserving the live session and blocking input/editor admission.", new Dictionary<string, object?>
                        {
                            ["requestId"] = requestId,
                            ["sessionGeneration"] = sessionGeneration,
                            ["processId"] = ProcessId,
                            ["statusFileExists"] = File.Exists(statusPath),
                            ["shutdownDecision"] = "preserve-session",
                            ["sessionRunning"] = IsSessionRunning,
                            ["inputWritable"] = IsInputWritable,
                            ["readinessState"] = "unverified"
                        });
                    }
                }
                // Read the correlated outcome only after foreground execution
                // was requested and its status boundary appeared. This is
                // independent of prompt restoration: the marker establishes
                // script outcome, never interactive input readiness.
                if (foregroundRequested && File.Exists(statusPath))
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
                        scriptOutcomeVerified = true;
                        if (foregroundCompletionVerified)
                        {
                            status = EditorExecutionStatus.Failed;
                            errorMessage = "PowerShell reported a final error status during structured execution.";
                        }
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
                    if (correlation.IsEnabled)
                        correlation.Record(new ExecutionCorrelationEvent(scriptOutcomeVerified ? "StatusValidated" : "StatusValidationFailed",
                            RequestId: requestId, TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                            Outcome: scriptOutcomeVerified ? outcome : "UNKNOWN", StatusValidated: scriptOutcomeVerified,
                            Reason: scriptOutcomeVerified ? null : "invalid-or-incomplete-status"));
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
            return new AuthoritativeStructuredExecutionResult(status, CurrentWorkingDirectory, errorMessage,
                foregroundCompletionVerified, RecoveryRequired: useExperimentalDirectBridge && status == EditorExecutionStatus.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            phase.RequestCancellation();
            if (correlation.IsEnabled)
                correlation.Record(new ExecutionCorrelationEvent("CancellationRequested", RequestId: requestId,
                    TerminalGeneration: sessionGeneration, ProcessId: ProcessId));
            var promptRecovered = false;
            var requestStatusObserved = false;
            var sessionTerminated = false;
            var preSubmissionCleanupConfirmed = true;
            if (foregroundCompletionVerified)
            {
                // Cancellation can race with post-prompt outcome parsing. The
                // request-specific status and prompt predicates already proved
                // that this invocation settled, so no second Ctrl+C is needed.
                promptRecovered = true;
                requestStatusObserved = File.Exists(statusPath) && sessionGeneration == SessionGeneration;
                foregroundTerminationVerified = requestStatusObserved;
            }
            else if (phase.RequiresForegroundInterruption)
            {
                promptRecovered = (await StopConsoleForGenerationAsync(sessionGeneration).ConfigureAwait(false)).Outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt;
                requestStatusObserved = File.Exists(statusPath) && sessionGeneration == SessionGeneration;
                if (!promptRecovered || !requestStatusObserved)
                {
                    // Ctrl+C and a visible prompt alone do not prove this request's
                    // pipeline stopped. Preserve the existing controlled failure
                    // path when the request status and prompt cannot both be verified.
                    await ShutdownAsync().ConfigureAwait(false);
                    sessionTerminated = !IsSessionRunning;
                }
                foregroundTerminationVerified = (promptRecovered && requestStatusObserved) || sessionTerminated;
            }
            else if (stagedLauncherMayExist)
            {
                if (stagingOperationSettled)
                {
                    var cleanupOwned = phase.TryBeginStagedLauncherCleanup();
                    var debugPipe = _debugServicePipeName;
                    preSubmissionCleanupConfirmed = cleanupOwned && debugPipe is not null && await PsesDapEvaluationClient.RemoveStagedScriptAsync(
                        debugPipe, requestId, sessionGeneration, stagedScriptVariable).ConfigureAwait(false);
                    if (preSubmissionCleanupConfirmed)
                        phase.ConfirmStagedLauncherCleanup();
                    if (correlation.IsEnabled)
                        correlation.Record(new ExecutionCorrelationEvent("StagedLauncherCleanupSettled", RequestId: requestId,
                            TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                            Outcome: preSubmissionCleanupConfirmed ? "Acknowledged" : "Unknown",
                            Reason: preSubmissionCleanupConfirmed ? "request-correlated-launcher-absence" : "cleanup-acknowledgment-missing"));
                }
                else
                {
                    preSubmissionCleanupConfirmed = false;
                    DeveloperDiagnostics.LogWarning("PSES", "Staged launcher cleanup was withheld because the DAP stage has no terminal response; cleanup could race a late stage completion.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["cleanupDispatched"] = false,
                        ["stagingSettled"] = false
                    });
                }
                if (!preSubmissionCleanupConfirmed)
                {
                    lock (_syncRoot) _interactiveReadinessUnverified = true;
                }
            }
            if (correlation.IsEnabled)
                correlation.Record(new ExecutionCorrelationEvent("CancellationRecoverySettled", RequestId: requestId,
                    TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                    Outcome: (foregroundEvaluationSubmitted ? foregroundTerminationVerified : preSubmissionCleanupConfirmed) ? "CANCELLED" : "UNKNOWN",
                    Reason: foregroundEvaluationSubmitted ? foregroundTerminationVerified ? null : "recovery-unverified"
                        : preSubmissionCleanupConfirmed ? "pre-foreground-canceled" : stagingOperationSettled ? "staged-launcher-cleanup-unverified" : "dap-stage-settlement-unverified"));
            DeveloperDiagnostics.LogInfo("PSES", "Structured cancellation recovery settled before execution ownership was released.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["phase"] = phase.Current.ToString(),
                ["foregroundEvaluationSubmitted"] = foregroundEvaluationSubmitted,
                ["promptRecovered"] = promptRecovered,
                ["requestStatusObserved"] = requestStatusObserved,
                ["sessionTerminated"] = sessionTerminated,
                ["pipelineInterruptionProven"] = foregroundTerminationVerified,
                ["stagingOperationSettled"] = stagingOperationSettled,
                ["preSubmissionCleanupConfirmed"] = preSubmissionCleanupConfirmed
            });
            var cancellationVerified = foregroundEvaluationSubmitted ? foregroundTerminationVerified : preSubmissionCleanupConfirmed;
            return new AuthoritativeStructuredExecutionResult(cancellationVerified ? EditorExecutionStatus.Cancelled : EditorExecutionStatus.Unknown, CurrentWorkingDirectory,
                !foregroundEvaluationSubmitted ? preSubmissionCleanupConfirmed ? "Execution canceled before foreground submission; the staged launcher was removed." : "Execution cancellation was not verified because the staged launcher cleanup could not be confirmed."
                    : promptRecovered && requestStatusObserved ? "Execution canceled; request completion and interactive prompt were observed." : sessionTerminated ? "Execution canceled; the owned session was terminated because request completion or prompt recovery was unverified." : "Execution cancellation could not be verified.",
                false, RecoveryRequired: !foregroundEvaluationSubmitted && !preSubmissionCleanupConfirmed);
        }
        catch (Exception ex)
        {
            if (foregroundRequested && !foregroundCompletionVerified)
            {
                try
                {
                    await ShutdownAsync().ConfigureAwait(false);
                    foregroundTerminationVerified = !IsSessionRunning;
                }
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
                "Structured execution could not verify the script outcome: " + ex.Message, false,
                RecoveryRequired: (_experimentalRunFileBridge.IsEnabled && !executeInCurrentScope) ||
                    (_experimentalRunSelectionBridge.IsEnabled && mode == EditorExecutionMode.RunSelection && executeInCurrentScope));
        }
        finally
        {
            var settledPhase = phase.Current;
            phase.AdvanceTo(StructuredExecutionPhase.Terminal);
            if (correlation.IsEnabled)
                correlation.Record(new ExecutionCorrelationEvent("RequestTerminalCleanup", RequestId: requestId,
                    TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                    Outcome: phase.CancellationRequested ? "canceled" : protocolResult?.Status.ToString(),
                    Reason: $"phase={settledPhase};foregroundSubmitted={foregroundEvaluationSubmitted}"));
            var foregroundSettled = !foregroundHandoffStarted || foregroundCompletionVerified || foregroundTerminationVerified;
            if (foregroundSettled)
                TryDeleteFile(statusPath);
            else
                DeveloperDiagnostics.LogWarning("PSES", "Private status file retained because foreground termination was not verified.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["statusPath"] = DeveloperDiagnostics.SanitizePath(statusPath)
                });
            if (snapshotCreated && snapshotPath is not null)
            {
                if (foregroundSettled)
                {
                    try
                    {
                        File.Delete(snapshotPath);
                        DeveloperDiagnostics.LogInfo("PSES", "Request-owned editor snapshot cleanup completed.", new Dictionary<string, object?>
                        {
                            ["requestId"] = requestId,
                            ["sessionGeneration"] = sessionGeneration,
                            ["snapshotPath"] = DeveloperDiagnostics.SanitizePath(snapshotPath),
                            ["snapshotExistsAfterCleanup"] = File.Exists(snapshotPath)
                        });
                    }
                    catch (Exception cleanupException)
                    {
                        DeveloperDiagnostics.LogException("PSES", cleanupException, "Request-owned editor snapshot cleanup failed.", new Dictionary<string, object?>
                        {
                            ["requestId"] = requestId,
                            ["sessionGeneration"] = sessionGeneration,
                            ["snapshotPath"] = DeveloperDiagnostics.SanitizePath(snapshotPath)
                        });
                    }
                }
                else
                {
                    DeveloperDiagnostics.LogWarning("PSES", "Request-owned editor snapshot retained because foreground termination was not verified.", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["sessionGeneration"] = sessionGeneration,
                        ["snapshotPath"] = DeveloperDiagnostics.SanitizePath(snapshotPath)
                    });
                }
            }
            string filteredEchoRemainder;
            lock (_syncRoot)
            {
                if (_activeStructuredRequestId == requestId)
                    _activeStructuredRequestId = null;
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
            if (requestEchoFilter is not null)
            {
                if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                    ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("EchoFilterSettled",
                        RequestId: requestId, TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                        Outcome: cancellationToken.IsCancellationRequested ? "Canceled" : protocolResult?.Status.ToString(),
                        Reason: $"candidate={requestEchoFilter.CandidateEchoObserved};matches={requestEchoFilter.MatchedEchoCount};remainderLength={filteredEchoRemainder.Length}",
                        EventSource: "TerminalEchoFilter", EventType: "EchoFilterSettlement",
                        CorrelationConfidence: "RequestIdentity"));
                DeveloperDiagnostics.LogInfo("PSES", "Request-generated invocation echo coordination settled.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = sessionGeneration,
                    ["terminalGeneration"] = sessionGeneration,
                    ["candidateEchoObserved"] = requestEchoFilter.CandidateEchoObserved,
                    ["matchedEchoCount"] = requestEchoFilter.MatchedEchoCount,
                    ["candidateWithoutMatch"] = requestEchoFilter.CandidateEchoObserved && requestEchoFilter.MatchedEchoCount == 0,
                    ["filteredRemainderLength"] = filteredEchoRemainder.Length,
                    ["foregroundCompletionVerified"] = foregroundCompletionVerified,
                    ["promptRestorationVerified"] = foregroundCompletionVerified,
                    ["foregroundTerminationVerified"] = foregroundTerminationVerified,
                    ["cancellationRequested"] = cancellationToken.IsCancellationRequested,
                    ["contentOmitted"] = true
                });
            }
            savedScriptReadLock?.Dispose();
            if (executionGateAcquired) _structuredExecutionGate.Release();
            if (correlation.IsEnabled)
                correlation.Record(new ExecutionCorrelationEvent("ServiceGateReleased", RequestId: requestId,
                    TerminalGeneration: sessionGeneration, ProcessId: ProcessId,
                    Reason: foregroundSettled ? null : "foreground-unverified"));
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
        if (!IsInputWritable) return false;
        int generation;
        lock (_syncRoot) generation = _generation;
        return (await StopConsoleForGenerationAsync(generation).ConfigureAwait(false)).Outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt;
    }

    private async Task<InterruptRecoveryResult> StopConsoleForGenerationAsync(int expectedGeneration)
    {
        ConPtyProcess? expectedConPty;
        TaskCompletionSource<long> promptRecoveryWaiter;
        long promptObservationBeforeInterrupt;
        lock (_syncRoot)
        {
            expectedConPty = _conPty;
            if (expectedGeneration != _generation)
                return new InterruptRecoveryResult(LiveConsoleInterruptOutcome.SessionReplaced, false);
            if (expectedConPty is null || expectedConPty.HasExited)
                return new InterruptRecoveryResult(LiveConsoleInterruptOutcome.SessionTerminated, false);
            if (_shutdownRequested || _sessionFaulted)
                return new InterruptRecoveryResult(LiveConsoleInterruptOutcome.Failed, false);

            promptObservationBeforeInterrupt = _promptReadyObservation;
            _promptAnsiCarry = string.Empty;
            if (_pendingInteractiveCommand is { } pending && pending.Generation == expectedGeneration)
            {
                pending.InterruptRequested = true;
            }
            _promptRecognitionBuffer = string.Empty;
            promptRecoveryWaiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _promptRecoveryGeneration = expectedGeneration;
            _promptRecoveryWaiter = promptRecoveryWaiter;
        }
        var signalDelivered = false;
        try
        {
            await WriteInputCoreAsync("\u0003", CancellationToken.None, expectedConPty, expectedGeneration).ConfigureAwait(false);
            signalDelivered = true;
            try
            {
                var observed = await promptRecoveryWaiter.Task.WaitAsync(PromptRecoveryTimeout).ConfigureAwait(false);
                DeveloperDiagnostics.LogInfo("PSES", "PSES console-repl interrupt reached a post-interrupt prompt boundary.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["promptObservationBeforeInterrupt"] = promptObservationBeforeInterrupt,
                    ["promptObservationAfterInterrupt"] = observed,
                    ["promptRecoveryObserved"] = true
                });
                return new InterruptRecoveryResult(LiveConsoleInterruptOutcome.RecoveredAtPrompt, signalDelivered);
            }
            catch (TimeoutException)
            {
                var timeoutOutcome = LiveConsoleInterruptOutcome.TimedOutUnverified;
                lock (_syncRoot)
                {
                    if (!ReferenceEquals(_conPty, expectedConPty) || _generation != expectedGeneration)
                    {
                        timeoutOutcome = LiveConsoleInterruptOutcome.SessionReplaced;
                    }
                    else if (expectedConPty.HasExited)
                    {
                        timeoutOutcome = LiveConsoleInterruptOutcome.SessionTerminated;
                    }
                    else
                    {
                        _interactiveReadinessUnverified = true;
                    }
                }
                DeveloperDiagnostics.LogWarning("PSES", "PSES console-repl interrupt was sent but no post-interrupt prompt boundary was observed within the bounded recovery interval.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["promptObservationBeforeInterrupt"] = promptObservationBeforeInterrupt,
                    ["promptRecoveryObserved"] = false,
                    ["outcome"] = timeoutOutcome.ToString(),
                    ["recoveryTimeoutMilliseconds"] = (int)PromptRecoveryTimeout.TotalMilliseconds
                });
                return new InterruptRecoveryResult(timeoutOutcome, signalDelivered);
            }
        }
        catch (Exception ex)
        {
            if (!signalDelivered)
            {
                lock (_syncRoot)
                {
                    if (_pendingInteractiveCommand is { } pending && pending.Generation == expectedGeneration)
                        pending.InterruptRequested = false;
                }
            }
            DeveloperDiagnostics.LogException("PSES", ex, "PSES console-repl interrupt failed.");
            return new InterruptRecoveryResult(LiveConsoleInterruptOutcome.Failed, signalDelivered);
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

    private sealed record InterruptRecoveryResult(LiveConsoleInterruptOutcome Outcome, bool SignalDelivered);

    private async Task<bool> RestoreInteractivePromptAsync(Guid requestId, int expectedGeneration, string invocation, string statusPath, CancellationToken cancellationToken, Action onForegroundEvaluationSubmitted, Action onEvaluationResponse, bool experimentalDirectRunFile = false)
    {
        PsesLanguageServiceClient? client;
        int? expectedProcessId = null;
        var verificationStarted = System.Diagnostics.Stopwatch.StartNew();
        var invocationObserved = false;
        var invocationTail = string.Empty;
        // For direct Run File, the request-unique status file is written only by
        // the generated invocation after the snapshot returns. Requiring PSES to
        // echo its entire multiline request is unreliable (PSReadLine can format
        // or split that echo) and adds no completion proof beyond the status file,
        // the post-status native prompt, and the runtime identity checks below.
        var promptSignal = new PromptRestorationSignal(expectedGeneration, requireInvocationEcho: !experimentalDirectRunFile);
        ExecutionShadowReadiness? shadow = null;
        var statusMarkerRecorded = 0;
        var pendingStatusLogged = 0;
        var foregroundResponseReceived = 0;
        var eventOrder = 0L;
        var frameSequenceAtRegistration = 0L;
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
            if (invocationObserved)
            {
                if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                    ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("InvocationEchoObserved",
                        RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: ProcessId,
                        EchoObserved: true));
                DeveloperDiagnostics.LogInfo("PSES", "Request-generated invocation observed before display filtering.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = expectedGeneration,
                    ["processId"] = ProcessId,
                    ["eventOrder"] = Interlocked.Increment(ref eventOrder),
                    ["invocationLength"] = invocation.Length,
                    ["observedChunkLength"] = text.Length,
                    ["contentOmitted"] = true
                });
            }
        };
        Action<int, string> promptHandler = (generation, promptText) =>
        {
            var eventNumber = Interlocked.Increment(ref eventOrder);
            DeveloperDiagnostics.LogInfo("PSES", "Request prompt-handler callback entered.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["eventGeneration"] = generation,
                ["processId"] = ProcessId,
                ["eventOrder"] = eventNumber,
                ["promptPatternObserved"] = true,
                ["promptChunkLength"] = promptText.Length,
                ["contentOmitted"] = true
            });
            // The foreground invocation itself is printed after a prompt. A
            // chunk ending there can look like a ready prompt, so only accept
            // one after the script's private completion file has been written.
            bool cursorVisible;
            bool statusFileExists = File.Exists(statusPath);
            if (statusFileExists && ExecutionCorrelationTelemetry.Shared.IsEnabled &&
                Interlocked.Exchange(ref statusMarkerRecorded, 1) == 0)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("StatusMarkerObserved",
                    RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: ProcessId));
            bool invocationEchoRemoved;
            bool newPromptCandidate;
            PromptRestorationSnapshot before;
            PromptRestorationSnapshot snapshot;
            lock (_syncRoot)
            {
                cursorVisible = _cursorVisible;
                invocationEchoRemoved = invocationObserved;
                before = promptSignal.Snapshot();
                newPromptCandidate = promptSignal.ObservePrompt(generation, statusFileExists, invocationEchoRemoved, cursorVisible);
                snapshot = promptSignal.Snapshot();
            }
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("PromptCandidate",
                    RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: ProcessId,
                    Reason: generation != expectedGeneration ? "stale-generation" : !invocationEchoRemoved ? "echo-missing"
                        : before.Completed ? "duplicate-after-acceptance" : !snapshot.StatusFileObserved ? "status-not-visible"
                        : !snapshot.PostScriptPromptObserved ? "post-status-prompt-missing" : !snapshot.Completed ? "cursor-hidden" : "accepted",
                    EchoObserved: invocationEchoRemoved, PostStatusPrompt: snapshot.PostScriptPromptObserved,
                    CursorReady: cursorVisible, ActualReady: snapshot.Completed));
            shadow?.ObservePrompt(generation, cursorVisible, snapshot.Completed, invocationEchoRemoved, ShadowIdentityValid());
            DeveloperDiagnostics.LogInfo("PSES", "Request-specific prompt pattern recognized and predicate evaluated.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["eventGeneration"] = generation,
                ["processId"] = ProcessId,
                ["eventOrder"] = eventNumber,
                ["statusFileExists"] = statusFileExists,
                ["invocationObservedBeforeFiltering"] = invocationEchoRemoved,
                ["cursorVisible"] = cursorVisible,
                ["promptPatternObserved"] = true,
                ["postScriptPromptObserved"] = snapshot.PostScriptPromptObserved,
                ["accepted"] = snapshot.Completed,
                ["rejectionReason"] = generation != expectedGeneration ? "stale-generation" : !invocationEchoRemoved ? "request-invocation-not-observed" : !statusFileExists ? "status-file-not-yet-visible" : !cursorVisible ? "cursor-hidden" : null
            });
            DeveloperDiagnostics.LogInfo("PSES", "Request prompt-handler callback completed.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["eventGeneration"] = generation,
                ["processId"] = ProcessId,
                ["eventOrder"] = eventNumber,
                ["handlerCompletionObserved"] = true,
                ["statusFileExists"] = statusFileExists,
                ["invocationObservedBeforeFiltering"] = invocationEchoRemoved,
                ["cursorVisible"] = cursorVisible,
                ["signalCompleted"] = snapshot.Completed
            });
            if (newPromptCandidate && generation == expectedGeneration && invocationEchoRemoved && !statusFileExists)
            {
                DeveloperDiagnostics.LogInfo("PSES", "Prompt-like boundary before the request status file was ignored for visual restoration.", new Dictionary<string, object?>
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
            var eventNumber = Interlocked.Increment(ref eventOrder);
            if (DeveloperDiagnostics.IsEnabled && Volatile.Read(ref foregroundResponseReceived) == 0 &&
                generation == expectedGeneration && visible &&
                Volatile.Read(ref pendingStatusLogged) == 0 && File.Exists(statusPath) &&
                Interlocked.Exchange(ref pendingStatusLogged, 1) == 0)
                DeveloperDiagnostics.LogInfo("PSES", "Request status marker observed while foreground evaluate response was pending.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = expectedGeneration,
                    ["processId"] = ProcessId,
                    ["eventOrder"] = eventNumber,
                    ["invocationObservedBeforeFiltering"] = invocationObserved,
                    ["contentOmitted"] = true
                });
            PromptRestorationSnapshot before;
            PromptRestorationSnapshot after;
            lock (_syncRoot)
            {
                before = promptSignal.Snapshot();
                promptSignal.ObserveCursor(generation, visible);
                after = promptSignal.Snapshot();
            }
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("CursorPredicateEvaluated",
                    RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: ProcessId,
                    Reason: generation != expectedGeneration ? "stale-generation" : !visible ? "cursor-hidden"
                        : before.Completed ? "duplicate-after-acceptance" : !after.PostScriptPromptObserved ? "post-status-prompt-missing" : "accepted",
                    PostStatusPrompt: after.PostScriptPromptObserved, CursorReady: visible, ActualReady: after.Completed));
            shadow?.ObserveCursor(generation, visible, after.Completed,
                invocationObserved, ShadowIdentityValid());
            DeveloperDiagnostics.LogInfo("PSES", "Prompt verification cursor visibility transition observed.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["eventGeneration"] = generation,
                ["processId"] = ProcessId,
                ["eventOrder"] = eventNumber,
                ["cursorVisible"] = visible,
                ["signalCompleted"] = promptSignal.Snapshot().Completed
            });
        };
        long observationBeforeHandoff;
        lock (_syncRoot)
        {
            client = _languageServiceClient;
            expectedProcessId = _conPty is { HasExited: false } owner ? owner.ProcessId : null;
            if (_shutdownRequested || _sessionFaulted || expectedGeneration != _generation || client is null || !client.IsInitialized)
                return false;

            observationBeforeHandoff = _promptReadyObservation;
            _promptRecognitionBuffer = string.Empty;
            _promptAnsiCarry = string.Empty;
        }

        bool ShadowIdentityValid()
        {
            lock (_syncRoot)
                return !_shutdownRequested && !_sessionFaulted && expectedGeneration == _generation &&
                    expectedProcessId is > 0 && _conPty is { HasExited: false } owner &&
                    owner.ProcessId == expectedProcessId.Value && _activeStructuredRequestId == requestId;
        }

        if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
        {
            shadow = new ExecutionShadowReadiness(ExecutionCorrelationTelemetry.Shared, requestId,
                expectedGeneration, expectedProcessId, ProcessStartTimeUtc?.UtcTicks);
        }

        PromptReadyObserved += promptHandler;
        OutputObservedBeforeDisplayFiltering += rawHandler;
        CursorVisibilityChanged += cursorHandler;
        frameSequenceAtRegistration = _promptFrameDiagnostics.Snapshot().TotalRecorded;
        bool cursorVisibleAtEntry;
        lock (_syncRoot) cursorVisibleAtEntry = _cursorVisible;
        DeveloperDiagnostics.LogInfo("PSES", "Request-specific prompt verification entered.", new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionGeneration"] = expectedGeneration,
            ["processId"] = ProcessId,
            ["statusFileExistsAtEntry"] = File.Exists(statusPath),
            ["cursorVisibleAtEntry"] = cursorVisibleAtEntry,
            ["waiterRegisteredUtc"] = DateTimeOffset.UtcNow,
            ["frameSequenceAtRegistration"] = frameSequenceAtRegistration
        });
        try
        {
            // PSES owns the foreground transition and writes the invocation before
            // user output. Its REPL then writes the next prompt at the new cursor.
            var handoffTask = client.EvaluateAsync(requestId, invocation, cancellationToken, onForegroundEvaluationSubmitted);
            PsesStructuredEvaluationResult handoff;
            var terminalWon = false;
            if (experimentalDirectRunFile)
            {
                var winner = await Task.WhenAny(handoffTask, promptSignal.Completion).ConfigureAwait(false);
                terminalWon = winner == promptSignal.Completion;
                if (terminalWon)
                {
                    // The client retains the JSON-RPC pending entry and evaluation
                    // gate until the correlated response or transport failure. The
                    // continuation only observes that late outcome; it cannot
                    // change this request's already verified terminal result.
                    _ = ObserveLateExperimentalEvaluationAsync(handoffTask, requestId, expectedGeneration);
                    handoff = new PsesStructuredEvaluationResult(EditorExecutionStatus.Completed,
                        ResponseReceived: false, ExecutionBusyObserved: true, ProtocolError: null,
                        ErrorMessage: null);
                }
                else
                {
                    handoff = await handoffTask.ConfigureAwait(false);
                    Volatile.Write(ref foregroundResponseReceived, 1);
                    onEvaluationResponse();
                }
            }
            else
            {
                handoff = await handoffTask.ConfigureAwait(false);
                Volatile.Write(ref foregroundResponseReceived, 1);
                onEvaluationResponse();
            }
            var processStartTicks = ProcessStartTimeUtc?.UtcTicks;
            var runtimeGeneration = client.RuntimeGeneration;
            bool requestOwnsForeground;
            bool identityStillCurrent;
            lock (_syncRoot)
            {
                requestOwnsForeground = _activeStructuredRequestId == requestId;
                identityStillCurrent = !_shutdownRequested && !_sessionFaulted && expectedGeneration == _generation &&
                    expectedProcessId is > 0 && _conPty is { HasExited: false } currentProcess &&
                    currentProcess.ProcessId == expectedProcessId.Value && processStartTicks is > 0 &&
                    processStartTicks == client.ProcessStartUtcTicks &&
                    client.RuntimeIdentityVerified && _runtimeSessionIdentity == client.RuntimeSessionIdentity &&
                    _runtimeGeneration == client.RuntimeGeneration;
            }
            var candidate = HostForegroundTaskEndedCandidate.Classify(new HostForegroundTaskEndedCandidateFacts(
                requestId, handoff.ProtocolRequestId, handoff.ResponseSequence, handoff.IdleSequence,
                client.RuntimeIdentityVerified ? client.RuntimeSessionIdentity : null, runtimeGeneration, expectedGeneration, expectedProcessId,
                processStartTicks, handoff.ProtocolError is null, requestOwnsForeground, identityStillCurrent,
                DebuggerOwnsSession: GetDebuggerOwnershipSnapshot().MayOwnSession,
                Canceled: cancellationToken.IsCancellationRequested, Faulted: !client.IsInitialized,
                Superseded: expectedGeneration != SessionGeneration,
                PsesVersionVerified: client.PsesLifecycleContractVerified,
                RuntimeGenerationMatches: runtimeGeneration is > 0 && _runtimeGeneration == runtimeGeneration,
                IdleObservationCount: handoff.IdleObservationCount ?? 0));
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
            {
                var debuggerOwnership = GetDebuggerOwnershipSnapshot();
                var responseConfidence = !handoff.ResponseReceived ? "Incomplete" :
                    !identityStillCurrent ? "Stale" : debuggerOwnership.MayOwnSession ? "Rejected" :
                    handoff.ProtocolRequestId.HasValue && handoff.ResponseSequence.HasValue ? "Correlated" : "Partial";
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("LspEvaluationResponse",
                    RequestId: requestId, ProtocolRequestId: handoff.ProtocolRequestId,
                    RuntimeSessionId: client.RuntimeIdentityVerified ? client.RuntimeSessionIdentity : null,
                    RuntimeGeneration: runtimeGeneration, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                    ProcessStartUtcTicks: processStartTicks, PsesVersion: client.PsesLifecycleVersion,
                    ResponseSequence: handoff.ResponseSequence, IdleSequence: handoff.IdleSequence,
                    Outcome: handoff.Status.ToString(), Reason: handoff.ResponseReceived ? null : "no-response",
                    EventSource: "PSES/LSP", EventType: "EvaluateResponse",
                    EventOutcome: handoff.ResponseReceived ? handoff.Status.ToString() : "Incomplete",
                    CorrelationConfidence: responseConfidence,
                    RejectionReason: responseConfidence is "Stale" or "Rejected" ? responseConfidence : null,
                    DebuggerSessionId: debuggerOwnership.DebuggerSessionId,
                    DebuggerState: debuggerOwnership.State.ToString(), DebuggerMayOwnSession: debuggerOwnership.MayOwnSession));
            }
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("HostForegroundTaskEndedCandidate",
                    RequestId: requestId, ProtocolRequestId: handoff.ProtocolRequestId,
                    RuntimeSessionId: client.RuntimeIdentityVerified ? client.RuntimeSessionIdentity : null,
                    RuntimeGeneration: runtimeGeneration, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                    ProcessStartUtcTicks: processStartTicks, PsesVersion: client.PsesLifecycleVersion,
                    ResponseSequence: handoff.ResponseSequence, IdleSequence: handoff.IdleSequence,
                    Outcome: candidate.Classification, Reason: candidate.Reason,
                    IdentityValid: identityStillCurrent, ActualReady: false));
            if (!terminalWon && !File.Exists(statusPath))
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
            var statusObserved = File.Exists(statusPath);
            DeveloperDiagnostics.LogInfo("PSES", "Prompt-verification status-file state sampled after foreground handoff.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["processId"] = ProcessId,
                ["statusFileExists"] = statusObserved,
                ["eventOrder"] = Interlocked.Increment(ref eventOrder)
            });
            while (!statusObserved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSessionRunning || expectedGeneration != SessionGeneration) return false;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                statusObserved = File.Exists(statusPath);
            }
            DeveloperDiagnostics.LogInfo("PSES", "Prompt-verification status file became visible.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["processId"] = ProcessId,
                ["statusFileExists"] = true,
                ["eventOrder"] = Interlocked.Increment(ref eventOrder)
            });
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
            {
                if (Interlocked.Exchange(ref statusMarkerRecorded, 1) == 0)
                    ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("StatusMarkerObserved",
                        RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId));
                // The diagnostic read runs independently; it cannot delay the production waiter.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var shadowOutcome = await ReadShadowStatusAsync(statusPath).ConfigureAwait(false);
                        var validated = shadowOutcome is "OK" or "ERROR";
                        ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent(
                            validated ? "ShadowStatusValidated" : "ShadowStatusValidationFailed",
                            RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                            Outcome: validated ? shadowOutcome : "UNKNOWN", StatusValidated: validated,
                            Reason: validated ? null : "invalid-or-incomplete-status"));
                        shadow?.ObserveStatus(validated, promptSignal.Snapshot().Completed,
                            invocationObserved, ShadowIdentityValid());
                    }
                    catch { /* Diagnostic work has no execution authority. */ }
                });
            }
            if (promptSignal.ObserveStatusFileVisible())
            {
                bool cursorVisibleAtStatus;
                lock (_syncRoot) cursorVisibleAtStatus = _cursorVisible;
                DeveloperDiagnostics.LogInfo("PSES", "Foreground status file appeared after an earlier prompt candidate; a post-status prompt is still required.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = expectedGeneration,
                    ["requestId"] = requestId,
                    ["processId"] = ProcessId,
                    ["cursorVisibleAtStatus"] = cursorVisibleAtStatus,
                    ["contentOmitted"] = true
                });
            }
            try
            {
                await promptSignal.Completion.WaitAsync(PromptRecoveryTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                {
                    var pending = promptSignal.Snapshot();
                    ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("PromptObservationThreshold",
                        RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                        Reason: !invocationObserved ? "echo-missing" : !pending.StatusFileObserved ? "status-not-visible"
                            : pending.PromptBoundaryObserved && !pending.PostScriptPromptObserved ? "prompt-before-status-only"
                            : !pending.PostScriptPromptObserved ? "no-post-status-prompt-candidate" : !pending.CursorVisible ? "cursor-hidden" : "signal-incomplete",
                        EchoObserved: invocationObserved, PostStatusPrompt: pending.PostScriptPromptObserved,
                        CursorReady: pending.CursorVisible, ActualReady: pending.Completed,
                        OutputFramesSinceRegistration: _promptFrameDiagnostics.Snapshot().TotalRecorded - frameSequenceAtRegistration));
                }
                // This is an observation threshold for diagnostics, not an execution
                // deadline. Keep the request handlers attached and await the matching
                // post-status prompt so a slow but healthy PSES session can recover.
                DeveloperDiagnostics.LogWarning("PSES", "Structured execution exceeded the prompt observation threshold; retaining request-correlated late recovery.", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["sessionGeneration"] = expectedGeneration,
                    ["processId"] = ProcessId,
                    ["observationThresholdMilliseconds"] = (int)PromptRecoveryTimeout.TotalMilliseconds,
                    ["statusFileExists"] = File.Exists(statusPath),
                    ["signal"] = promptSignal.Snapshot(),
                    ["recoveryWaitContinues"] = true,
                    ["contentOmitted"] = true
                });
                await promptSignal.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_syncRoot)
            {
                if (_shutdownRequested || _sessionFaulted || expectedGeneration != _generation ||
                    expectedProcessId is null || _conPty is not { HasExited: false } owner || owner.ProcessId != expectedProcessId.Value)
                {
                    if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                        ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("ReadinessIdentityRejected",
                            RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                            Reason: _shutdownRequested ? "shutdown-requested" : _sessionFaulted ? "session-faulted"
                                : expectedGeneration != _generation ? "stale-generation" : expectedProcessId is null ? "process-missing"
                                : _conPty is not { HasExited: false } ? "process-exited" : "process-identity-mismatch", IdentityValid: false));
                    return false;
                }
                if (_activeStructuredRequestId == requestId && File.Exists(statusPath))
                    _interactiveReadinessUnverified = false;
            }
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("ServicePromptObservationEmitted",
                    RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                    IdentityValid: true, ActualReady: true));
            NotifyStructuredPromptReadyObserved(new StructuredPromptReadyObservation(
                requestId,
                expectedGeneration,
                ProcessId ?? 0,
                CurrentWorkingDirectory ?? string.Empty));
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("ServiceReadinessAccepted",
                    RequestId: requestId, TerminalGeneration: expectedGeneration, ProcessId: expectedProcessId,
                    IdentityValid: true, ActualReady: true));
            DeveloperDiagnostics.LogInfo("PSES", "Request-specific prompt restoration predicate accepted.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["processId"] = ProcessId,
                ["eventOrder"] = Interlocked.Increment(ref eventOrder),
                ["elapsedMilliseconds"] = verificationStarted.Elapsed.TotalMilliseconds,
                ["signal"] = promptSignal.Snapshot()
            });
            long observationAfterHandoff;
            lock (_syncRoot) observationAfterHandoff = _promptReadyObservation;
            DeveloperDiagnostics.LogInfo("PSES", "Request status and prompt predicates observed; interactive keyboard consumption remains unverified.", new Dictionary<string, object?>
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
                ["inputPipeWritable"] = IsInputWritable,
                ["inputConsumptionVerified"] = false,
                ["foregroundInvocationLength"] = invocation.Length,
                ["contentOmitted"] = true
            });
            return true;
        }
        catch (TimeoutException)
        {
            var frameSnapshot = _promptFrameDiagnostics.Snapshot();
            DeveloperDiagnostics.LogWarning("PSES", "Request-specific prompt verification ended without a verified ready prompt.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = expectedGeneration,
                ["processId"] = ProcessId,
                ["promptObservationBeforeHandoff"] = observationBeforeHandoff,
                ["elapsedMilliseconds"] = verificationStarted.Elapsed.TotalMilliseconds,
                ["statusFileExistsAtTimeout"] = File.Exists(statusPath),
                ["invocationObservedBeforeFiltering"] = invocationObserved,
                ["signal"] = promptSignal.Snapshot(),
                ["sessionShutdownRequestedAfterTimeout"] = false,
                ["sessionPreservedAfterTimeout"] = IsSessionRunning,
                ["inputAdmissionBlockedAfterTimeout"] = true,
                ["contentOmitted"] = true,
                ["diagnosticHistoryEvictedFrameCount"] = frameSnapshot.Evicted,
                ["diagnosticHistoryCapacity"] = frameSnapshot.Capacity,
                ["diagnosticHistoryRetainedFrameCount"] = frameSnapshot.Frames.Length,
                ["diagnosticHistoryTotalRecordedFrameCount"] = frameSnapshot.TotalRecorded,
                ["recentConPtyFrames"] = frameSnapshot.Frames.TakeLast(64)
                    .Select(ToFrameDiagnosticProperties)
                    .ToArray()
            });
            return false;
        }
        finally
        {
            PromptReadyObserved -= promptHandler;
            OutputObservedBeforeDisplayFiltering -= rawHandler;
            CursorVisibilityChanged -= cursorHandler;
        }
    }

    private static async Task ObserveLateExperimentalEvaluationAsync(Task<PsesStructuredEvaluationResult> evaluation, Guid requestId, int generation)
    {
        try
        {
            var result = await evaluation.ConfigureAwait(false);
            DeveloperDiagnostics.LogInfo("PSES", "Late experimental evaluate response observed after request-correlated terminal completion.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = generation,
                ["responseReceived"] = result.ResponseReceived,
                ["status"] = result.Status.ToString(),
                ["contentOmitted"] = true
            });
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogWarning("PSES", "Late experimental evaluate response failed after request-correlated terminal completion; pending ownership settled by the language-service client.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = generation,
                ["exceptionType"] = ex.GetType().Name,
                ["contentOmitted"] = true
            });
        }
    }

    private static async Task<string?> ReadShadowStatusAsync(string statusPath)
    {
        try
        {
            await using var stream = new FileStream(statusPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var buffer = new byte[17];
            var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (count > 16) return null;
            var value = Encoding.UTF8.GetString(buffer, 0, count).Trim();
            return value is "OK" or "ERROR" ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, object?> ToFrameDiagnosticProperties(PromptFrameDiagnostic frame)
        => new Dictionary<string, object?>
        {
            ["sequence"] = frame.Sequence,
            ["utc"] = frame.Utc,
            ["monotonicTimestamp"] = frame.MonotonicTimestamp,
            ["generation"] = frame.Generation,
            ["requestId"] = frame.RequestId,
            ["byteCount"] = frame.ByteCount,
            ["csiStarted"] = frame.CsiStarted,
            ["csiEnded"] = frame.CsiEnded,
            ["csiSpannedFrames"] = frame.CsiSpannedFrames,
            ["pendingControlSequence"] = frame.PendingControlSequence,
            ["sequenceTypes"] = frame.SequenceTypes,
            ["controlHex"] = frame.ControlHex,
            ["promptCandidate"] = frame.PromptCandidate,
            ["promptAccepted"] = frame.PromptAccepted,
            ["rejectionReason"] = frame.RejectionReason,
            ["cursorVisible"] = frame.CursorVisible,
            ["authoritative"] = frame.Authoritative
        };

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
                _pendingInputOutputProbe = null;
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
        if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
        {
            Guid runtimeSessionIdentity; long? runtimeGeneration;
            lock (_syncRoot) { runtimeSessionIdentity = _runtimeSessionIdentity; runtimeGeneration = _runtimeGeneration; }
            ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("TerminalGenerationStopping",
                RuntimeSessionId: runtimeSessionIdentity == Guid.Empty ? null : runtimeSessionIdentity,
                RuntimeGeneration: runtimeGeneration, TerminalGeneration: generation, ProcessId: processId,
                EventSource: "ConPTY/PSES", EventType: "TerminalGenerationStopping",
                CorrelationConfidence: "GenerationBound"));
        }
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
                    _pendingInputOutputProbe = null;
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
        if (!attempted)
        {
            return new LiveConsoleInterruptResult(
                false, false, false, false, false, processId, PromptRecoveryTimeout,
                LiveConsoleInterruptOutcome.NotAttempted, signalDelivered: false);
        }
        CancellationTokenSource? activeCommandCancellation;
        lock (_syncRoot) activeCommandCancellation = _activeCommandCancellation;
        try { activeCommandCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        var recovery = await StopConsoleForGenerationAsync(CurrentSessionGeneration).ConfigureAwait(false);
        var outcome = recovery.Outcome;
        return new LiveConsoleInterruptResult(
            attempted,
            outcome == LiveConsoleInterruptOutcome.RecoveredAtPrompt,
            escalationRequired: false,
            processTerminationSucceeded: false,
            sessionRestarted: false,
            processId,
            PromptRecoveryTimeout,
            outcome,
            recovery.SignalDelivered);
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

    private async Task<LiveConsoleCommandResult> ExecuteTextAsync(string text, Action<ExecutionOutputRecord> onOutput, Guid requestId, CancellationToken cancellationToken, string? completionMarkerPath = null)
    {
        var started = DateTime.UtcNow;
        var generation = SessionGeneration;
        var processId = ProcessId ?? throw new InvalidOperationException("PSES host is not running.");
        var command = text.TrimEnd('\r', '\n') + "\r";
        PendingInteractiveCommand pending;
        lock (_syncRoot)
        {
            if (_shutdownRequested || _sessionFaulted || generation != _generation || _conPty is not { HasExited: false } owner || owner.ProcessId != processId)
                throw new InvalidOperationException("The authoritative PSES session changed before command admission.");
            if (_interactiveReadinessUnverified)
                throw new InvalidOperationException("Interactive readiness is unverified. Use Reset Console before submitting another command.");
            if (_pendingInteractiveCommand is not null || _commandInProgress)
                throw new InvalidOperationException("Another PSES interactive or editor operation already owns terminal admission.");
            pending = new PendingInteractiveCommand(requestId, generation, processId, _promptReadyObservation, started, completionMarkerPath);
            _pendingInteractiveCommand = pending;
            _commandInProgress = true;
        }
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "PSES-owned ExecuteTextAsync prepared terminal command.",
            new Dictionary<string, object?>
            {
                ["textLength"] = text?.Length ?? 0,
                ["commandLength"] = command.Length,
                ["carriageReturnAppended"] = command.EndsWith('\r'),
                ["sessionGeneration"] = _generation,
                ["requestId"] = requestId,
                ["processId"] = ProcessId,
                ["inputWritable"] = IsInputWritable,
                ["contentOmitted"] = true
            });
        try
        {
            await WriteInputCoreAsync(command, cancellationToken, expectedGeneration: generation).ConfigureAwait(false);
            var settled = false;
            lock (_syncRoot)
            {
                if (ReferenceEquals(_pendingInteractiveCommand, pending) &&
                    _conPty is { HasExited: false } currentOwner && currentOwner.ProcessId == processId && _generation == generation)
                {
                    pending.Submitted = true;
                    settled = pending.ObservedPromptSequence > pending.PromptSequenceAtAdmission && IsCompletionMarkerObserved(pending);
                }
            }
            if (settled) CompleteInteractiveCommand(pending, pending.InterruptRequested ? LiveConsoleCommandOutcome.Interrupted : LiveConsoleCommandOutcome.PromptObserved, pending.ObservedPromptSequence);
            DeveloperDiagnostics.LogInfo("PSES", "Direct console command input was submitted; execution outcome remains pending prompt observation.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = generation,
                ["processId"] = processId,
                ["commandLength"] = command.Length,
                ["elapsedMilliseconds"] = (DateTime.UtcNow - started).TotalMilliseconds,
                ["outcome"] = "Submitted",
                ["contentOmitted"] = true
            });
            return new LiveConsoleCommandResult("PSES console-repl command", false, CurrentWorkingDirectory, started, DateTime.UtcNow, requestId, generation, processId, LiveConsoleCommandOutcome.Submitted);
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_pendingInteractiveCommand, pending))
                {
                    _pendingInteractiveCommand = null;
                    _commandInProgress = false;
                    _interactiveReadinessUnverified = true;
                }
            }
            throw;
        }
    }

    private void ObserveNativeSelectionCompletionMarker(Guid requestId, int generation)
    {
        PendingInteractiveCommand? pending;
        bool markerObserved;
        bool markerSucceeded;
        bool freshPromptObserved;
        bool submitted;
        lock (_syncRoot)
        {
            pending = _pendingInteractiveCommand;
            if (pending is null || pending.RequestId != requestId || pending.Generation != generation || generation != _generation)
                return;
            markerObserved = TryReadCompletionMarker(pending.CompletionMarkerPath, out markerSucceeded);
            freshPromptObserved = pending.ObservedPromptSequence > pending.PromptSequenceAtAdmission;
            submitted = pending.Submitted;
        }

        if (!markerObserved) return;
        DeveloperDiagnostics.LogInfo("PSES", "Native Run Selection completion marker observed for its admitted request.", new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["sessionGeneration"] = generation,
            ["processId"] = pending!.ProcessId,
            ["markerStatus"] = markerSucceeded ? "OK" : "ERROR",
            ["submissionConfirmed"] = submitted,
            ["freshPromptAlreadyObserved"] = freshPromptObserved,
            ["completionEvidenceReady"] = IsNativeSelectionCompletionReady(submitted, freshPromptObserved, markerObserved),
            ["contentOmitted"] = true
        });
        if (!submitted) return;

        // The marker notification closes the prompt-before-marker race. The same
        // admission checks as prompt-driven completion still require the current
        // request, process, generation, and a fresh prompt observation.
        CompleteInteractiveCommand(pending,
            pending.InterruptRequested ? LiveConsoleCommandOutcome.Interrupted : CompletionMarkerSucceeded(pending)
                ? LiveConsoleCommandOutcome.PromptObserved : LiveConsoleCommandOutcome.ExecutionFailed,
            pending.ObservedPromptSequence);
    }

    private void CompleteInteractiveCommand(PendingInteractiveCommand pending, LiveConsoleCommandOutcome outcome, long promptSequence)
    {
        LiveConsoleCommandCompletion? completion = null;
        lock (_syncRoot)
        {
            if (!IsNativeSelectionCompletionReady(pending.Submitted,
                    pending.ObservedPromptSequence > pending.PromptSequenceAtAdmission,
                    IsCompletionMarkerObserved(pending)) || !ReferenceEquals(_pendingInteractiveCommand, pending) ||
                pending.Generation != _generation || _interactiveReadinessUnverified ||
                _conPty is not { HasExited: false } owner || owner.ProcessId != pending.ProcessId)
                return;
            _pendingInteractiveCommand = null;
            _commandInProgress = false;
            var completionOutcome = pending.InterruptRequested
                ? LiveConsoleCommandOutcome.Interrupted
                : CompletionMarkerSucceeded(pending)
                    ? outcome
                    : LiveConsoleCommandOutcome.ExecutionFailed;
            completion = new LiveConsoleCommandCompletion(
                pending.RequestId,
                pending.Generation,
                pending.ProcessId,
                completionOutcome,
                promptSequence,
                DateTimeOffset.UtcNow);
        }

        DeveloperDiagnostics.LogInfo("PSES", "Direct console command reached a same-generation prompt observation.", new Dictionary<string, object?>
        {
            ["requestId"] = completion.RequestId,
            ["sessionGeneration"] = completion.SessionGeneration,
            ["processId"] = completion.ProcessId,
            ["promptObservationSequence"] = completion.PromptObservationSequence,
            ["outcome"] = completion.Outcome.ToString(),
            ["elapsedMilliseconds"] = (completion.ObservedAt - pending.StartedAt).TotalMilliseconds,
            ["promptIsHeuristic"] = true,
            ["contentOmitted"] = true
        });
        var handlers = InteractiveCommandCompleted;
        if (handlers is null) return;
        foreach (Action<LiveConsoleCommandCompletion> handler in handlers.GetInvocationList())
        {
            try { handler(completion); }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("PSES", ex, "Direct command completion subscriber failed.", new Dictionary<string, object?>
                {
                    ["requestId"] = completion.RequestId,
                    ["sessionGeneration"] = completion.SessionGeneration,
                    ["processId"] = completion.ProcessId,
                    ["contentOmitted"] = true
                });
            }
        }
    }

    private static bool IsCompletionMarkerObserved(PendingInteractiveCommand pending)
        => pending.CompletionMarkerPath is null || TryReadCompletionMarker(pending.CompletionMarkerPath, out _);

    internal static bool IsNativeSelectionCompletionReady(bool submitted, bool freshPromptObserved, bool requestMarkerObserved)
        => submitted && freshPromptObserved && requestMarkerObserved;

    private static bool CompletionMarkerSucceeded(PendingInteractiveCommand pending)
        => pending.CompletionMarkerPath is null ||
           (TryReadCompletionMarker(pending.CompletionMarkerPath, out var succeeded) && succeeded);

    private static bool TryReadCompletionMarker(string? path, out bool succeeded)
    {
        succeeded = false;
        if (path is null) return true;
        try
        {
            var marker = File.ReadAllText(path).Trim();
            if (string.Equals(marker, "OK", StringComparison.Ordinal))
            {
                succeeded = true;
                return true;
            }
            return string.Equals(marker, "ERROR", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed class PendingInteractiveCommand(Guid requestId, int generation, int processId, long promptSequenceAtAdmission, DateTime startedAt, string? completionMarkerPath)
    {
        public Guid RequestId { get; } = requestId;
        public int Generation { get; } = generation;
        public int ProcessId { get; } = processId;
        public long PromptSequenceAtAdmission { get; } = promptSequenceAtAdmission;
        public DateTime StartedAt { get; } = startedAt;
        public string? CompletionMarkerPath { get; } = completionMarkerPath;
        public long ObservedPromptSequence { get; set; }
        public bool Submitted { get; set; }
        public bool InterruptRequested { get; set; }
    }

    private async Task WriteInputCoreAsync(string data, CancellationToken cancellationToken, ConPtyProcess? expectedConPty = null, int? expectedGeneration = null)
    {
        ConPtyProcess conPty;
        int generation;
        lock (_syncRoot)
        {
            conPty = expectedConPty ?? _conPty ?? throw new InvalidOperationException("PSES host is not running.");
            generation = expectedGeneration ?? _generation;
            if (!ReferenceEquals(_conPty, conPty) || generation != _generation || _shutdownRequested || _sessionFaulted || conPty.HasExited)
                throw new InvalidOperationException("The authoritative PSES session changed before the input write was admitted.");
        }
        var bytes = Encoding.UTF8.GetBytes(data);
        await _terminalInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        InputOutputProbe? inputProbe = null;
        try
        {
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_conPty, conPty) || generation != _generation || _shutdownRequested || _sessionFaulted || conPty.HasExited)
                    throw new InvalidOperationException("The authoritative PSES session changed while the input write was waiting for admission.");
                if (DeveloperDiagnostics.IsEnabled && (data.Contains('\r') || data.Contains('\n')))
                {
                    inputProbe = new InputOutputProbe(DeveloperDiagnostics.CurrentOperationId, generation, conPty.ProcessId, Stopwatch.GetTimestamp());
                    _pendingInputOutputProbe = inputProbe;
                }
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
            lock (_syncRoot)
            {
                if (ReferenceEquals(_pendingInputOutputProbe, inputProbe)) _pendingInputOutputProbe = null;
            }
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
        var decoder = new StreamingUtf8Decoder();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                CaptureRawConPtyBytes(buffer, count, generation, owner.ProcessId, kind);
                var text = decoder.Decode(buffer, count);
                if (text.Length == 0) continue;
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
                var promptBoundaryObserved = ObservePromptBoundary(text, generation);
                Guid? activeRequestId;
                bool cursorVisibleNow;
                lock (_syncRoot)
                {
                    activeRequestId = _activeStructuredEchoFilter?.RequestId;
                    cursorVisibleNow = _cursorVisible;
                }
                var frameDiagnostic = _promptFrameDiagnostics.Record(buffer.AsSpan(0, count), generation,
                    activeRequestId, promptBoundaryObserved, promptBoundaryObserved,
                    promptBoundaryObserved ? "accepted-by-existing-predicate" : "no-prompt-candidate",
                    cursorVisibleNow, promptBoundaryObserved);
                if (promptBoundaryObserved || frameDiagnostic.CsiSpannedFrames)
                {
                    DeveloperDiagnostics.LogInfo("PSES", "ConPTY prompt frame/control-sequence observation.", new Dictionary<string, object?>
                    {
                        ["frameSequence"] = frameDiagnostic.Sequence,
                        ["utc"] = frameDiagnostic.Utc,
                        ["sessionGeneration"] = generation,
                        ["requestId"] = activeRequestId,
                        ["byteCount"] = count,
                        ["csiStarted"] = frameDiagnostic.CsiStarted,
                        ["csiEnded"] = frameDiagnostic.CsiEnded,
                        ["csiSpannedFrames"] = frameDiagnostic.CsiSpannedFrames,
                        ["pendingControlSequence"] = frameDiagnostic.PendingControlSequence,
                        ["sequenceTypes"] = frameDiagnostic.SequenceTypes,
                        ["controlHex"] = frameDiagnostic.ControlHex,
                        ["promptCandidate"] = promptBoundaryObserved,
                        ["promptAccepted"] = promptBoundaryObserved,
                        ["cursorVisible"] = cursorVisibleNow,
                        ["authoritative"] = false
                    });
                }
                if (promptBoundaryObserved)
                {
                    TaskCompletionSource<long>? promptRecoveryWaiter;
                    PendingInteractiveCommand? pendingInteractiveCommand;
                    long promptObservation;
                    lock (_syncRoot)
                    {
                        promptObservation = ++_promptReadyObservation;
                        promptRecoveryWaiter = generation == _promptRecoveryGeneration
                            ? this._promptRecoveryWaiter
                            : null;
                        pendingInteractiveCommand = _pendingInteractiveCommand;
                        if (pendingInteractiveCommand is not null && pendingInteractiveCommand.Generation == generation)
                        {
                            pendingInteractiveCommand.ObservedPromptSequence = promptObservation;
                        }
                    }
                    promptRecoveryWaiter?.TrySetResult(promptObservation);
                    if (pendingInteractiveCommand is not null && pendingInteractiveCommand.Generation == generation)
                    {
                        CompleteInteractiveCommand(
                            pendingInteractiveCommand,
                            pendingInteractiveCommand.InterruptRequested ? LiveConsoleCommandOutcome.Interrupted : LiveConsoleCommandOutcome.PromptObserved,
                            promptObservation);
                    }
                    NotifyPromptReadyObserved(generation, text);
                }
            }
            var trailingText = decoder.Flush();
            if (trailingText.Length > 0)
            {
                capture.Append(trailingText);
                PublishOutput(kind, generation, trailingText);
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
        // Write-Progress can move the cursor to column one before PSReadLine
        // prints the next prompt without emitting a newline in the raw stream.
        lock (_syncRoot)
        {
            if (generation != _generation || _shutdownRequested || _sessionFaulted)
            {
                return false;
            }

            // Normalize the accumulated raw stream, not each ConPTY read in
            // isolation: ANSI CSI sequences and prompt text can split at any byte.
            _promptAnsiCarry += text;
            if (_promptAnsiCarry.Length > 2048) _promptAnsiCarry = _promptAnsiCarry[^2048..];
            var normalized = AnsiSequenceRegex.Replace(CursorColumnOneRegex.Replace(_promptAnsiCarry, "\n"), string.Empty)
                .Replace('\r', '\n');
            _promptRecognitionBuffer = normalized.Length > 1024 ? normalized[^1024..] : normalized;

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

        if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
            ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("PsesSessionTerminated",
                TerminalGeneration: generation, ProcessId: owner.ProcessId, EventSource: "PSES/ConPTY",
                EventType: "PowerShellProcessTerminated", EventOutcome: "Terminated",
                CorrelationConfidence: "GlobalObservation"));
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
            if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("PsesSessionTerminated",
                    TerminalGeneration: generation, ProcessId: ProcessId, EventSource: "PSES/LSP",
                    EventType: "LanguageServiceSessionTerminated", EventOutcome: "Faulted",
                    CorrelationConfidence: "GlobalObservation"));
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
        InputOutputProbe? inputProbe;
        try
        {
            var preFilterHandlers = OutputObservedBeforeDisplayFiltering;
            if (preFilterHandlers is not null)
            {
                foreach (Action<int, string> handler in preFilterHandlers.GetInvocationList())
                {
                    try { handler(generation, text); }
                    catch (Exception ex)
                    {
                        DeveloperDiagnostics.LogException("PSES", ex, "Pre-filter output observer failed.", new Dictionary<string, object?>
                        {
                            ["sessionGeneration"] = generation,
                            ["processId"] = ProcessId,
                            ["textLength"] = text.Length,
                            ["contentOmitted"] = true
                        });
                    }
                }
            }
        }
        catch { /* Diagnostics and observers must not disrupt terminal output. */ }
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
                    ? FilterAndTraceEcho(echoFilter, generation, string.Concat(executionState.Process(text)))
                    : string.Concat(executionState.Process(text))
                : _activeStructuredEchoFilter is { } directEchoFilter
                    ? FilterAndTraceEcho(directEchoFilter, generation, text)
                    : text;
            inputProbe = _pendingInputOutputProbe is { } pending && pending.Generation == generation
                ? pending
                : null;
            if (inputProbe is not null) _pendingInputOutputProbe = null;
        }
        if (inputProbe is not null)
        {
            DeveloperDiagnostics.LogInfo("PSES", "First ConPTY output observed after an Enter-bearing input write; temporal ordering does not prove command consumption.", new Dictionary<string, object?>
            {
                ["inputOperationId"] = inputProbe.OperationId,
                ["sessionGeneration"] = generation,
                ["processId"] = inputProbe.ProcessId,
                ["elapsedAfterInputAttemptMs"] = Stopwatch.GetElapsedTime(inputProbe.StartedAt).TotalMilliseconds,
                ["rawOutputLength"] = text.Length,
                ["visibleOutputLength"] = visibleText.Length,
                ["contentOmitted"] = true
            });
        }
        PublishVisibleOutput(kind, generation, visibleText);
    }

    private static string FilterAndTraceEcho(StructuredEchoFilter filter, int generation, string text)
    {
        if (!filter.IsDebuggerLaunchEcho)
        {
            return filter.Filter(text);
        }

        var candidateWasObserved = filter.CandidateEchoObserved;
        var previousMatchCount = filter.MatchedEchoCount;
        var visible = filter.Filter(text);
        if (!candidateWasObserved && filter.CandidateEchoObserved)
        {
            DeveloperDiagnostics.LogInfo("Debugger", "First candidate debugger launch echo fragment observed.", new Dictionary<string, object?>
            {
                ["debugSessionId"] = filter.RequestId,
                ["terminalGeneration"] = generation,
                ["fragmentLength"] = text.Length,
                ["matchInFirstFragment"] = filter.MatchedEchoCount > previousMatchCount,
                ["contentOmitted"] = true
            });
        }
        if (filter.MatchedEchoCount > previousMatchCount)
        {
            DeveloperDiagnostics.LogInfo("Debugger", "Debugger launch echo matched and was suppressed.", new Dictionary<string, object?>
            {
                ["debugSessionId"] = filter.RequestId,
                ["terminalGeneration"] = generation,
                ["matchedEchoCount"] = filter.MatchedEchoCount,
                ["candidateFragmentLength"] = text.Length,
                ["contentOmitted"] = true
            });
        }
        return visible;
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
        try { onOutput?.Invoke(record); }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "Terminal output subscriber failed after ConPTY output was decoded.", new Dictionary<string, object?>
            {
                ["sessionGeneration"] = generation,
                ["processId"] = ProcessId,
                ["outputLength"] = visibleText.Length,
                ["contentOmitted"] = true
            });
        }
        var rawSubscribers = RawOutputReceived;
        if (rawSubscribers is null) return;
        foreach (Action<int, string> subscriber in rawSubscribers.GetInvocationList())
        {
            try { subscriber(generation, visibleText); }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("PSES", ex, "Raw terminal output subscriber failed; remaining subscribers will still receive the output.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = generation,
                    ["processId"] = ProcessId,
                    ["outputLength"] = visibleText.Length,
                    ["contentOmitted"] = true
                });
            }
        }
    }

    private void NotifyPromptReadyObserved(int generation, string text)
    {
        Guid? activeRequestId;
        lock (_syncRoot) activeRequestId = _activeStructuredEchoFilter?.RequestId;
        if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
            ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("TerminalPromptObserved",
                RequestId: activeRequestId, TerminalGeneration: generation, ProcessId: ProcessId,
                EventSource: "ConPTY/TerminalPresentation", EventType: "PromptPresentationObserved",
                CorrelationConfidence: activeRequestId.HasValue ? "RequestContextAtObservation" : "PresentationOnly",
                Reason: "prompt-text-omitted"));
        var handlers = PromptReadyObserved;
        if (handlers is null) return;
        foreach (Action<int, string> handler in handlers.GetInvocationList())
        {
            try { handler(generation, text); }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("PSES", ex, "Prompt-ready subscriber callback failed; remaining subscribers will still be notified.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = generation,
                    ["processId"] = ProcessId,
                    ["requestId"] = activeRequestId,
                    ["subscriberMethod"] = handler.Method.Name,
                    ["promptTextLength"] = text.Length,
                    ["contentOmitted"] = true
                });
            }
        }
    }

    private void NotifyStructuredPromptReadyObserved(StructuredPromptReadyObservation observation)
    {
        var handlers = StructuredPromptReadyObserved;
        if (handlers is null) return;
        foreach (Action<StructuredPromptReadyObservation> handler in handlers.GetInvocationList())
        {
            try { handler(observation); }
            catch (Exception ex)
            {
                if (ExecutionCorrelationTelemetry.Shared.IsEnabled)
                    ExecutionCorrelationTelemetry.Shared.Record(new ExecutionCorrelationEvent("ServicePromptConsumerFailed",
                        RequestId: observation.RequestId, TerminalGeneration: observation.TerminalGeneration,
                        ProcessId: observation.ProcessId, Reason: "subscriber-exception", ActualReady: false));
                DeveloperDiagnostics.LogException("PSES", ex, "Structured prompt-ready subscriber callback failed.", new Dictionary<string, object?>
                {
                    ["requestId"] = observation.RequestId,
                    ["sessionGeneration"] = observation.TerminalGeneration,
                    ["processId"] = observation.ProcessId,
                    ["subscriberMethod"] = handler.Method.Name,
                    ["contentOmitted"] = true
                });
            }
        }
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

    private static string? TryOpenMatchingSavedScript(string? candidatePath, string scriptContent, out FileStream? readLock)
    {
        readLock = null;
        if (string.IsNullOrWhiteSpace(candidatePath)) return null;

        try
        {
            var fullPath = Path.GetFullPath(candidatePath);
            if (!string.Equals(Path.GetExtension(fullPath), ".ps1", StringComparison.OrdinalIgnoreCase)) return null;

            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var diskContent = reader.ReadToEnd();
                if (!string.Equals(diskContent, scriptContent, StringComparison.Ordinal))
                {
                    stream.Dispose();
                    return null;
                }

                readLock = stream;
                return fullPath;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            DeveloperDiagnostics.LogInfo("PSES", "Saved Run File path could not be validated; the immutable editor snapshot will be used.", new Dictionary<string, object?>
            {
                ["sourcePath"] = DeveloperDiagnostics.SanitizePath(candidatePath),
                ["reason"] = ex.GetType().Name,
                ["contentOmitted"] = true
            });
            return null;
        }
    }

    internal static string BuildNativeSelectionInvocation(string snapshotPath, string completionMarkerPath, Guid requestId)
    {
        var snapshot = QuotePowerShellSingleQuotedString(snapshotPath);
        var marker = QuotePowerShellSingleQuotedString(completionMarkerPath);
        var temporaryMarker = QuotePowerShellSingleQuotedString(completionMarkerPath + ".tmp");
        var statusVariable = "__ps7sd_selection_success_" + requestId.ToString("N", CultureInfo.InvariantCulture);
        var statusName = QuotePowerShellSingleQuotedString(statusVariable);
        return "$" + statusVariable + " = $true; try { . " + snapshot + "; $" + statusVariable + " = $? } " +
            "catch { $" + statusVariable + " = $false; throw } finally { " +
            "[System.IO.File]::WriteAllText(" + temporaryMarker + ", $(if ($" + statusVariable + ") { 'OK' } else { 'ERROR' })); " +
            "[System.IO.File]::Move(" + temporaryMarker + ", " + marker + ", $true); " +
            "Remove-Variable -Name " + statusName + " -ErrorAction SilentlyContinue }";
    }

    internal static string BuildExperimentalDirectRunFileInvocation(string quotedSnapshotPath, string quotedStatusPath, string statusVariable)
        => "& { $" + statusVariable + " = $true; try { if (-not [System.IO.File]::Exists(" + quotedSnapshotPath + ")) { throw 'The editor snapshot is missing.' }; & " + quotedSnapshotPath + "; $" + statusVariable + " = $? } catch { $" + statusVariable + " = $false; throw } finally { if ($" + statusVariable + ") { [System.IO.File]::WriteAllText(" + quotedStatusPath + ", 'OK') } else { [System.IO.File]::WriteAllText(" + quotedStatusPath + ", 'ERROR') }; Remove-Variable -Name '" + statusVariable + "' -ErrorAction SilentlyContinue } }";

    internal static string BuildExperimentalDirectRunSelectionInvocation(string quotedSnapshotPath, string quotedStatusPath, string statusVariable)
        => "$" + statusVariable + " = $true; try { if (-not [System.IO.File]::Exists(" + quotedSnapshotPath + ")) { throw 'The editor snapshot is missing.' }; . " + quotedSnapshotPath + "; $" + statusVariable + " = $? } catch { $" + statusVariable + " = $false; throw } finally { if ($" + statusVariable + ") { [System.IO.File]::WriteAllText(" + quotedStatusPath + ", 'OK') } else { [System.IO.File]::WriteAllText(" + quotedStatusPath + ", 'ERROR') }; Remove-Variable -Name '" + statusVariable + "' -ErrorAction SilentlyContinue }";

    private static string BuildExperimentalDirectInvocation(string quotedSnapshotPath, string quotedStatusPath, string statusVariable, bool executeInCurrentScope)
        => executeInCurrentScope
            ? BuildExperimentalDirectRunSelectionInvocation(quotedSnapshotPath, quotedStatusPath, statusVariable)
            : BuildExperimentalDirectRunFileInvocation(quotedSnapshotPath, quotedStatusPath, statusVariable);

    internal static string[] BuildInvocationEchoForms(string invocation, bool executeInCurrentScope)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocation);
        if (!executeInCurrentScope || !invocation.StartsWith(". ", StringComparison.Ordinal))
        {
            return [invocation];
        }

        // PSES can echo the current-scope request as just its generated global
        // variable operand. That request-specific display form is still anchored
        // by StructuredEchoFilter to the interactive prompt and request window.
        return [invocation, invocation[2..]];
    }

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
        private readonly bool _preservePromptPrefix;
        private readonly StringBuilder _pending = new();
        private int _suppressedCharacters;
        private int _promptPrefixedSuppressedCharacters;
        private int _matchedEchoCount;
        private bool _candidateEchoObserved;
        private bool _isDebuggerLaunchEcho;
        private bool _consumeEchoLineTerminator;
        private int _echoLineTerminatorIndex;

        public Guid RequestId => _requestId;
        public int MatchedEchoCount => _matchedEchoCount;
        public bool CandidateEchoObserved => _candidateEchoObserved;
        public bool IsDebuggerLaunchEcho => _isDebuggerLaunchEcho;
        public void MarkDebuggerLaunchEcho() => _isDebuggerLaunchEcho = true;

        public StructuredEchoFilter(Guid requestId, params string[] scriptContents)
            : this(requestId, preservePromptPrefix: false, scriptContents)
        {
        }

        public StructuredEchoFilter(Guid requestId, bool preservePromptPrefix, params string[] scriptContents)
        {
            _requestId = requestId;
            _preservePromptPrefix = preservePromptPrefix;
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
            _pending.Append(text);
            if (!_candidateEchoObserved && HasPromptAnchoredCandidate(_pending.ToString()))
            {
                _candidateEchoObserved = true;
            }
            SuppressCompleteEchoes();
            ConsumeEchoLineTerminator();
            var pendingText = _pending.ToString();
            if (_consumeEchoLineTerminator ||
                IsAwaitingPromptPrefixedNormalizedEcho(pendingText) ||
                IsAwaitingRequestScopedGeneratedInvocationEcho(pendingText))
            {
                return string.Empty;
            }
            // After the generated invocation has been removed, all subsequent
            // ConPTY output belongs to the running command. In particular,
            // Read-Host's ordinary input echo must reach the renderer before Enter.
            var retainedSuffix = _suppressedCharacters > 0 ? 0 : _retainedSuffixLength;
            var emitLength = Math.Max(0, _pending.Length - retainedSuffix);
            if (emitLength == 0)
            {
                return string.Empty;
            }

            var visible = _pending.ToString(0, emitLength);
            _pending.Remove(0, emitLength);
            return visible;
        }

        public string Flush()
        {
            SuppressCompleteEchoes();
            ConsumeEchoLineTerminator(final: true);
            var visible = _pending.ToString();
            if (_preservePromptPrefix && _candidateEchoObserved && _matchedEchoCount == 0)
            {
                var marker = "__ps7sd_status_" + _requestId.ToString("N", CultureInfo.InvariantCulture);
                var markerIndex = visible.LastIndexOf(marker, StringComparison.Ordinal);
                DeveloperDiagnostics.LogWarning("PSES", "Prompt-anchored generated echo remained unmatched at request cleanup.", new Dictionary<string, object?>
                {
                    ["requestId"] = _requestId,
                    ["pendingLength"] = visible.Length,
                    ["requestMarkerPresent"] = markerIndex >= 0,
                    ["statusWritePresent"] = visible.Contains("[System.IO.File]::WriteAllText(", StringComparison.Ordinal),
                    ["cleanupCommandPresent"] = visible.Contains("Remove-Variable", StringComparison.Ordinal),
                    ["lineFeedAfterMarker"] = markerIndex >= 0 && visible.IndexOf('\n', markerIndex + marker.Length) >= 0,
                    ["contentOmitted"] = true
                });
            }
            _pending.Clear();
            _consumeEchoLineTerminator = false;
            _echoLineTerminatorIndex = 0;
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
                    var pendingText = _pending.ToString();
                    var hasRequestMarker = ContainsRequestScopedGeneratedEchoMarker(pendingText);
                    promptPrefixedMatch = FindPromptPrefixedNormalizedEcho(
                        pendingText, allowPayloadBoundaryFallback: !hasRequestMarker);
                    if (promptPrefixedMatch.Length == 0)
                    {
                        promptPrefixedMatch = FindRequestScopedGeneratedEcho(pendingText);
                        if (promptPrefixedMatch.Length == 0)
                        {
                            promptPrefixedMatch = FindRequestScopedGeneratedEchoLine(pendingText);
                            if (promptPrefixedMatch.Length == 0) return;
                        }
                    }
                }

                var preservedControls = _preservePromptPrefix
                    ? ExtractAnsiControlSequences(_pending.ToString(promptPrefixedMatch.Index, promptPrefixedMatch.Length))
                    : string.Empty;
                _pending.Remove(promptPrefixedMatch.Index, promptPrefixedMatch.Length);
                if (preservedControls.Length > 0) _pending.Insert(promptPrefixedMatch.Index, preservedControls);
                _suppressedCharacters += promptPrefixedMatch.Length;
                _promptPrefixedSuppressedCharacters += promptPrefixedMatch.Length;
                _matchedEchoCount++;
                _consumeEchoLineTerminator = true;
                _echoLineTerminatorIndex = promptPrefixedMatch.Index + preservedControls.Length;
                ConsumeEchoLineTerminator();
            }
        }

        private void ConsumeEchoLineTerminator(bool final = false)
        {
            if (!_consumeEchoLineTerminator || _pending.Length <= _echoLineTerminatorIndex) return;
            if (_pending[_echoLineTerminatorIndex] == '\r')
            {
                if (_pending.Length == _echoLineTerminatorIndex + 1 && !final) return;
                var consumed = _pending.Length > _echoLineTerminatorIndex + 1 && _pending[_echoLineTerminatorIndex + 1] == '\n' ? 2 : 1;
                _pending.Remove(_echoLineTerminatorIndex, consumed);
                _suppressedCharacters += consumed;
                _consumeEchoLineTerminator = false;
                return;
            }

            if (_pending[_echoLineTerminatorIndex] == '\n')
            {
                _pending.Remove(_echoLineTerminatorIndex, 1);
                _suppressedCharacters++;
            }
            _consumeEchoLineTerminator = false;
        }

        private bool HasPromptAnchoredCandidate(string pending)
        {
            foreach (var expected in _expectedForms)
            {
                var firstLineLength = expected.IndexOf('\n');
                var firstLine = firstLineLength >= 0 ? expected[..firstLineLength] : expected;
                var tokenLength = Math.Min(5, firstLine.Length);
                if (tokenLength == 0) continue;
                var tokenIndex = pending.IndexOf(firstLine[..tokenLength], StringComparison.Ordinal);
                if (tokenIndex < 0) continue;
                var lineStart = pending.LastIndexOf('\n', Math.Max(0, tokenIndex - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                if (pending[lineStart..tokenIndex].Contains('>')) return true;
            }
            return false;
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
                        var removalStart = _preservePromptPrefix ? firstLineIndex : lineStart;
                        return (removalStart, firstLineIndex + expected.Length - removalStart);
                    }

                    searchFrom = firstLineIndex + firstLine.Length;
                }
            }

            return (0, 0);
        }

        private (int Index, int Length) FindPromptPrefixedNormalizedEcho(
            string pending,
            bool allowPayloadBoundaryFallback)
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
                        if (!allowPayloadBoundaryFallback)
                        {
                            searchFrom = tokenIndex + firstToken.Length;
                            continue;
                        }

                        // ConPTY/PSReadLine may redraw a submitted line with cursor
                        // movement and wrapped fragments that do not preserve a
                        // byte-for-byte normalized prefix. Once the prompt and the
                        // first script token are proven, the editor payload length
                        // still gives us a safe boundary for removing that echo.
                        var lengthByPayload = CountRawCharactersForNormalizedLength(pending[tokenIndex..], expected.Length);
                        if (lengthByPayload > 0)
                        {
                            var removalStart = _preservePromptPrefix ? tokenIndex : lineStart;
                            return (removalStart, tokenIndex + lengthByPayload - removalStart);
                        }

                        searchFrom = tokenIndex + firstToken.Length;
                        continue;
                    }

                    var rawLength = CountRawCharactersForNormalizedLength(pending[tokenIndex..], expected.Length);
                    if (rawLength > 0)
                    {
                        var removalStart = _preservePromptPrefix ? tokenIndex : lineStart;
                        return (removalStart, tokenIndex + rawLength - removalStart);
                    }
                    searchFrom = tokenIndex + firstToken.Length;
                }
            }

            return (0, 0);
        }

        private bool ContainsRequestScopedGeneratedEchoMarker(string pending)
        {
            if (!_preservePromptPrefix || _requestId == Guid.Empty)
            {
                return false;
            }

            return pending.Contains("__ps7sd_status_" + _requestId.ToString("N", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private (int Index, int Length) FindRequestScopedGeneratedEcho(string pending)
        {
            if (!_preservePromptPrefix || _requestId == Guid.Empty)
            {
                return (0, 0);
            }

            var requestMarker = "__ps7sd_status_" + _requestId.ToString("N", CultureInfo.InvariantCulture);
            var markerIndex = pending.IndexOf(requestMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return (0, 0);
            }

            var promptIndex = pending.LastIndexOf('>', markerIndex - 1);
            if (promptIndex < 0)
            {
                return (0, 0);
            }

            var invocationStart = pending.IndexOf("& {", promptIndex + 1, markerIndex - promptIndex - 1, StringComparison.Ordinal);
            if (invocationStart < 0)
            {
                return (0, 0);
            }

            // The terminal can wrap this long single-line command across rows
            // and PSReadLine can redraw a printable fragment after a cursor
            // move. Use the request-specific cleanup tail as the completion
            // boundary rather than a physical line ending or raw payload size.
            // The status variable occurs several times in the wrapper, with its
            // final occurrence in the cleanup tail. PSReadLine can redraw the
            // cleanup command name as fragments around cursor controls, so use
            // the final request marker and the stable suffix after it.
            var finalMarkerIndex = pending.LastIndexOf(requestMarker, StringComparison.Ordinal);
            if (finalMarkerIndex < invocationStart)
            {
                return (0, 0);
            }

            var errorActionIndex = pending.IndexOf("-ErrorAction SilentlyContinue", finalMarkerIndex + requestMarker.Length, StringComparison.Ordinal);
            if (errorActionIndex < 0)
            {
                return (0, 0);
            }

            var firstClosingBrace = pending.IndexOf('}', errorActionIndex + "-ErrorAction SilentlyContinue".Length);
            var secondClosingBrace = firstClosingBrace < 0 ? -1 : pending.IndexOf('}', firstClosingBrace + 1);
            if (secondClosingBrace < 0)
            {
                return (0, 0);
            }

            var normalizedCleanupSuffix = NormalizeEchoText(pending[finalMarkerIndex..(secondClosingBrace + 1)]);
            var expectedCleanupSuffix = NormalizeEchoText(requestMarker + "' -ErrorAction SilentlyContinue }}");
            if (!string.Equals(normalizedCleanupSuffix, expectedCleanupSuffix, StringComparison.Ordinal))
            {
                return (0, 0);
            }

            var invocationEnd = secondClosingBrace + 1;
            var lineEnd = pending.IndexOfAny(['\r', '\n'], invocationEnd);
            if (lineEnd < 0)
            {
                return (0, 0);
            }
            if (NormalizeEchoText(pending[invocationEnd..lineEnd]).Length > 0)
            {
                return (0, 0);
            }

            return (invocationStart, lineEnd - invocationStart);
        }

        private (int Index, int Length) FindRequestScopedGeneratedEchoLine(string pending)
        {
            if (!_preservePromptPrefix || _requestId == Guid.Empty) return (0, 0);

            // A PSReadLine redraw can duplicate printable text inside the cleanup
            // tail, defeating both exact and normalized matching. The generated
            // direct Run File command is a single submitted line. Only recover a
            // prompt-anchored line carrying this request's private status marker,
            // status write, and cleanup command; retain everything after its LF.
            var marker = "__ps7sd_status_" + _requestId.ToString("N", CultureInfo.InvariantCulture);
            var markerIndex = pending.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return (0, 0);
            var promptIndex = pending.LastIndexOf('>', markerIndex - 1);
            if (promptIndex < 0) return (0, 0);
            var invocationStart = pending.IndexOf("& {", promptIndex + 1, markerIndex - promptIndex - 1, StringComparison.Ordinal);
            if (invocationStart < 0) return (0, 0);
            var lineEnd = pending.IndexOf('\n', markerIndex + marker.Length);
            if (lineEnd < 0) return (0, 0);
            var candidate = pending[invocationStart..lineEnd];
            if (!candidate.Contains("[System.IO.File]::WriteAllText(", StringComparison.Ordinal) ||
                !candidate.Contains("Remove-Variable", StringComparison.Ordinal)) return (0, 0);

            DeveloperDiagnostics.LogInfo("PSES", "Request-scoped generated invocation echo recovered at its submitted line boundary.", new Dictionary<string, object?>
            {
                ["requestId"] = _requestId,
                ["candidateLength"] = candidate.Length,
                ["contentOmitted"] = true
            });
            return (invocationStart, lineEnd - invocationStart);
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

        private bool IsAwaitingRequestScopedGeneratedInvocationEcho(string pending)
        {
            if (!_preservePromptPrefix || _requestId == Guid.Empty)
            {
                return false;
            }

            // Direct Run File's generated command carries its request ID in the
            // private status variable. PSReadLine can redraw the submitted line
            // with cursor controls or partial chunks that temporarily break the
            // normalized-prefix check above. Once the prompt and this exact
            // request marker are both present, keep the candidate intact until
            // its line terminator lets the normal exact echo matcher decide.
            var requestMarker = "__ps7sd_status_" + _requestId.ToString("N", CultureInfo.InvariantCulture);
            var markerIndex = pending.IndexOf(requestMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return false;
            }

            var promptIndex = pending.LastIndexOf('>', markerIndex - 1);
            if (promptIndex < 0)
            {
                return false;
            }

            return pending.IndexOf("& {", promptIndex + 1, markerIndex - promptIndex - 1, StringComparison.Ordinal) >= 0;
        }

        private static string NormalizeEchoText(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\u001b')
                {
                    index = SkipAnsiControlSequence(value, index) - 1;
                    continue;
                }

                if (!char.IsControl(character) && !char.IsWhiteSpace(character)) builder.Append(character);
            }
            return builder.ToString();
        }

        private static string ExtractAnsiControlSequences(string value)
        {
            var controls = new StringBuilder();
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '\u001b' || index + 1 >= value.Length) continue;
                var start = index;
                if (value[index + 1] == '[')
                {
                    index += 2;
                    while (index < value.Length && value[index] is not (>= '@' and <= '~')) index++;
                    if (index < value.Length) controls.Append(value, start, index - start + 1);
                    else controls.Append(value.AsSpan(start));
                }
                else if (value[index + 1] == ']')
                {
                    index += 2;
                    while (index < value.Length && value[index] != '\a' && !(value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\')) index++;
                    if (index < value.Length && value[index] == '\u001b') index++;
                    if (index < value.Length) controls.Append(value, start, index - start + 1);
                    else controls.Append(value.AsSpan(start));
                }
                else
                {
                    index++;
                    controls.Append(value, start, index - start + 1);
                }
            }
            return controls.ToString();
        }

        private static int CountRawCharactersForNormalizedLength(string value, int normalizedLength)
        {
            var count = 0;
            var rawIndex = 0;
            while (rawIndex < value.Length && count < normalizedLength)
            {
                var character = value[rawIndex];
                if (character == '\u001b')
                {
                    rawIndex = SkipAnsiControlSequence(value, rawIndex);
                    continue;
                }

                if (!char.IsControl(character) && !char.IsWhiteSpace(character)) count++;
                rawIndex++;
            }
            return count == normalizedLength ? rawIndex : 0;
        }

        private static int SkipAnsiControlSequence(string value, int start)
        {
            if (start + 1 >= value.Length)
            {
                return value.Length;
            }

            if (value[start + 1] == '[')
            {
                var index = start + 2;
                while (index < value.Length && value[index] is not (>= '@' and <= '~')) index++;
                return index < value.Length ? index + 1 : value.Length;
            }

            if (value[start + 1] == ']')
            {
                var index = start + 2;
                while (index < value.Length && value[index] != '\a' &&
                    !(value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\'))
                {
                    index++;
                }

                if (index < value.Length && value[index] == '\u001b') index++;
                return index < value.Length ? index + 1 : value.Length;
            }

            return start + 2;
        }
    }

    private sealed record InputOutputProbe(string? OperationId, int Generation, int ProcessId, long StartedAt);

    internal sealed record StructuredMarkerResult(string Status, string? ErrorMessage);
}
