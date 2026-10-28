using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
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

    public bool IsSessionRunning => _conPty is { HasExited: false } && !_sessionFaulted;
    public bool IsInputWritable => IsSessionRunning;
    public bool IsCommandInProgress => false;
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
        }

        var generation = _generation;
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
        => ExecuteTextAsync(commandText, onOutput, cancellationToken);

    public async Task<LiveConsoleCommandResult> ExecuteScriptAsync(string documentDisplayName, string scriptContent, Action<ExecutionOutputRecord> onOutput, bool executeInCurrentScope = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scriptContent);
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "PSES-owned ExecuteScriptAsync entered.",
            new Dictionary<string, object?>
            {
                ["providerType"] = GetType().FullName,
                ["documentDisplayName"] = DeveloperDiagnostics.SanitizePreview(documentDisplayName, 120),
                ["scriptLength"] = scriptContent.Length,
                ["executeInCurrentScope"] = executeInCurrentScope,
                ["sessionGeneration"] = _generation,
                ["processId"] = ProcessId,
                ["inputWritable"] = IsInputWritable,
                ["contentOmitted"] = true
            });
        var result = await ExecuteTextAsync(scriptContent, onOutput, cancellationToken).ConfigureAwait(false);
        ScriptExecutionCompleted?.Invoke();
        DeveloperDiagnostics.LogInfo(
            "Phase2CProof",
            "PSES-owned ExecuteScriptAsync exited after completion event.",
            new Dictionary<string, object?>
            {
                ["providerType"] = GetType().FullName,
                ["sessionGeneration"] = _generation,
                ["processId"] = ProcessId,
                ["resultWasStopped"] = result.WasStopped
            });
        return result;
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
        var executionGateAcquired = false;

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
            var client = _languageServiceClient ?? throw new InvalidOperationException("The authoritative PSES language-service adapter is unavailable.");
            DeveloperDiagnostics.LogInfo("PSES", "STRUCTURED_LSP_REQUEST_CREATED.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["backendId"] = "authoritative-pses-session",
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration,
                ["scriptLength"] = scriptContent.Length,
                ["executeInCurrentScope"] = executeInCurrentScope,
                ["contentOmitted"] = true
            });
            protocolResult = await client.EvaluateAsync(requestId, scriptContent, cancellationToken).ConfigureAwait(false);
            var status = protocolResult.Status;
            DeveloperDiagnostics.LogInfo("Phase2CProof", "STRUCTURED_RESULT_CREATED.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration,
                ["status"] = status.ToString(),
                ["completionObserved"] = protocolResult.ResponseReceived,
                ["protocolError"] = protocolResult.ProtocolError,
                ["durationMs"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            });
            return new AuthoritativeStructuredExecutionResult(status, CurrentWorkingDirectory, protocolResult.ErrorMessage ?? protocolResult.ProtocolError, protocolResult.ResponseReceived);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var interruptSent = await StopConsoleForGenerationAsync(sessionGeneration).ConfigureAwait(false);
            DeveloperDiagnostics.LogInfo("PSES", "Structured client cancellation escalated to an explicit terminal interrupt; prompt recovery remains a separate observation.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["interruptSent"] = interruptSent,
                ["pipelineInterruptionProven"] = false
            });
            return new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Cancelled, CurrentWorkingDirectory,
                interruptSent ? "Execution canceled; terminal interrupt sent, pipeline interruption not yet proven." : "Execution canceled; terminal interrupt could not be sent.", false);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("Phase2CProof", ex, "Authoritative structured execution failed.", new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["processId"] = ProcessId,
                ["sessionGeneration"] = sessionGeneration
            });
            return new AuthoritativeStructuredExecutionResult(EditorExecutionStatus.Failed, CurrentWorkingDirectory, ex.Message, false);
        }
        finally
        {
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

    private async Task<bool> StopConsoleForGenerationAsync(int expectedGeneration)
    {
        ConPtyProcess? expectedConPty;
        lock (_syncRoot)
        {
            expectedConPty = _conPty;
            if (_shutdownRequested || _sessionFaulted || expectedGeneration != _generation || expectedConPty is null || expectedConPty.HasExited)
                return false;
        }
        try
        {
            await WriteInputCoreAsync("\u0003", CancellationToken.None, expectedConPty, expectedGeneration).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSES", ex, "PSES console-repl interrupt failed.");
            return false;
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
                var text = Encoding.UTF8.GetString(buffer, 0, count);
                ResizeForensicsTrace.Record(
                    $"PsesReceive.{kind}",
                    generation,
                    text,
                    processId: ProcessId,
                    file: "pses-fingerprints.jsonl");
                capture.Append(text);
                PublishOutput(kind, generation, text);
                if (text.Contains(">>", StringComparison.Ordinal) || text.Contains("PS ", StringComparison.Ordinal))
                    try { PromptReadyObserved?.Invoke(generation, text); } catch { }
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
        }
        var record = new ExecutionOutputRecord(kind, text, DateTime.UtcNow);
        try { onOutput?.Invoke(record); } catch { }
        try { RawOutputReceived?.Invoke(generation, text); } catch { }
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

    internal sealed record StructuredMarkerResult(string Status, string? ErrorMessage);
}
