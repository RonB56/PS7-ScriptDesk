using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.AcceptanceRunner;

internal static class Program
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CoreBudget = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main()
    {
        var started = DateTimeOffset.Now;
        var evidence = Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence))
            evidence = Path.Combine(AppContext.BaseDirectory, "evidence", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(evidence);
        var result = new RunResult(started, Environment.Version.ToString(), Environment.OSVersion.ToString());
        var productionMigrationMode = string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_PROD01"), "1", StringComparison.Ordinal);
        var bridgeR1Mode = string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_BRIDGE_R1"), "1", StringComparison.Ordinal);
        var bridgeProtocolMode = string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_BRIDGE_PROTOCOL"), "1", StringComparison.Ordinal);
        var bridgeT02OnlyMode = string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_BRIDGE_T02_ONLY"), "1", StringComparison.Ordinal);
        result.DirectRunSelectionEnabled = string.Equals(
            Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_DIRECT_RUNSELECTION"), "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_DIRECT_RUNSELECTION"), "true", StringComparison.OrdinalIgnoreCase) || bridgeR1Mode || bridgeProtocolMode || bridgeT02OnlyMode;
        if (productionMigrationMode)
        {
            result.DirectRunSelectionEnabled = ExperimentalRunSelectionBridgeFeatureGate.FromEnvironment().IsEnabled;
            result.Notes.Add($"Production selection gate enabled={result.DirectRunSelectionEnabled}; PS7SCRIPTDESK_EXPERIMENTAL_DIRECT_RUNSELECTION=0 opts out.");
        }
        else
            result.Notes.Add($"Runner route override: PS7SD_ACCEPTANCE_DIRECT_RUNSELECTION={result.DirectRunSelectionEnabled}.");
        if (string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_FAST01"), "1", StringComparison.Ordinal))
            result.Notes.Add("Fast01 route attribution: ordinary current-scope Run Selection uses the configured direct PSES candidate; classifier-known interactive selections retain native ConPTY fallback.");
        var timer = Stopwatch.StartNew();
        using var budget = new CancellationTokenSource(CoreBudget);
        string? pwshPath = ResolvePwshPath();
        string? psesRoot = ResolvePsesRoot();
        result.PowerShellPath = pwshPath;
        result.PsesRoot = psesRoot;
        if (pwshPath is not null && File.Exists(pwshPath))
        {
            result.PowerShellVersion = await ReadPwshVersionAsync(pwshPath, CaseTimeout);
            result.PsesVersion = ReadPsesVersion(psesRoot);
        }

        var outputLog = new SequencedOutputCapture();
        var visibleOutputLog = new SequencedOutputCapture();
        var redactions = new List<string>();
        foreach (var check in HarnessContractChecks.Run())
            result.Tests.Add(new TestResult(check.Id, check.Name, "Synthetic/contract test", "Harness self-check detects boundary or failure-classification defects.",
                check.Detail, check.Passed ? "PASS" : "FAIL", check.Passed ? null : check.Detail, Math.Round(check.DurationSeconds, 3)));
        var service = new PsesOwnedInteractiveSessionService(
            new ExperimentalRunFileBridgeFeatureGate(false),
            new ExperimentalRunSelectionBridgeFeatureGate(isEnabled: result.DirectRunSelectionEnabled),
            new PsesEditorBridgeFeatureGate(isEnabled: bridgeR1Mode || bridgeProtocolMode || bridgeT02OnlyMode || PsesEditorBridgeFeatureGate.FromEnvironment().IsEnabled));
        var sessionStarted = false;
        int? ownedSessionProcessId = null;
        DateTimeOffset? ownedSessionProcessStartUtc = null;
        try
        {
            if (pwshPath is null || !File.Exists(pwshPath) || psesRoot is null ||
                !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            {
                AddBlockedPrerequisites(result, "PowerShell 7 or PSES was not found. Set PS7SCRIPT_DESK_TEST_PWSH / PS7SCRIPTDESK_PSES_ROOT or install the defaults.");
            }
            else
            {
                var originalPsesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
                Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
                var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", result.PowerShellVersion ?? "7.x",
                    ParseMajorMinor(result.PowerShellVersion), Environment.Is64BitProcess ? "x64" : "x86", pwshPath,
                    "A02 acceptance runner", true, false, true, true);
                service.RawOutputReceived += (_, text) => outputLog.Append(text);
                var startupTimer = Stopwatch.StartNew();
                try
                {
                    var startupDirectory = Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_STARTUP_CWD") ?? Path.GetTempPath();
                    await service.StartSessionAsync(runtime, record => visibleOutputLog.Append(record.Text), startupDirectory, budget.Token)
                        .WaitAsync(StartupTimeout, budget.Token);
                    sessionStarted = true;
                    ownedSessionProcessId = service.ProcessId;
                    ownedSessionProcessStartUtc = service.ProcessStartTimeUtc;
                    result.ProcessId = service.ProcessId;
                    result.SessionGeneration = service.SessionGeneration;
                    result.Tests.Add(Timed(Pass("A01", "PSES-owned PowerShell session startup", "Production integration",
                        $"Session running; PID={service.ProcessId}; generation={service.SessionGeneration}."), startupTimer));

                    if (bridgeProtocolMode)
                    {
                        await RunBridgeCancellationBoundaryAsync(result, service, outputLog, budget.Token);
                        await RunCoordinatorRunSelectionBoundaryAsync(result, service, outputLog, budget.Token);
                        var idleShutdownTimer = Stopwatch.StartNew();
                        try
                        {
                            var idleShutdown = await service.ShutdownAsync(budget.Token).WaitAsync(TimeSpan.FromSeconds(15), budget.Token);
                            result.Tests.Add(Timed(new TestResult("T01", "Authenticated idle bridge shutdown", "Production integration",
                                "An authenticated, silent bridge client does not block bounded PSES shutdown.",
                                $"shutdown={idleShutdown}; processExited={!service.IsSessionRunning}",
                                idleShutdown && !service.IsSessionRunning ? "PASS" : "FAIL",
                                idleShutdown && !service.IsSessionRunning ? null : "Idle bridge shutdown did not settle cleanly."), idleShutdownTimer));
                        }
                        catch (Exception ex) { result.Tests.Add(Timed(Fail("T01", "Authenticated idle bridge shutdown", ex), idleShutdownTimer)); }
                        await RunFocusedBridgeShutdownProbeAsync(result, runtime, outputLog, budget.Token);
                    }
                    else if (bridgeT02OnlyMode)
                    {
                        await RunBridgeCancellationBoundaryAsync(result, service, outputLog, budget.Token);
                    }
                    else if (bridgeR1Mode)
                    {
                        await RunBridgeR1CasesAsync(result, service, outputLog, budget.Token, redactions);
                    }
                    else if (productionMigrationMode)
                        await RunProductionMigrationCasesAsync(result, service, outputLog, budget.Token, redactions);
                    else if (string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_FAST01"), "1", StringComparison.Ordinal))
                        await RunFast01CasesAsync(result, service, outputLog, visibleOutputLog, budget.Token);
                    else
                        await RunCoreCasesAsync(result, service, outputLog, visibleOutputLog, budget.Token, redactions);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(evidence, "startup-exception.txt"), ex.ToString());
                    result.Tests.Add(Timed(new TestResult("A01", "PSES-owned PowerShell session startup", "Production integration",
                        "Required PSES session starts within 45 seconds", "Startup failed", HarnessContractChecks.ClassifyStartupException(ex), ShortException(ex)), startupTimer));
                    AddNotRunAfterFatal(result, "Required PSES startup failed; dependent production tests could not execute.");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", originalPsesRoot);
                }
            }
        }
        catch (Exception ex)
        {
            result.RunnerError = ShortException(ex);
            result.Tests.Add(new TestResult("INFRA-001", "Unexpected runner infrastructure exception", "Harness infrastructure",
                "Unexpected exceptions produce an unsuccessful run", "Runner exception", "FAIL", result.RunnerError));
        }
        finally
        {
            var cleanupTimer = Stopwatch.StartNew();
            var sessionDetailsPath = service.SessionDetailsPath;
            var ownedProcessId = ownedSessionProcessId ?? service.ProcessId;
            if (sessionStarted || service.IsSessionRunning || service.SessionDetailsPath is not null)
            {
                try
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var stopped = await service.ShutdownAsync(shutdownTimeout.Token).WaitAsync(TimeSpan.FromSeconds(20));
                    result.Cleanup = stopped ? "Production service orderly shutdown confirmed." : "Shutdown returned false.";
                    if (!stopped) await BoundedProcessFallbackAsync(ownedProcessId, ownedSessionProcessStartUtc);
                }
                catch (Exception ex)
                {
                    result.Cleanup = "Orderly shutdown error: " + ShortException(ex);
                    try { await BoundedProcessFallbackAsync(ownedProcessId, ownedSessionProcessStartUtc); result.Cleanup += " Bounded owned-process fallback attempted."; }
                    catch (Exception fallbackEx) { result.Cleanup += " Fallback error: " + ShortException(fallbackEx); }
                }
            }
            if (ownedProcessId is int pid && !await IsProcessExitedAsync(pid, ownedSessionProcessStartUtc))
                result.Cleanup += $" Owned PowerShell process {pid} is still running after shutdown/fallback.";
            service.Dispose();
            if (sessionDetailsPath is not null)
            {
                var sessionRoot = Path.GetDirectoryName(sessionDetailsPath);
                if (sessionRoot is not null && Guid.TryParse(Path.GetFileName(sessionRoot), out _) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(sessionRoot)), "PSES", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Directory.Exists(sessionRoot)) Directory.Delete(sessionRoot, recursive: true);
                        if (Directory.Exists(sessionRoot)) throw new IOException("Runner-owned session directory still exists after removal attempt.");
                        result.Cleanup += " Unique runner PSES session directory removal verified.";
                    }
                    catch (Exception ex) { result.Cleanup += " Session directory cleanup error: " + ShortException(ex); }
                }
            }
            if (service.SessionDetailsPath is null && !sessionStarted) result.Cleanup += " No runner-owned session directory was created.";
            var cleanupDetails = result.Cleanup ?? "Cleanup did not report an outcome.";
            result.Cleanup = cleanupDetails;
            var cleanupFailed = cleanupDetails.Contains("error:", StringComparison.OrdinalIgnoreCase) || cleanupDetails.Contains("returned false", StringComparison.OrdinalIgnoreCase) ||
                cleanupDetails.Contains("still exists", StringComparison.OrdinalIgnoreCase) || cleanupDetails.Contains("still running", StringComparison.OrdinalIgnoreCase);
            var cleanupStatus = HarnessContractChecks.ClassifyCleanup(!cleanupFailed, cleanupDetails);
            if (cleanupStatus.StartsWith("FAIL", StringComparison.Ordinal))
                result.Tests.Add(Timed(new TestResult("CLEANUP", "Runner-owned process and session-directory cleanup", "Harness infrastructure",
                    "Orderly shutdown and owned-directory removal are confirmed", cleanupDetails, "FAIL", cleanupDetails), cleanupTimer));
            else
                result.Tests.Add(Timed(new TestResult("CLEANUP", "Runner-owned process and session-directory cleanup", "Harness infrastructure",
                    "Orderly shutdown and owned-directory removal are confirmed", cleanupDetails, "PASS", null), cleanupTimer));
        }

        timer.Stop();
        result.DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 3);
        result.CompletedAt = DateTimeOffset.Now;
        result.EvidenceDirectory = evidence;
        var output = outputLog.SnapshotAfter(0);
        var redactedJson = Redact(JsonSerializer.Serialize(result, JsonOptions), redactions);
        var redactedTerminal = Redact(output.Text, redactions);
        var redactedMarkdown = Redact(RenderMarkdown(result), redactions);
        var secretRedacted = redactions.All(secret => !redactedJson.Contains(secret, StringComparison.Ordinal) &&
            !redactedTerminal.Contains(secret, StringComparison.Ordinal) && !redactedMarkdown.Contains(secret, StringComparison.Ordinal));
        result.Tests.Add(new TestResult("A08-REDACTION", "Synthetic secure-input artifact redaction", "Synthetic/contract test",
            "Synthetic secret is absent from serialized JSON, Markdown, and terminal capture after redaction.",
            secretRedacted ? "Secret absent from all checked artifact payloads." : "Secret detected in a checked artifact payload.",
            secretRedacted ? "PASS" : "FAIL", secretRedacted ? null : "Artifact redaction verification failed."));
        redactedJson = Redact(JsonSerializer.Serialize(result, JsonOptions), redactions);
        redactedMarkdown = Redact(RenderMarkdown(result), redactions);
        await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), redactedJson);
        await File.WriteAllTextAsync(Path.Combine(evidence, "runtime-output.log"), redactedTerminal);
        await File.WriteAllTextAsync(Path.Combine(evidence, "runner-diagnostics.log"),
            $"captureLastSequence={output.LastSequence}{Environment.NewLine}captureTruncated={output.Truncated}{Environment.NewLine}" + result.Cleanup + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(evidence, "results.md"), redactedMarkdown);
        PrintSummary(result);
        var productionCasesExecuted = result.Tests.Any(t => t.Category == "Production integration" && t.Status is "PASS" or "FAIL");
        return HarnessContractChecks.ShouldExitNonZero(result.RunnerError is not null, result.Tests.Any(t => t.Status == "FAIL"), productionCasesExecuted) ? 1 : 0;
    }

    private static async Task RunProductionMigrationCasesAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture output, CancellationToken budget, List<string> redactions)
    {
        var gate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var backend = new AuthoritativeSessionStructuredExecutionBackend(
            new AuthoritativeSessionStructuredExecutionAdapter(service, gate));
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        var selectionGate = new ExperimentalRunSelectionBridgeFeatureGate(true);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, backend }, gate, false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier(), selectionGate);
        EditorExecutionRequest Selection(string source) => new(Guid.NewGuid(), service.SessionGeneration,
            EditorExecutionMode.RunSelection, "A02-PROD-01 routing probe", source,
            ExecuteInCurrentScope: true, CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresCurrentScope: true));

        var ordinary = coordinator.Route(Selection("$A02ProductionProbe='OK'; Write-Output $A02ProductionProbe"));
        var interactive = coordinator.Route(Selection("Read-Host 'A02 prompt'"));
        var unknown = coordinator.Route(Selection("Start-Sleep -Seconds 5"));
        var loop = coordinator.Route(Selection("while ($true) { }"));
        var memberInput = coordinator.Route(Selection("[Console]::ReadLine()"));
        var file = coordinator.Route(new EditorExecutionRequest(Guid.NewGuid(), service.SessionGeneration,
            EditorExecutionMode.ScriptCall, "A02 Run File routing probe", "Write-Output 'file'",
            ExecuteInCurrentScope: false, CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresScriptCallIsolation: true)));
        using var rollback = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, backend }, gate, false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier(),
            new ExperimentalRunSelectionBridgeFeatureGate(false));
        var reverted = rollback.Route(Selection("Write-Output 'rollback'"));
        void Check(string id, string name, bool passed, string actual) => result.Tests.Add(new TestResult(
            id, name, "Source/contract", name, actual, passed ? "PASS" : "FAIL", passed ? null : actual));
        Check("R01-ROUTE", "All Run Selection syntax classes route to the PSES scheduler", ordinary.Accepted &&
            ordinary.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
            ordinary.Classification == ExecutionRequestClassification.KnownInteractive && ordinary.SelectionReason == "PsesSchedulerAdmission" &&
            interactive.SelectedBackend == ordinary.SelectedBackend && unknown.SelectedBackend == ordinary.SelectedBackend &&
            loop.SelectedBackend == ordinary.SelectedBackend && memberInput.SelectedBackend == ordinary.SelectedBackend,
            $"ordinary={ordinary.SelectedBackend}/{ordinary.SelectionReason}; interactive={interactive.SelectedBackend}; dynamic={unknown.SelectedBackend}; loop={loop.SelectedBackend}; member={memberInput.SelectedBackend}");
        Check("R03", "Interactive Run Selection uses PSES host input", interactive.Accepted &&
            interactive.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
            interactive.SelectionReason == "PsesSchedulerAdmission",
            $"backend={interactive.SelectedBackend}; classification={interactive.Classification}");
        Check("R04", "Classifier opt-out cannot reactivate the legacy selection launcher", reverted.Accepted &&
            reverted.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
            reverted.SelectionReason == "PsesSchedulerAdmission",
            $"backend={reverted.SelectedBackend}; reason={reverted.SelectionReason}");
        Check("R06", "Run File routes through the PSES bridge backend", file.Accepted &&
            file.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId && file.SelectionReason == "PsesSchedulerAdmission",
            $"backend={file.SelectedBackend}; mode=ScriptCall; reason={file.SelectionReason}");
        if (selectionGate.IsEnabled)
            await RunCoordinatorRunSelectionBoundaryAsync(result, service, output, budget);
        await RunBridgeReadHostAndOutputOwnershipAsync(result, service, output, budget, redactions);
        await RunBridgeNativeChildParityAsync(result, service, output, budget);
        await RunBridgeCompetingConsoleAdmissionAsync(result, service, output, budget);
        await RunRunFileIdentityCasesAsync(result, service, output, budget);
    }

    private static async Task RunBridgeR1CasesAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture output, CancellationToken budget, List<string> redactions)
    {
        await RunBridgeLanguageServiceCoexistenceAsync(result, service, output, budget);
        await RunBridgeReadHostAndOutputOwnershipAsync(result, service, output, budget, redactions);

        var timer = Stopwatch.StartNew();
        var processId = service.ProcessId;
        var generation = service.SessionGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(budget);
        var gate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, gate);
        var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, gate, false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier(),
            new ExperimentalRunSelectionBridgeFeatureGate(true));
        var longScript = string.Join(";", Enumerable.Repeat("Get-Process | Measure-Object", 120));
        var request = new EditorExecutionRequest(Guid.NewGuid(), generation, EditorExecutionMode.RunSelection,
            "A02-ARCH-06B-R1 cancellation probe", longScript, ExecuteInCurrentScope: true,
            CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresCurrentScope: true));
        try
        {
            var routing = coordinator.Route(request);
            var execution = coordinator.ExecuteAsync(request, cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(500), budget);
            cancellation.Cancel();
            var resultOfExecution = await execution.WaitAsync(TimeSpan.FromSeconds(20), budget);
            var verificationMarker = "BRIDGE_CANCEL_SESSION_REUSED";
            var verificationBoundary = output.Boundary;
            await RunConsoleAndWaitAsync(service, output,
                "Write-Output ('BRIDGE_CANCEL_'+'SESSION_REUSED'); Write-Output ('BRIDGE_CANCEL_HISTORY=' + @((Get-History | Where-Object { $_.CommandLine -like '*Get-Process | Measure-Object*' }).Count))",
                verificationMarker, generation, budget);
            var verification = await WaitForCaptureAsync(output, verificationBoundary,
                text => text.Contains("BRIDGE_CANCEL_HISTORY=0", StringComparison.Ordinal), InteractiveTimeout, budget);
            var passed = routing.Accepted && routing.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
                resultOfExecution.Status == EditorExecutionStatus.Cancelled && !resultOfExecution.RecoveryRequired &&
                service.IsSessionRunning && service.IsInputWritable && service.ProcessId == processId &&
                service.SessionGeneration == generation && verification.Text.Contains("BRIDGE_CANCEL_HISTORY=0", StringComparison.Ordinal) &&
                verification.Text.Contains(verificationMarker, StringComparison.Ordinal);
            result.Tests.Add(Timed(new TestResult("R05", "Bridge cancellation settlement and session reuse", "Production integration",
                "Coordinator cancellation is acknowledged and settled, the cancelled script is absent from history, and the same session accepts a console command.",
                $"route={routing.SelectedBackend}; status={resultOfExecution.Status}; error={resultOfExecution.ErrorMessage}; recoveryRequired={resultOfExecution.RecoveryRequired}; process={processId}->{service.ProcessId}; generation={generation}->{service.SessionGeneration}; consoleReusable={verification.Text.Contains(verificationMarker, StringComparison.Ordinal)}; cancelHistoryClean={verification.Text.Contains("BRIDGE_CANCEL_HISTORY=0", StringComparison.Ordinal)}",
                passed ? "PASS" : "FAIL", passed ? null : "Cancellation settlement or same-session/history verification failed.", Math.Round(timer.Elapsed.TotalSeconds, 3)), timer));
        }
        catch (Exception ex)
        {
            cancellation.Cancel();
            result.Tests.Add(Timed(Fail("R05", "Bridge cancellation settlement and session reuse", ex), timer));
        }

        await RunBridgeShutdownWhileExecutingAsync(result, service, output, budget);
        if (!service.IsSessionRunning) return;
    }

    private static async Task RunBridgeCancellationBoundaryAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, CancellationToken budget)
    {
        var timer = Stopwatch.StartNew();
        var processId = service.ProcessId;
        var generation = service.SessionGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(budget);
        var gate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, gate);
        var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, gate, false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier(),
            new ExperimentalRunSelectionBridgeFeatureGate(true));
        var requestId = Guid.NewGuid();
        var raceScript = "Write-Output 'BRIDGE_BOUNDARY_START';" + string.Join(";", Enumerable.Repeat("Get-Process | Measure-Object", 120)) +
            "; Write-Output 'BRIDGE_BOUNDARY_FINAL'";
        var request = new EditorExecutionRequest(requestId, generation, EditorExecutionMode.RunSelection,
            "A02-ARCH-06B-R1.1 completion-boundary cancellation",
            raceScript,
            ExecuteInCurrentScope: true, CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresCurrentScope: true));
        var outputBoundary = output.Boundary;
        try
        {
            var route = coordinator.Route(request);
            var executionTask = coordinator.ExecuteAsync(request, cancellation.Token);
            var started = await WaitForCaptureAsync(output, outputBoundary,
                text => text.Contains("BRIDGE_BOUNDARY_START", StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(started.Text.Contains("BRIDGE_BOUNDARY_START", StringComparison.Ordinal),
                "The cancellation-race request did not reach its controlled start point.");
            await Task.Delay(TimeSpan.FromMilliseconds(500), budget);
            cancellation.Cancel();
            var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(15), budget);
            var followupId = Guid.NewGuid();
            var followup = new EditorExecutionRequest(followupId, generation, EditorExecutionMode.RunSelection,
                "A02-ARCH-06B-R1.1 same-pipe follow-up", "Write-Output 'BRIDGE_BOUNDARY_FOLLOWUP_OK'",
                ExecuteInCurrentScope: true, CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresCurrentScope: true));
            var followupResult = await coordinator.ExecuteAsync(followup, budget).WaitAsync(TimeSpan.FromSeconds(15), budget);
            var followupCapture = await WaitForCaptureAsync(output, outputBoundary,
                text => text.Contains("BRIDGE_BOUNDARY_FOLLOWUP_OK", StringComparison.Ordinal), InteractiveTimeout, budget);
            var followupObserved = followupCapture.Text.Contains("BRIDGE_BOUNDARY_FOLLOWUP_OK", StringComparison.Ordinal);
            var passed = route.Accepted && route.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
                execution.RequestId == requestId && execution.SessionGeneration == generation &&
                (execution.Status is EditorExecutionStatus.Completed or EditorExecutionStatus.Cancelled) &&
                !execution.RecoveryRequired &&
                followupResult.Status == EditorExecutionStatus.Completed && followupResult.RequestId == followupId &&
                followupResult.SessionGeneration == generation && followupObserved && !followupResult.RecoveryRequired &&
                service.IsSessionRunning && service.ProcessId == processId && service.SessionGeneration == generation;
            result.Tests.Add(Timed(new TestResult("T02", "Cancellation/completion boundary and pipe reuse", "Production integration",
                "Cancellation near normal completion leaves the protocol aligned for a second request in the same session.",
                $"route={route.SelectedBackend}/{route.Classification}; cancellationIssuedAfterStart=True; first={execution.Status}; firstIdentity={execution.RequestId == requestId && execution.SessionGeneration == generation}; firstRecovery={execution.RecoveryRequired}; followup={followupResult.Status}; followupIdentity={followupResult.RequestId == followupId && followupResult.SessionGeneration == generation}; marker={followupObserved}; sameProcess={service.ProcessId == processId}; sameGeneration={service.SessionGeneration == generation}",
                passed ? "PASS" : "FAIL", passed ? null : "The completion-boundary cancellation contaminated or faulted the next request."), timer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("T02", "Cancellation/completion boundary and pipe reuse", ex), timer)); }
    }

    private static async Task RunFocusedBridgeShutdownProbeAsync(RunResult result, PowerShellRuntimeInfo runtime,
        SequencedOutputCapture output, CancellationToken budget)
    {
        var service = new PsesOwnedInteractiveSessionService(
            new ExperimentalRunFileBridgeFeatureGate(false),
            new ExperimentalRunSelectionBridgeFeatureGate(true),
            new PsesEditorBridgeFeatureGate(true));
        int? processId = null;
        string? sessionDetailsPath = null;
        try
        {
            service.RawOutputReceived += (_, text) => output.Append(text);
            await service.StartSessionAsync(runtime, _ => { }, Path.GetTempPath(), budget)
                .WaitAsync(StartupTimeout, budget);
            processId = service.ProcessId;
            await RunBridgeShutdownWhileExecutingAsync(result, service, output, budget, "T03");
        }
        catch (Exception ex)
        {
            result.Tests.Add(Fail("T03", "Concurrent shutdown and request settlement", ex));
        }
        finally
        {
            sessionDetailsPath = service.SessionDetailsPath;
            try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
            service.Dispose();
            if (sessionDetailsPath is not null)
            {
                var sessionRoot = Path.GetDirectoryName(sessionDetailsPath);
                if (sessionRoot is not null && Directory.Exists(sessionRoot))
                {
                    try { Directory.Delete(sessionRoot, recursive: true); } catch { }
                }
            }
            if (processId is int pid && !await IsProcessExitedAsync(pid, null))
                result.Tests.Add(new TestResult("T03-CLEANUP", "Concurrent shutdown probe process cleanup", "Harness infrastructure",
                    "The bounded probe-owned process exits after shutdown.", $"process={pid} remains running", "FAIL", "Probe process did not exit."));
        }
    }

    private static async Task RunBridgeLanguageServiceCoexistenceAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, CancellationToken budget)
    {
        var timer = Stopwatch.StartNew();
        const string uri = "untitled:ps7scriptdesk/bridge-r1-lsp.ps1";
        const string text = "Get-Pro";
        try
        {
            var before = await service.GetLiveCompletionsAsync(uri, text, text.Length, budget)
                .WaitAsync(TimeSpan.FromSeconds(15), budget);
            await RunCoordinatorRunSelectionBoundaryAsync(result, service, output, budget);
            var after = await service.GetLiveCompletionsAsync(uri, text, text.Length, budget)
                .WaitAsync(TimeSpan.FromSeconds(15), budget);
            var beforeMatch = before?.Items.Any(item => item.CompletionText.Contains("Get-Process", StringComparison.OrdinalIgnoreCase)) == true;
            var afterMatch = after?.Items.Any(item => item.CompletionText.Contains("Get-Process", StringComparison.OrdinalIgnoreCase)) == true;
            var passed = beforeMatch && afterMatch && service.IsSessionRunning;
            result.Tests.Add(Timed(new TestResult("R10", "LSP completion before and after bridge execution", "Production integration",
                "A focused live LSP completion request remains available around bridge-backed Run Selection without corrupting the shared PSES session.",
                $"beforeCompletion={beforeMatch}; afterCompletion={afterMatch}; sameSessionRunning={service.IsSessionRunning}",
                passed ? "PASS" : "FAIL", passed ? null : "LSP completion was not available on both sides of bridge execution.", Math.Round(timer.Elapsed.TotalSeconds, 3)), timer));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("R10", "LSP completion before and after bridge execution", ex), timer));
        }
    }

    private static async Task RunBridgeCompetingConsoleAdmissionAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, CancellationToken budget)
    {
        var timer = Stopwatch.StartNew();
        var generation = service.SessionGeneration;
        var processId = service.ProcessId;
        var boundary = output.Boundary;
        try
        {
            var submitted = await service.ExecuteConsoleCommandAsync(
                "Start-Sleep -Seconds 2; Write-Output ('A02_R1_'+'CONSOLE_BUSY_DONE')",
                _ => { }, Guid.NewGuid(), budget).WaitAsync(TimeSpan.FromSeconds(10), budget);

            var featureGate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
            var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, featureGate);
            var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
            var legacy = new LegacyLiveConsoleExecutionBackend(service);
            using var coordinator = new EditorExecutionCoordinator(
                new IEditorExecutionBackend[] { legacy, structured }, featureGate, false,
                ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier(),
                new ExperimentalRunSelectionBridgeFeatureGate(true));
            var request = new EditorExecutionRequest(Guid.NewGuid(), generation,
                EditorExecutionMode.RunSelection, "A02-ARCH-06B-R1 console-busy probe",
                "Write-Output ('A02_R1_'+'BRIDGE_AFTER_CONSOLE')", ExecuteInCurrentScope: true,
                CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresCurrentScope: true));
            var route = coordinator.Route(request);
            var competingTask = coordinator.ExecuteAsync(request, budget);
            var consoleCapture = await WaitForCaptureAsync(output, boundary,
                text => text.Contains("A02_R1_CONSOLE_BUSY_DONE", StringComparison.Ordinal), TimeSpan.FromSeconds(10), budget);
            var competing = await competingTask.WaitAsync(TimeSpan.FromSeconds(10), budget);
            var settled = competing.Status == EditorExecutionStatus.Completed
                ? await WaitForCaptureAsync(output, boundary,
                    text => text.Contains("A02_R1_BRIDGE_AFTER_CONSOLE", StringComparison.Ordinal), TimeSpan.FromSeconds(10), budget)
                : consoleCapture;
            var consoleDone = settled.Text.IndexOf("A02_R1_CONSOLE_BUSY_DONE", StringComparison.Ordinal);
            var bridgeDone = settled.Text.IndexOf("A02_R1_BRIDGE_AFTER_CONSOLE", StringComparison.Ordinal);
            var safelyRejected = competing.Status == EditorExecutionStatus.Rejected && bridgeDone < 0 && consoleDone >= 0;
            var queuedAfterConsole = competing.Status == EditorExecutionStatus.Completed && consoleDone >= 0 && bridgeDone > consoleDone;
            var passed = submitted.SessionGeneration == generation && route.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
                (safelyRejected || queuedAfterConsole) &&
                service.IsInputWritable && service.ProcessId == processId && service.SessionGeneration == generation;
            var promptDeadline = Stopwatch.StartNew();
            while (service.IsCommandInProgress && promptDeadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(TimeSpan.FromMilliseconds(50), budget);
            passed &= !service.IsCommandInProgress;
            result.Tests.Add(Timed(new TestResult("R07", "Bridge admission while native console is busy", "Production integration",
                "An active foreground console command is not preempted; a competing bridge request is safely rejected or runs after console completion.",
                $"consoleSubmitted={submitted.SessionGeneration == generation}; route={route.SelectedBackend}; bridgeStatus={competing.Status}; consoleMarkerIndex={consoleDone}; bridgeMarkerIndex={bridgeDone}; sameSession={service.ProcessId == processId && service.SessionGeneration == generation}",
                passed ? "PASS" : "FAIL", passed ? null : "The bridge request preempted, overlapped, or failed the established console ownership contract.", Math.Round(timer.Elapsed.TotalSeconds, 3)), timer));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("R07", "Bridge admission while native console is busy", ex), timer));
        }
    }

    private static async Task RunBridgeShutdownWhileExecutingAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, CancellationToken budget,
        string testId = "R08")
    {
        var timer = Stopwatch.StartNew();
        var generation = service.SessionGeneration;
        var featureGate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, featureGate);
        var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, featureGate, false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier(),
            new ExperimentalRunSelectionBridgeFeatureGate(true));
        var boundary = output.Boundary;
        var workload = "Write-Output ('BRIDGE_SHUTDOWN_'+'STARTED');" +
            string.Join(";", Enumerable.Repeat("Get-Process | Measure-Object", 300));
        var request = new EditorExecutionRequest(Guid.NewGuid(), generation,
            EditorExecutionMode.RunSelection, "A02-ARCH-06B-R1 shutdown probe", workload,
            ExecuteInCurrentScope: true,
            CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresCurrentScope: true));
        try
        {
            var route = coordinator.Route(request);
            if (!route.Accepted || route.SelectedBackend != EditorExecutionCoordinator.StructuredBackendId)
                throw new InvalidOperationException($"Shutdown probe did not route through the authenticated bridge: {route.SelectedBackend}/{route.Classification}.");
            var execution = coordinator.ExecuteAsync(request, CancellationToken.None);
            var started = await WaitForCaptureAsync(output, boundary,
                text => text.Contains("BRIDGE_SHUTDOWN_STARTED", StringComparison.Ordinal), InteractiveTimeout, budget);
            if (!started.Text.Contains("BRIDGE_SHUTDOWN_STARTED", StringComparison.Ordinal))
                throw new InvalidOperationException("The active bridge shutdown script did not produce its startup marker.");
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var shutdown = await service.ShutdownAsync(shutdownTimeout.Token).WaitAsync(TimeSpan.FromSeconds(25), budget);
            var settled = await execution.WaitAsync(TimeSpan.FromSeconds(5), budget);
            var settlementStatusAccepted = settled.Status is EditorExecutionStatus.Cancelled or EditorExecutionStatus.Unknown or EditorExecutionStatus.Failed;
            var passed = shutdown && settlementStatusAccepted &&
                settled.Status != EditorExecutionStatus.Completed &&
                (settled.Status != EditorExecutionStatus.Unknown || settled.RecoveryRequired) &&
                !service.IsSessionRunning && service.SessionGeneration == generation;
            result.Tests.Add(Timed(new TestResult(testId, "Shutdown with active bridge execution", "Production integration",
                "Shutdown requests cancellation, settles or marks the request uncertain, releases resources safely, and never reports premature success.",
                $"route={route.SelectedBackend}; shutdown={shutdown}; execution={settled.Status}; recoveryRequired={settled.RecoveryRequired}; generationStable={service.SessionGeneration == generation}; sessionStopped={!service.IsSessionRunning}",
                passed ? "PASS" : "FAIL", passed ? null : "Shutdown or active-request settlement did not meet the bounded lifecycle contract.", Math.Round(timer.Elapsed.TotalSeconds, 3)), timer));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail(testId, "Shutdown with active bridge execution", ex), timer));
        }
    }

    private static async Task RunBridgeReadHostAndOutputOwnershipAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, CancellationToken budget,
        List<string> redactions)
    {
        var timer = Stopwatch.StartNew();
        var generation = service.SessionGeneration;
        var processId = service.ProcessId;
        var boundary = output.Boundary;
        var rawCallbackCount = 0;
        var prompt = "A02_R1_READHOST_PROMPT";
        var prompt2 = "A02_R1_READHOST_PROMPT_2";
        var script = "$value=Read-Host -Prompt 'A02_R1_READHOST_PROMPT'; $value2=Read-Host -Prompt 'A02_R1_READHOST_PROMPT_2'; Write-Host ('A02_R1_'+'VT_OUTPUT_ITEM'); Write-Output ('A02_R1_'+'READHOST_RESULT=' + $value + '|' + $value2)";
        try
        {
            var pending = service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02-ARCH-06B-R1 bridge Read-Host", script, true,
                _ => Interlocked.Increment(ref rawCallbackCount), budget,
                mode: EditorExecutionMode.RunSelection, directRunSelectionApproved: true);
            var promptSeen = await WaitForCaptureAsync(output, boundary,
                text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(!promptSeen.Truncated && promptSeen.Text.Contains(prompt, StringComparison.Ordinal),
                "The authenticated bridge did not render the Read-Host prompt in the native terminal.");
            await service.WriteRawInputAsync("BRIDGE_INPUT_OK\r", budget).WaitAsync(InteractiveTimeout, budget);
            var secondPrompt = await WaitForCaptureAsync(output, boundary,
                text => text.Contains(prompt2, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(secondPrompt.Text.Contains(prompt2, StringComparison.Ordinal),
                "The sequential Read-Host prompt was not rendered by the native console.");
            await service.WriteRawInputAsync("BRIDGE_INPUT_SECOND\r", budget).WaitAsync(InteractiveTimeout, budget);
            var execution = await pending.WaitAsync(CaseTimeout, budget);
            var rendered = await WaitForCaptureAsync(output, boundary,
                text => text.Contains("A02_R1_READHOST_RESULT=BRIDGE_INPUT_OK|BRIDGE_INPUT_SECOND", StringComparison.Ordinal), InteractiveTimeout, budget);
            var capture = rendered;
            var outputCount = capture.Text.Split("A02_R1_VT_OUTPUT_ITEM", StringSplitOptions.None).Length - 1;
            var resultCount = capture.Text.Split("A02_R1_READHOST_RESULT=BRIDGE_INPUT_OK|BRIDGE_INPUT_SECOND", StringSplitOptions.None).Length - 1;
            var passed = execution.Status == EditorExecutionStatus.Completed && execution.CompletionObserved &&
                !execution.RecoveryRequired && outputCount == 1 && resultCount == 1 && rawCallbackCount == 0 &&
                !capture.Text.Contains("$value=Read-Host", StringComparison.Ordinal) &&
                !capture.Text.Contains("Write-Host ('A02_R1_", StringComparison.Ordinal) &&
                service.ProcessId == processId && service.SessionGeneration == generation;
            result.Tests.Add(Timed(new TestResult("R04/R06", "Bridge Read-Host, native input, and single-owner output", "Production integration",
                "Read-Host runs through the bridge, ConPTY input completes it, terminal owns VT rendering once, and raw bytes are not reported as editor stdout.",
                $"status={execution.Status}; promptVisible={capture.Text.Contains(prompt, StringComparison.Ordinal) && capture.Text.Contains(prompt2, StringComparison.Ordinal)}; vtItemCount={outputCount}; resultCount={resultCount}; editorStdoutCallbackCount={rawCallbackCount}; sameSession={service.ProcessId == processId && service.SessionGeneration == generation}",
                passed ? "PASS" : "FAIL", passed ? null : "Interactive completion, output ownership, or session continuity check failed.", Math.Round(timer.Elapsed.TotalSeconds, 3)), timer));

            const string secureValue = "A02_SECURE_INPUT_SYNTHETIC_SENTINEL";
            redactions.Add(secureValue);
            const string securePrompt = "A02_R1_SECURE_READHOST_PROMPT";
            const string secureMarker = "A02_R1_SECURE_LENGTH=";
            var secureBoundary = output.Boundary;
            var secureScript = "$secret=Read-Host -AsSecureString -Prompt '" + securePrompt + "'; if ($secret -is [System.Security.SecureString]) { Write-Output ('" + secureMarker + "' + $secret.Length) }";
            var secureTask = service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02-ARCH-06C bridge secure Read-Host", secureScript, true, _ => { }, budget,
                mode: EditorExecutionMode.RunSelection, directRunSelectionApproved: true);
            var securePromptSeen = await WaitForCaptureAsync(output, secureBoundary,
                text => text.Contains(securePrompt, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(securePromptSeen.Text.Contains(securePrompt, StringComparison.Ordinal), "Secure Read-Host prompt was not rendered.");
            await service.WriteRawInputAsync(secureValue + "\r", budget).WaitAsync(InteractiveTimeout, budget);
            var secureExecution = await secureTask.WaitAsync(CaseTimeout, budget);
            var secureCapture = await WaitForCaptureAsync(output, secureBoundary,
                text => text.Contains(secureMarker + secureValue.Length, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(secureExecution.Status == EditorExecutionStatus.Completed &&
                    secureCapture.Text.Contains(secureMarker + secureValue.Length, StringComparison.Ordinal) &&
                    !secureCapture.Text.Contains(secureValue, StringComparison.Ordinal),
                "Secure Read-Host failed, exposed the submitted value, or did not preserve SecureString.");
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("R04/R06", "Bridge Read-Host, native input, and single-owner output", ex), timer));
        }
    }

    private static async Task RunBridgeNativeChildParityAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, CancellationToken budget)
    {
        var timer = Stopwatch.StartNew();
        var generation = service.SessionGeneration;
        var boundary = output.Boundary;
        const string prompt = "A02_NATIVE_CHILD_STDIN_PROMPT:";
        const string input = "NATIVE_CHILD_INPUT_OK";
        const string stdoutMarker = "A02_NATIVE_CHILD_STDOUT=" + input;
        const string stderrMarker = "A02_NATIVE_CHILD_STDERR";
        const string exitMarker = "A02_NATIVE_CHILD_EXIT=7";
        var source = "& $env:ComSpec /v:on /d /c \"set /p childInput=" + prompt +
            " & echo A02_NATIVE_CHILD_STDOUT=!childInput! & echo A02_NATIVE_CHILD_STDERR 1>&2 & exit /b 7\"; " +
            "Write-Output ('A02_NATIVE_CHILD_EXIT=' + $LASTEXITCODE)";
        try
        {
            var execution = service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02-ARCH-06C native child I/O", source, true, _ => { }, budget,
                mode: EditorExecutionMode.RunSelection, directRunSelectionApproved: true);
            var promptCapture = await WaitForCaptureAsync(output, boundary,
                text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(promptCapture.Text.Contains(prompt, StringComparison.Ordinal),
                "Native child stdin prompt did not arrive through the shared ConPTY.");
            await service.WriteRawInputAsync(input + "\r", budget).WaitAsync(InteractiveTimeout, budget);
            var completed = await execution.WaitAsync(CaseTimeout, budget);
            var capture = await WaitForCaptureAsync(output, boundary,
                text => text.Contains(exitMarker, StringComparison.Ordinal), InteractiveTimeout, budget);
            var passed = completed.Status == EditorExecutionStatus.Completed &&
                capture.Text.Contains(stdoutMarker, StringComparison.Ordinal) &&
                capture.Text.Contains(stderrMarker, StringComparison.Ordinal) &&
                capture.Text.Contains(exitMarker, StringComparison.Ordinal) &&
                service.IsSessionRunning && service.SessionGeneration == generation;
            result.Tests.Add(Timed(new TestResult("E01", "Bridge native child stdin/stdout/stderr and exit code", "Production integration",
                "A native child receives ConPTY input, writes both output streams, and leaves its exit code observable to the PowerShell script.",
                $"status={completed.Status}; prompt={capture.Text.Contains(prompt, StringComparison.Ordinal)}; stdout={capture.Text.Contains(stdoutMarker, StringComparison.Ordinal)}; stderr={capture.Text.Contains(stderrMarker, StringComparison.Ordinal)}; exitCode={capture.Text.Contains(exitMarker, StringComparison.Ordinal)}; sameGeneration={service.SessionGeneration == generation}",
                passed ? "PASS" : "FAIL", passed ? null : "Native child I/O or exit-code ownership failed at the PSES/ConPTY boundary.", Math.Round(timer.Elapsed.TotalSeconds, 3)), timer));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("E01", "Bridge native child stdin/stdout/stderr and exit code", ex), timer));
        }
    }

    private static async Task RunRunFileIdentityCasesAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture rawOutput, CancellationToken budget)
    {
        var evidence = Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_EVIDENCE")
            ?? throw new InvalidOperationException("Run File identity checks require the assigned evidence directory.");
        var runFileDirectory = Path.Combine(evidence, "Run File Paths With Spaces & Apostrophe's");
        var savedPath = Path.Combine(runFileDirectory, "A02-PROD-02A-saved-script.ps1");
        var relativePath = Path.Combine(runFileDirectory, "A02-PROD-02A-neighbor.txt");
        var unsavedRelativePath = Path.Combine(evidence, "A02-PROD-02A-neighbor.txt");
        var pid = service.ProcessId;
        var generation = service.SessionGeneration;
        var overallTimer = Stopwatch.StartNew();
        Directory.CreateDirectory(runFileDirectory);
        service.ResizeConsole(300, 50);
        await File.WriteAllTextAsync(relativePath, "RUNFILE_RELATIVE_OK", budget);
        await File.WriteAllTextAsync(unsavedRelativePath, "RUNFILE_RELATIVE_OK", budget);

        try
        {
            var source = "$A02RunFileLocal='LOCAL_ONLY'; $global:A02RunFileGlobal='GLOBAL_OK'; function A02RunFileLocalFunction { 'LOCAL_FUNCTION_ONLY' }; " +
                "Write-Output ('R01_ROOT=' + $PSScriptRoot); Write-Output ('R01_PATH=' + $PSCommandPath); " +
                "Write-Output ('R01_INVOCATION=' + $MyInvocation.MyCommand.Path); " +
                "Write-Output ('R01_RELATIVE=' + (Get-Content -Raw (Join-Path $PSScriptRoot 'A02-PROD-02A-neighbor.txt')).Trim()); " +
                "Write-Output 'R01_SAVED_DONE'";
            await File.WriteAllTextAsync(savedPath, source, budget);
            var savedTimer = Stopwatch.StartNew();
            var savedBoundary = rawOutput.Boundary;
            var saved = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02-PROD-02A clean saved script", source, false, _ => { }, budget,
                mode: EditorExecutionMode.ScriptCall, savedScriptPath: savedPath).WaitAsync(CaseTimeout, budget);
            var savedOutput = await WaitForCaptureAsync(rawOutput, savedBoundary,
                text => text.Contains("R01_SAVED_DONE", StringComparison.Ordinal), TimeSpan.FromSeconds(5), budget);
            var expectedPath = Path.GetFullPath(savedPath);
            var savedPass = saved.Status == EditorExecutionStatus.Completed && saved.CompletionObserved &&
                savedOutput.Text.Contains("R01_ROOT=" + Path.GetDirectoryName(expectedPath), StringComparison.OrdinalIgnoreCase) &&
                savedOutput.Text.Contains("R01_PATH=" + expectedPath, StringComparison.OrdinalIgnoreCase) &&
                savedOutput.Text.Contains("R01_INVOCATION=" + expectedPath, StringComparison.OrdinalIgnoreCase) &&
                savedOutput.Text.Contains("R01_RELATIVE=RUNFILE_RELATIVE_OK", StringComparison.Ordinal) &&
                service.ProcessId == pid && service.SessionGeneration == generation;
            Require(savedPass, $"Clean saved-file identity or relative-path execution failed. status={saved.Status}; completion={saved.CompletionObserved}; path={expectedPath}; output={savedOutput.Text}; PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            result.Tests.Add(Timed(Pass("R01", "Clean saved Run File identity", "Production integration",
                $"$PSScriptRoot, $PSCommandPath, $MyInvocation path, and relative file resolved from the actual saved script; PID={pid}; generation={generation}."), savedTimer));

            var scopeTimer = Stopwatch.StartNew();
            var scopeBoundary = rawOutput.Boundary;
            await RunConsoleAndWaitAsync(service, rawOutput,
                "if ($null -eq (Get-Variable A02RunFileLocal -ErrorAction SilentlyContinue)) { Write-Output ('R02_'+'VARIABLE_ISOLATED') }; if ($null -eq (Get-Command A02RunFileLocalFunction -ErrorAction SilentlyContinue)) { Write-Output ('R02_'+'FUNCTION_ISOLATED') }; Write-Output ('R02_GLOBAL=' + $global:A02RunFileGlobal); Write-Output ('R02_VERIFY_'+'DONE')",
                "R02_VERIFY_DONE", generation, budget);
            var scopeOutput = rawOutput.SnapshotAfter(scopeBoundary).Text;
            Require(scopeOutput.Contains("R02_VARIABLE_ISOLATED", StringComparison.Ordinal) &&
                    scopeOutput.Contains("R02_FUNCTION_ISOLATED", StringComparison.Ordinal) &&
                    scopeOutput.Contains("R02_GLOBAL=GLOBAL_OK", StringComparison.Ordinal),
                "An ordinary Run File variable or function became visible in the integrated console.");
            result.Tests.Add(Timed(Pass("R02", "Run File script-scope isolation", "Production integration",
                "Ordinary variable and function definitions were isolated; an explicit $global: assignment remained visible."), scopeTimer));

            var dirtyTimer = Stopwatch.StartNew();
            const string staleDiskSource = "Write-Output 'R03_STALE_DISK_CONTENT'";
            const string currentBufferSource = "Write-Output ('R03_BUFFER_CONTENT'); Write-Output ('R03_BUFFER_ROOT=' + $PSScriptRoot); Write-Output ('R03_BUFFER_PATH=' + $PSCommandPath); Write-Output ('R03_BUFFER_INVOCATION=' + $MyInvocation.MyCommand.Path); Write-Output ('R03_RELATIVE=' + (Get-Content -Raw (Join-Path $PSScriptRoot 'A02-PROD-02A-neighbor.txt')).Trim())";
            await File.WriteAllTextAsync(savedPath, staleDiskSource, budget);
            var dirtyBoundary = rawOutput.Boundary;
            var dirty = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02-PROD-02A modified saved buffer", currentBufferSource, false, _ => { }, budget,
                mode: EditorExecutionMode.ScriptCall, savedScriptPath: savedPath).WaitAsync(CaseTimeout, budget);
            var dirtyOutput = await WaitForCaptureAsync(rawOutput, dirtyBoundary,
                text => text.Contains("R03_BUFFER_PATH=", StringComparison.Ordinal), TimeSpan.FromSeconds(5), budget);
            var dirtyActualPath = dirtyOutput.Text.Split("R03_BUFFER_PATH=", StringSplitOptions.None).Last().Split('\r', '\n')[0].Trim();
            Require(dirty.Status == EditorExecutionStatus.Completed && dirty.CompletionObserved &&
                    dirtyOutput.Text.Contains("R03_BUFFER_CONTENT", StringComparison.Ordinal) &&
                    !dirtyOutput.Text.Contains("R03_STALE_DISK_CONTENT", StringComparison.Ordinal) &&
                    dirtyOutput.Text.Contains("R03_BUFFER_ROOT=" + Path.GetDirectoryName(expectedPath), StringComparison.OrdinalIgnoreCase) &&
                    dirtyOutput.Text.Contains("R03_BUFFER_INVOCATION=" + dirtyActualPath, StringComparison.Ordinal) &&
                    string.Equals(Path.GetDirectoryName(dirtyActualPath), Path.GetDirectoryName(expectedPath), StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(dirtyActualPath, expectedPath, StringComparison.OrdinalIgnoreCase) &&
                    dirtyOutput.Text.Contains("R03_RELATIVE=RUNFILE_RELATIVE_OK", StringComparison.Ordinal),
                $"Modified editor content did not execute from a temporary snapshot. status={dirty.Status}; output={dirtyOutput.Text}");
            result.Tests.Add(Timed(Pass("R03", "Modified buffer avoids stale disk script", "Production integration",
                "A dirty saved buffer used an immutable sibling snapshot: current content executed, $PSScriptRoot and relative sibling lookup used the source directory, and PowerShell reported the snapshot's actual path."), dirtyTimer));

            var unsavedTimer = Stopwatch.StartNew();
            const string unsavedSource = "Write-Output ('R04_UNSAVED_CONTENT'); Write-Output ('R04_UNSAVED_ROOT=' + $PSScriptRoot); Write-Output ('R04_UNSAVED_PATH=' + $PSCommandPath); Write-Output ('R04_UNSAVED_INVOCATION=' + $MyInvocation.MyCommand.Path); Write-Output ('R04_RELATIVE=' + (Get-Content -Raw (Join-Path $PSScriptRoot 'A02-PROD-02A-neighbor.txt')).Trim())";
            var unsavedBoundary = rawOutput.Boundary;
            var unsaved = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "Untitled A02-PROD-02A buffer", unsavedSource, false, _ => { }, budget,
                mode: EditorExecutionMode.ScriptCall).WaitAsync(CaseTimeout, budget);
            var unsavedOutput = await WaitForCaptureAsync(rawOutput, unsavedBoundary,
                text => text.Contains("R04_UNSAVED_PATH=", StringComparison.Ordinal), TimeSpan.FromSeconds(5), budget);
            var unsavedActualPath = unsavedOutput.Text.Split("R04_UNSAVED_PATH=", StringSplitOptions.None).Last().Split('\r', '\n')[0].Trim();
            Require(unsaved.Status == EditorExecutionStatus.Completed && unsaved.CompletionObserved &&
                    unsavedOutput.Text.Contains("R04_UNSAVED_CONTENT", StringComparison.Ordinal) &&
                    unsavedOutput.Text.Contains("R04_UNSAVED_ROOT=" + Path.GetFullPath(service.CurrentWorkingDirectory ?? string.Empty), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetDirectoryName(unsavedActualPath), Path.GetFullPath(service.CurrentWorkingDirectory ?? string.Empty), StringComparison.OrdinalIgnoreCase) &&
                    unsavedOutput.Text.Contains("R04_UNSAVED_INVOCATION=" + unsavedActualPath, StringComparison.OrdinalIgnoreCase) &&
                    unsavedOutput.Text.Contains("R04_RELATIVE=RUNFILE_RELATIVE_OK", StringComparison.Ordinal),
                $"Unsaved editor buffer did not execute from the supported temporary snapshot. status={unsaved.Status}; output={unsavedOutput.Text}");
            result.Tests.Add(Timed(Pass("R04", "Unsaved Run File snapshot", "Production integration",
                "Unsaved buffer content executed from an immutable snapshot under the intended console working directory; relative lookup followed that directory."), unsavedTimer));

            var streamsTimer = Stopwatch.StartNew();
            var streamBoundary = rawOutput.Boundary;
            var callbackCount = 0;
            var streams = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02 Run File stream ownership", "Write-Output 'R05_SUCCESS'; Write-Warning 'R05_WARNING'; Write-Verbose 'R05_VERBOSE' -Verbose; Write-Information 'R05_INFORMATION' -InformationAction Continue; Write-Error 'R05_NONTERMINATING' -ErrorAction Continue", false,
                _ => Interlocked.Increment(ref callbackCount), budget,
                mode: EditorExecutionMode.ScriptCall).WaitAsync(CaseTimeout, budget);
            var streamOutput = await WaitForCaptureAsync(rawOutput, streamBoundary,
                text => text.Contains("R05_NONTERMINATING", StringComparison.Ordinal), TimeSpan.FromSeconds(5), budget);
            Require(streams.Status == EditorExecutionStatus.Completed && streams.CompletionObserved && callbackCount == 0 &&
                    streamOutput.Text.Contains("R05_SUCCESS", StringComparison.Ordinal) &&
                    streamOutput.Text.Contains("R05_WARNING", StringComparison.Ordinal) &&
                    streamOutput.Text.Contains("R05_VERBOSE", StringComparison.Ordinal) &&
                    streamOutput.Text.Contains("R05_INFORMATION", StringComparison.Ordinal) &&
                    streamOutput.Text.Contains("R05_NONTERMINATING", StringComparison.Ordinal),
                $"Run File stream ownership or status was incorrect. status={streams.Status}; callbackCount={callbackCount}; output={streamOutput.Text}");
            result.Tests.Add(Timed(Pass("R06", "Run File stream ownership", "Production integration",
                "Success, warning, verbose, information, and nonterminating error records remained under native PSES/ConPTY presentation; no duplicate editor result callback was emitted."), streamsTimer));

            var terminatingTimer = Stopwatch.StartNew();
            var terminatingBoundary = rawOutput.Boundary;
            var terminating = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02 Run File terminating error", "Write-Output 'R07_BEFORE_TERMINATING'; Write-Output ('R07_PATH=' + $PSCommandPath); throw 'R07_TERMINATING_ERROR'", false, _ => { }, budget,
                mode: EditorExecutionMode.ScriptCall).WaitAsync(CaseTimeout, budget);
            var terminatingOutput = await WaitForCaptureAsync(rawOutput, terminatingBoundary,
                text => text.Contains("R07_TERMINATING_ERROR", StringComparison.Ordinal), TimeSpan.FromSeconds(5), budget);
            var terminatingPath = terminatingOutput.Text.Split("R07_PATH=", StringSplitOptions.None).Last().Split('\r', '\n')[0].Trim();
            Require(terminating.Status == EditorExecutionStatus.Failed && terminating.CompletionObserved &&
                    terminatingOutput.Text.Contains("R07_BEFORE_TERMINATING", StringComparison.Ordinal) &&
                    terminatingOutput.Text.Contains("R07_TERMINATING_ERROR", StringComparison.Ordinal) &&
                    terminatingOutput.Text.Contains(terminatingPath, StringComparison.OrdinalIgnoreCase) &&
                    terminatingOutput.Text.Contains("At " + terminatingPath + ":", StringComparison.OrdinalIgnoreCase) &&
                    terminatingPath.Contains(".ps7sd-editor-", StringComparison.OrdinalIgnoreCase),
                $"Run File terminating error was not surfaced as a settled failure. status={terminating.Status}; completion={terminating.CompletionObserved}; output={terminatingOutput.Text}");
            result.Tests.Add(Timed(Pass("R07", "Run File terminating error", "Production integration",
                "A terminating script error appeared in the native terminal and settled as a failed bridge request."), terminatingTimer));

            var cancelTimer = Stopwatch.StartNew();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(budget);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
            var canceled = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02 Run File cancellation", "while ($true) { Start-Sleep -Milliseconds 100 }", false, _ => { }, cancellation.Token,
                mode: EditorExecutionMode.ScriptCall).WaitAsync(CaseTimeout, budget);
            var leftoverSnapshots = Directory.EnumerateFiles(
                service.CurrentWorkingDirectory ?? evidence,
                ".ps7sd-editor-*.ps1",
                SearchOption.TopDirectoryOnly).ToArray();
            Require(canceled.Status == EditorExecutionStatus.Cancelled && canceled.CompletionObserved && !canceled.RecoveryRequired &&
                    service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation && leftoverSnapshots.Length == 0,
                $"Run File cancellation did not settle safely. status={canceled.Status}; completion={canceled.CompletionObserved}; recoveryRequired={canceled.RecoveryRequired};");
            result.Tests.Add(Timed(Pass("R08", "Run File cancellation and cleanup", "Production integration",
                "The bridge confirmed cancellation before release; the same session remained usable for follow-up work."), cancelTimer));

            Require(service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation,
                $"Run File replaced the PSES session: PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            result.Tests.Add(Pass("R05", "Run File shared-session continuity", "Production integration",
                $"Saved, modified-buffer, and unsaved Run File requests completed in the existing PSES process and generation. PID={pid}; generation={generation}."));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("R01-R08", "Run File identity and snapshot integration", ex), overallTimer));
        }
        finally
        {
            try { File.Delete(savedPath); } catch { }
            try { File.Delete(relativePath); } catch { }
            try { File.Delete(unsavedRelativePath); } catch { }
            try { Directory.Delete(runFileDirectory); } catch { }
        }
    }

    private static async Task RunFast01CasesAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture rawOutput, SequencedOutputCapture visibleOutput, CancellationToken budget)
    {
        var pid = service.ProcessId;
        var generation = service.SessionGeneration;
        var gate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, gate);
        var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, gate, shadowMode: false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier());

        async Task<AuthoritativeStructuredExecutionResult> ExecuteDirectSelectionAsync(string source,
            CancellationToken cancellationToken = default) => await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02-ARCH-03 direct PSES qualification", source, true,
                record => visibleOutput.Append(record.Text), cancellationToken,
                mode: EditorExecutionMode.RunSelection, directRunSelectionApproved: true);

        async Task<object> EvaluateDapExpressionAsync(Guid requestId, string expression, CancellationToken cancellationToken)
        {
            var type = typeof(PsesOwnedInteractiveSessionService).Assembly.GetType("PS7ScriptDesk.PowerShell.Services.PsesDapEvaluationClient", throwOnError: true)!;
            var method = type.GetMethod("EvaluateAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?? throw new MissingMethodException(type.FullName, "EvaluateAsync");
            var task = method.Invoke(null, new object?[] { service.DebugServicePipeName!, requestId, generation, expression, cancellationToken, null, null }) as Task
                ?? throw new InvalidOperationException("PSES DAP evaluation did not return a task.");
            await task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return task.GetType().GetProperty("Result")?.GetValue(task)
                ?? throw new InvalidOperationException("PSES DAP evaluation did not return a result.");
        }

        async Task<(EditorExecutionResult Result, string Backend)> SelectAsync(string source, bool interactive = false,
            TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
        {
            var request = new EditorExecutionRequest(Guid.NewGuid(), generation, EditorExecutionMode.RunSelection,
                "A02-ARCH-04 coordinator Run Selection", source, WorkingDirectory: service.CurrentWorkingDirectory,
                ExecuteInCurrentScope: true,
                CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresInteractiveInput: interactive,
                    RequiresCurrentScope: true, RequiresWorkingDirectoryContinuity: true,
                    RequiresTerminalInterrupt: interactive),
                SourceInfo: new EditorExecutionSourceInfo(Guid.NewGuid(), 1, 0, source.Length));
            var route = coordinator.Route(request);
            Require(route.Accepted, "Targeted Run Selection request was rejected: " + route.SelectionReason);
            var effectiveCancellation = cancellationToken ?? budget;
            return (await coordinator.ExecuteAsync(request, effectiveCancellation).WaitAsync(timeout ?? CaseTimeout, budget), route.SelectedBackend);
        }

        // Phase-A architecture qualification: deliberately bypass the coordinator's
        // known-interactive fallback once to establish whether PSES LSP evaluation
        // can accept real ConPTY Read-Host input in the shared production session.
        // This probe does not change application routing.
        var inputProbeTimer = Stopwatch.StartNew();
        using (var inputProbeCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            var prompt = "ARCH03_DIRECT_READHOST_PROMPT";
            var marker = "ARCH03_DIRECT_READHOST_RESULT";
            var source = $"$ARCH03Input=Read-Host '{prompt}'; Write-Output ('{marker}=' + $ARCH03Input)";
            var boundary = rawOutput.Boundary;
            try
            {
                var request = SelectAsync(source, interactive: true, timeout: CaseTimeout, cancellationToken: inputProbeCancellation.Token);
                var promptCapture = await WaitForCaptureAsync(rawOutput, boundary,
                    text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
                Require(!promptCapture.Truncated && promptCapture.Text.Contains(prompt, StringComparison.Ordinal),
                    "Direct PSES evaluation did not expose the Read-Host prompt through the integrated terminal capture.");
                await service.WriteRawInputAsync("ARCH03_INPUT_OK\r", budget).WaitAsync(InteractiveTimeout, budget);
                var completed = await request;
                var capture = rawOutput.SnapshotAfter(boundary);
                Require(completed.Backend == EditorExecutionCoordinator.StructuredBackendId &&
                    completed.Result.Status == EditorExecutionStatus.Completed &&
                    capture.Text.Contains(marker + "=ARCH03_INPUT_OK", StringComparison.Ordinal) &&
                    !completed.Result.RecoveryRequired &&
                    !capture.Text.Contains("__ps7sd_status_", StringComparison.OrdinalIgnoreCase) &&
                    !capture.Text.Contains("PS7ScriptDesk\\EditorExecution\\", StringComparison.OrdinalIgnoreCase) &&
                    !capture.Text.Contains("WriteAllText", StringComparison.OrdinalIgnoreCase) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                    $"Coordinator did not complete direct LSP Read-Host using shared ConPTY input. backend={completed.Backend}; status={completed.Result.Status}; output={capture.Text}");
                await RunConsoleAndWaitAsync(service, rawOutput,
                    "Write-Output ('ARCH04_HISTORY_LEGITIMATE=' + 'PRESENT')", "ARCH04_HISTORY_LEGITIMATE=PRESENT", generation, budget);
                var historyBoundary = rawOutput.Boundary;
                var historyQuery = "$h=@(Get-History -Count 128 | Where-Object { $_.CommandLine -match '^\\s*(?:\\.|&)\\s+\\$global:__ps7sd_editor_[0-9a-f]{32}\\s*$' }); $legit=@(Get-History -Count 128 | Where-Object { $_.CommandLine -match 'ARCH04_HISTORY_LEGITIMATE' }); Write-Output ('ARCH04_'+'HISTORY=' + $h.Count + ':' + $legit.Count)";
                await RunConsoleAndWaitAsync(service, rawOutput, historyQuery, "ARCH04_HISTORY=", generation, budget);
                var historyCapture = rawOutput.SnapshotAfter(historyBoundary);
                Require(!historyCapture.Truncated && historyCapture.Text.Contains("ARCH04_HISTORY=0:1", StringComparison.Ordinal),
                    "Coordinator-routed Read-Host added its internal foreground invocation to PSReadLine history or altered a legitimate entry.");
                result.Tests.Add(Timed(Pass("ARCH03-A05", "Direct PSES Read-Host input handoff", "Production integration",
                    $"The production coordinator selected {completed.Backend}, accepted real ConPTY input, preserved request-scoped output isolation and legitimate history, and completed in PID={pid}/generation={generation}."), inputProbeTimer));
                result.Tests.Add(Timed(Pass("ARCH04-OUTPUT-HISTORY-01", "Migrated Read-Host output and history isolation", "Production integration",
                    "The coordinator-routed Read-Host request contained no control wrapper in its own capture; its generated invocation added no PSReadLine history entry, and a legitimate console entry remained."), inputProbeTimer));
            }
            catch (Exception ex)
            {
                inputProbeCancellation.Cancel();
                var settled = false;
                try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)); settled = !service.IsSessionRunning; }
                catch { }
                result.Tests.Add(Timed(Fail("ARCH03-A05", "Direct PSES Read-Host input handoff",
                    $"{ShortException(ex)}; owned-session shutdown confirmed={settled}"), inputProbeTimer));
                result.Notes.Add("ARCH-03 direct interactive probe failed or timed out; the owned PSES session was not reused and subsequent Fast01 cases are BLOCKED.");
                return;
            }
        }

        var inputCancelTimer = Stopwatch.StartNew();
        using (var inputCancel = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            var prompt = "ARCH04_DIRECT_CANCEL_PROMPT";
            var boundary = rawOutput.Boundary;
            try
            {
                var pending = SelectAsync($"$null=Read-Host '{prompt}'; Write-Output 'ARCH04_CANCEL_UNEXPECTED_COMPLETION'",
                    interactive: true, timeout: TimeSpan.FromSeconds(20), cancellationToken: inputCancel.Token);
                var promptCapture = await WaitForCaptureAsync(rawOutput, boundary,
                    text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
                Require(!promptCapture.Truncated && promptCapture.Text.Contains(prompt, StringComparison.Ordinal),
                    "Coordinator-routed direct Read-Host cancellation prompt was not visible.");
                inputCancel.Cancel();
                var canceled = await pending.WaitAsync(TimeSpan.FromSeconds(15), budget);
                var recovery = await ExecuteDirectSelectionAsync("Write-Output 'ARCH04_CANCEL_RECOVERY_OK'", budget);
                var recoveryCapture = rawOutput.SnapshotAfter(boundary).Text;
                Require(canceled.Backend == EditorExecutionCoordinator.StructuredBackendId &&
                    canceled.Result.Status == EditorExecutionStatus.Cancelled && !canceled.Result.RecoveryRequired &&
                    recovery.Status == EditorExecutionStatus.Completed && recoveryCapture.Contains("ARCH04_CANCEL_RECOVERY_OK", StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                    $"Direct Read-Host cancellation/reuse was not proven. backend={canceled.Backend}; status={canceled.Result.Status}; recovery={recovery.Status}; generation={generation}->{service.SessionGeneration}.");
                result.Tests.Add(Timed(Pass("ARCH04-C01", "Direct input cancellation and reuse", "Production integration",
                    $"Coordinator-routed Read-Host was interrupted, acknowledged, and followed by direct execution in PID={pid}/generation={generation}."), inputCancelTimer));
            }
            catch (Exception ex)
            {
                inputCancel.Cancel();
                result.Tests.Add(Timed(Fail("ARCH04-C01", "Direct input cancellation and reuse", ex), inputCancelTimer));
                if (!service.IsSessionRunning || !service.IsInputWritable)
                {
                    result.Notes.Add("Direct input cancellation left session readiness unverified; remaining cases BLOCKED and owned session is shut down.");
                    foreach (var id in new[] { "C01", "C02", "R01", "R02", "R03", "ECHO-HISTORY-01", "C04", "C03" })
                        result.Tests.Add(new TestResult(id, NameForFast01(id), "Production integration", "A02-ARCH-04 bounded qualification", "Not run after unverified cancellation", "BLOCKED", "The service reported unavailable or unverified interactive readiness after cancellation."));
                    try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
                    return;
                }
            }
        }

        var cwdProbeTimer = Stopwatch.StartNew();
        var cwdProbePath = Path.Combine(Path.GetTempPath(), "PS7SD-ARCH03-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwdProbePath);
        var cwdProbeFile = Path.Combine(cwdProbePath, "relative-check.txt");
        await File.WriteAllTextAsync(cwdProbeFile, "ARCH03_RELATIVE_OK", budget);
        try
        {
            var encodedCwd = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cwdProbePath));
            var scopeBoundary = rawOutput.Boundary;
            var change = await ExecuteDirectSelectionAsync($"Set-Location -LiteralPath ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedCwd}'))); Write-Output ('ARCH03_SCOPE=' + $PSScriptRoot + '|' + $PSCommandPath)");
            var scopeText = rawOutput.SnapshotAfter(scopeBoundary).Text;
            var verifyBoundary = rawOutput.Boundary;
            var verify = await ExecuteDirectSelectionAsync("Write-Output ('ARCH03_CWD=' + (Get-Location).Path); Get-Content -LiteralPath './relative-check.txt'");
            var verifyText = rawOutput.SnapshotAfter(verifyBoundary).Text;
            Require(change.Status == EditorExecutionStatus.Completed && verify.Status == EditorExecutionStatus.Completed &&
                verify.CompletionObserved && verifyText.Contains(Path.GetFullPath(cwdProbePath), StringComparison.OrdinalIgnoreCase) &&
                verifyText.Contains("ARCH03_RELATIVE_OK", StringComparison.Ordinal) &&
                scopeText.Contains("ARCH03_SCOPE=", StringComparison.Ordinal) &&
                scopeText.Contains(".ps1", StringComparison.Ordinal),
                $"Direct PSES Run Selection did not preserve location/relative path. change={change.Status}; verify={verify.Status}; output={verifyText}");
            result.Tests.Add(Timed(Pass("ARCH03-A03", "Direct selection scope and working directory", "Production integration",
                "Dot-sourced selection state persisted across direct PSES requests; relative path resolved; snapshot exposed its real PSScriptRoot/PSCommandPath."), cwdProbeTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("ARCH03-A03", "Direct selection scope and working directory", ex), cwdProbeTimer)); }
        finally
        {
            var originalPath = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_STARTUP_CWD") ?? Path.GetTempPath()));
            try { await ExecuteDirectSelectionAsync($"Set-Location -LiteralPath ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{originalPath}')))" ); } catch { }
            try { Directory.Delete(cwdProbePath, recursive: true); } catch { }
        }

        var secureProbeTimer = Stopwatch.StartNew();
        {
            const string typedValue = "ARCH04_DAP_SECURE_SENTINEL";
            var prompt = "ARCH04_DAP_SECURE_PROMPT";
            var marker = "ARCH04_DAP_SECURE_LENGTH";
            var boundary = rawOutput.Boundary;
            using var secureCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget);
            Task<object>? execution = null;
            try
            {
                execution = EvaluateDapExpressionAsync(Guid.NewGuid(), $"$s=Read-Host -AsSecureString '{prompt}'; Write-Output ('{marker}=' + $s.Length)", secureCancellation.Token);
                var seen = await WaitForCaptureAsync(rawOutput, boundary, text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
                Require(!seen.Truncated && seen.Text.Contains(prompt, StringComparison.Ordinal), "Direct secure Read-Host prompt was not visible.");
                await service.WriteRawInputAsync(typedValue + "\r", budget).WaitAsync(InteractiveTimeout, budget);
                var secureResult = await execution.WaitAsync(CaseTimeout, budget);
                var secureCapture = rawOutput.SnapshotAfter(boundary).Text;
                Require(secureResult.GetType().GetProperty("Status")?.GetValue(secureResult)?.ToString() == "Completed" &&
                    secureCapture.Contains(marker + "=" + typedValue.Length, StringComparison.Ordinal) &&
                    !secureCapture.Contains(typedValue, StringComparison.Ordinal),
                    $"Direct SecureString input failed or was exposed. status={secureResult.GetType().GetProperty("Status")?.GetValue(secureResult)}; outputLength={secureCapture.Length}; secretExposed={secureCapture.Contains(typedValue, StringComparison.Ordinal)}");
                result.Tests.Add(Timed(Pass("ARCH04-A06-DAP", "PSES DAP secure input experiment", "Production integration",
                    "The existing same-process PSES DAP evaluate request completed secure input without echoing the value."), secureProbeTimer));
            }
            catch (Exception ex)
            {
                secureCancellation.Cancel();
                string settlement = "execution-not-started";
                if (execution is not null)
                {
                    try
                    {
                        var canceled = await execution.WaitAsync(TimeSpan.FromSeconds(12), budget);
                        settlement = "cancellationResult=" + canceled.GetType().GetProperty("Status")?.GetValue(canceled)?.ToString();
                    }
                    catch (Exception settleException) { settlement = "cancellationSettlement=" + ShortException(settleException); }
                }
                result.Tests.Add(Timed(Fail("ARCH03-A06", "Direct PSES secure input", $"{ShortException(ex)}; {settlement}"), secureProbeTimer));
                if (!service.IsSessionRunning || !service.IsInputWritable)
                {
                    result.Notes.Add("The secure-input evaluation did not settle into a writable prompt; remaining cases are BLOCKED and the owned service is shut down.");
                    foreach (var id in new[] { "A06-FAST01", "C01", "C02", "R01", "R02", "R03", "ECHO-HISTORY-01", "C04", "C03" })
                        result.Tests.Add(new TestResult(id, NameForFast01(id), "Production integration", "A02-ARCH-04 bounded qualification", "Not run after unverified secure-input settlement", "BLOCKED", "PSES readiness was not verified after the secure-input request."));
                    try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
                    return;
                }
            }
        }

        var a06 = Stopwatch.StartNew();
        using (var caseCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            try
            {
                var source = "$a='FAST01_OUT'; Write-Output $a; Write-Warning 'FAST01_WARNING'; Write-Error 'FAST01_NONTERMINATING' -ErrorAction Continue; try { throw 'FAST01_CAUGHT' } catch { Write-Output $_.Exception.Message }";
                var boundary = rawOutput.Boundary;
                var execution = service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                    "A02-FAST-01 A06 completion containment", source, true, _ => { }, caseCancellation.Token);
                var completed = await execution.WaitAsync(CaseTimeout, budget);
                var capture = await WaitForCaptureAsync(rawOutput, boundary,
                    text => text.Contains("FAST01_CAUGHT", StringComparison.Ordinal), TimeSpan.FromSeconds(5), budget);
                Require(completed.Status == EditorExecutionStatus.Completed && completed.CompletionObserved &&
                    !capture.Truncated && capture.Text.Contains("FAST01_OUT", StringComparison.Ordinal) &&
                    capture.Text.Contains("FAST01_WARNING", StringComparison.Ordinal) &&
                    capture.Text.Contains("FAST01_NONTERMINATING", StringComparison.Ordinal) &&
                    capture.Text.Contains("FAST01_CAUGHT", StringComparison.Ordinal),
                    "A06 mixed-stream markers or structured completion were not verified.");
                result.Tests.Add(Timed(Pass("A06-FAST01", "Mixed output and structured completion", "Production integration",
                    $"All mixed-stream markers and structured completion observed; PID={pid}; generation={generation}."), a06));
            }
            catch (Exception ex)
            {
                caseCancellation.Cancel();
                var settled = false;
                try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)); settled = !service.IsSessionRunning; }
                catch { }
                result.Tests.Add(Timed(Fail("A06-FAST01", "Mixed output and structured completion", ex), a06));
                result.Notes.Add($"A06 containment: request cancellation attempted; owned service shutdown confirmed={settled}; uncertain session was not reused; dependent targeted cases BLOCKED.");
                foreach (var id in new[] { "C01", "C02", "C03", "C04" })
                    result.Tests.Add(new TestResult(id, NameForFast01(id), "Production integration", "Targeted A02-FAST-01 parity case", "Not run after A06 timeout", "BLOCKED", "The owned session was shut down after unverified A06 completion."));
                return;
            }
        }

        var initialCwd = service.CurrentWorkingDirectory;
        var cwd = Path.Combine(Path.GetTempPath(), "PS7SD-FAST01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var relativeFile = Path.Combine(cwd, "relative-check.txt");
        await File.WriteAllTextAsync(relativeFile, "FAST01_RELATIVE_OK", budget);
        try
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var path = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cwd));
                var selected = await SelectAsync($"Set-Location -LiteralPath ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{path}')))");
                var verify = "Write-Output ('FAST01_CWD=' + (Get-Location).Path); Get-Content -LiteralPath './relative-check.txt'";
                var boundary = rawOutput.Boundary;
                await RunConsoleAndWaitAsync(service, rawOutput, verify, "FAST01_RELATIVE_OK", generation, budget);
                var text = rawOutput.SnapshotAfter(boundary).Text;
                Require(selected.Backend == EditorExecutionCoordinator.LegacyBackendId &&
                    selected.Result.Status == EditorExecutionStatus.Completed &&
                    text.Contains(Path.GetFullPath(cwd), StringComparison.OrdinalIgnoreCase) &&
                    text.Contains("FAST01_RELATIVE_OK", StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                    $"Run Selection CWD fallback attribution or relative path failed. route={selected.Backend}; status={selected.Result.Status}; output={text}");
                result.Tests.Add(Timed(Pass("C01", "Unknown selection CWD fallback attribution", "Production integration",
                    $"The dynamic Set-Location selection remained on {selected.Backend}; relative file resolution and same-session CWD were preserved. Direct PSES CWD semantics are separately covered by ARCH03-A03."), timer));
            }
            catch (Exception ex) { result.Tests.Add(Timed(Fail("C01", "Run Selection working directory and relative path", ex), timer)); }

            var promptTimer = Stopwatch.StartNew();
            try
            {
                var script = "$FAST01First=Read-Host 'FAST01_PROMPT_ONE'; $FAST01Second=Read-Host 'FAST01_PROMPT_TWO'; Write-Output ('FAST01_PROMPTS=' + $FAST01First + ':' + $FAST01Second)";
                var pending = SelectAsync(script, interactive: true);
                var route = coordinator.Route(new EditorExecutionRequest(Guid.NewGuid(), generation, EditorExecutionMode.RunSelection,
                    "A02-FAST-01 interactive route observation", script, WorkingDirectory: service.CurrentWorkingDirectory,
                    ExecuteInCurrentScope: true,
                    CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresInteractiveInput: true, RequiresCurrentScope: true,
                        RequiresWorkingDirectoryContinuity: true), SourceInfo: new EditorExecutionSourceInfo(Guid.NewGuid(), 1, 0, script.Length)));
                var first = await WaitForCaptureAsync(rawOutput, rawOutput.Boundary,
                    text => text.Contains("FAST01_PROMPT_ONE", StringComparison.Ordinal), InteractiveTimeout, budget);
                Require(!first.Truncated && first.Text.Contains("FAST01_PROMPT_ONE", StringComparison.Ordinal), "First Read-Host prompt not observed.");
                await service.WriteRawInputAsync("FIRST_OK\r", budget).WaitAsync(InteractiveTimeout, budget);
                var second = await WaitForCaptureAsync(rawOutput, first.LastSequence,
                    text => text.Contains("FAST01_PROMPT_TWO", StringComparison.Ordinal), InteractiveTimeout, budget);
                Require(!second.Truncated && second.Text.Contains("FAST01_PROMPT_TWO", StringComparison.Ordinal), "Second Read-Host prompt not observed.");
                await service.WriteRawInputAsync("SECOND_OK\r", budget).WaitAsync(InteractiveTimeout, budget);
                var completed = await pending;
                Require(completed.Result.Status == EditorExecutionStatus.Completed && route.SelectedBackend == EditorExecutionCoordinator.LegacyBackendId &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                    $"Two-prompt selection failed or session changed. route={route.SelectedBackend}; status={completed.Result.Status}.");
                var verifyBoundary = rawOutput.Boundary;
                await RunConsoleAndWaitAsync(service, rawOutput, "Write-Output ('FAST01_PROMPT_VERIFY=' + $FAST01First + ':' + $FAST01Second)",
                    "FAST01_PROMPT_VERIFY=FIRST_OK:SECOND_OK", generation, budget);
                var verified = rawOutput.SnapshotAfter(verifyBoundary).Text;
                Require(verified.Contains("FAST01_PROMPT_VERIFY=FIRST_OK:SECOND_OK", StringComparison.Ordinal), "Prompt values did not persist to the next request.");
                result.Tests.Add(Timed(Pass("C02", "Sequential Read-Host prompts and session continuity", "Production integration",
                    $"Both inputs and later state verified; route={route.SelectedBackend} (native interactive fallback); PID/generation stable."), promptTimer));
            }
            catch (Exception ex) { result.Tests.Add(Timed(Fail("C02", "Sequential Read-Host prompts and session continuity", ex), promptTimer)); }

            var smokeTimer = Stopwatch.StartNew();
            try
            {
                await RunConsoleAndWaitAsync(service, rawOutput, "Write-Output ('FAST01_HISTORY_LEGITIMATE=' + 'PRESENT')", "FAST01_HISTORY_LEGITIMATE=PRESENT", generation, budget);
                var functionBoundary = rawOutput.Boundary;
                var functionSelection = await SelectAsync("function FAST01-SmokeFunction { 'FAST01_FUNCTION_OK' }; Write-Output 'FAST01_FUNCTION_DEFINED'");
                var functionDefinitionOutput = rawOutput.SnapshotAfter(functionBoundary).Text;
                var functionDefinitionPassed = functionSelection.Result.Status == EditorExecutionStatus.Completed &&
                    functionSelection.Backend == EditorExecutionCoordinator.StructuredBackendId &&
                    functionDefinitionOutput.Contains("FAST01_FUNCTION_DEFINED", StringComparison.Ordinal);
                result.Tests.Add(Timed(functionDefinitionPassed
                    ? Pass("R01", "Ordinary function definition through Direct PSES", "Production integration", "Run Selection defined an unqualified function through the direct PSES route.")
                    : Fail("R01", "Ordinary function definition through Direct PSES", $"route={functionSelection.Backend}; status={functionSelection.Result.Status}; output={functionDefinitionOutput}"), smokeTimer));

                var variableBoundary = rawOutput.Boundary;
                var variableSelection = await SelectAsync("$FAST01SmokeVariable='FAST01_VARIABLE_OK'; Write-Output 'FAST01_VARIABLE_DEFINED'");
                var variableDefinitionOutput = rawOutput.SnapshotAfter(variableBoundary).Text;
                var variableDefinitionPassed = variableSelection.Result.Status == EditorExecutionStatus.Completed &&
                    variableSelection.Backend == EditorExecutionCoordinator.StructuredBackendId &&
                    variableDefinitionOutput.Contains("FAST01_VARIABLE_DEFINED", StringComparison.Ordinal);
                result.Tests.Add(Timed(variableDefinitionPassed
                    ? Pass("R02", "Ordinary variable assignment through Direct PSES", "Production integration", "Run Selection assigned an unqualified variable through the direct PSES route.")
                    : Fail("R02", "Ordinary variable assignment through Direct PSES", $"route={variableSelection.Backend}; status={variableSelection.Result.Status}; output={variableDefinitionOutput}"), smokeTimer));

                var crossSelectionBoundary = rawOutput.Boundary;
                var crossSelection = await SelectAsync("FAST01-SmokeFunction");
                var crossSelectionOutput = rawOutput.SnapshotAfter(crossSelectionBoundary).Text;
                var crossSelectionPassed = crossSelection.Result.Status == EditorExecutionStatus.Completed &&
                    crossSelection.Backend == EditorExecutionCoordinator.LegacyBackendId &&
                    crossSelectionOutput.Contains("FAST01_FUNCTION_OK", StringComparison.Ordinal);
                result.Tests.Add(Timed(crossSelectionPassed
                    ? Pass("R03", "Unknown function-call fallback attribution", "Production integration", "The function defined by the prior direct selection remained callable in the same session; its bare custom command name is conservatively classified Unknown and stays on the legacy fallback.")
                    : Fail("R03", "Unknown function-call fallback attribution", $"route={crossSelection.Backend}; status={crossSelection.Result.Status}; output={crossSelectionOutput}"), smokeTimer));

                var selected = variableSelection;
                var verifyBoundary = rawOutput.Boundary;
                await RunConsoleAndWaitAsync(service, rawOutput, "Write-Output $FAST01SmokeVariable; FAST01-SmokeFunction", "FAST01_FUNCTION_OK", generation, budget);
                var verify = rawOutput.SnapshotAfter(verifyBoundary).Text;
                var historySource = "$h=@(Get-History -Count 128 | Where-Object { $_.CommandLine -match '^\\$__ps7sd_selection_success_[0-9a-f]{32}' }); $legit=@(Get-History -Count 128 | Where-Object { $_.CommandLine -match 'FAST01_HISTORY_LEGITIMATE' }); Write-Output ('FAST01_'+'HISTORY=' + $h.Count + ':' + $legit.Count)";
                var historyBoundary = rawOutput.Boundary;
                await RunConsoleAndWaitAsync(service, rawOutput, historySource, "FAST01_HISTORY=", generation, budget);
                var historyText = rawOutput.SnapshotAfter(historyBoundary).Text;
                var zeroWrappers = historyText.Contains("FAST01_HISTORY=0:", StringComparison.Ordinal);
                var legitPreserved = historyText.Contains("FAST01_HISTORY=0:1", StringComparison.Ordinal);
                static bool ContainsControlWrapper(string text) =>
                    text.Contains("$__ps7sd_selection_success_", StringComparison.Ordinal) ||
                    text.Contains("__ps7sd_status_", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("WriteAllText", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("PS7ScriptDesk\\EditorExecution\\", StringComparison.OrdinalIgnoreCase);
                var noWrapperEcho = !ContainsControlWrapper(functionDefinitionOutput) && !ContainsControlWrapper(variableDefinitionOutput);
                var smokePassed = functionDefinitionPassed && variableDefinitionPassed && crossSelectionPassed &&
                    selected.Result.Status == EditorExecutionStatus.Completed && selected.Backend == EditorExecutionCoordinator.StructuredBackendId &&
                    verify.Contains("FAST01_VARIABLE_OK", StringComparison.Ordinal) && verify.Contains("FAST01_FUNCTION_OK", StringComparison.Ordinal) &&
                    functionDefinitionOutput.Contains("FAST01_FUNCTION_DEFINED", StringComparison.Ordinal) &&
                    variableDefinitionOutput.Contains("FAST01_VARIABLE_DEFINED", StringComparison.Ordinal) &&
                    noWrapperEcho && zeroWrappers && legitPreserved &&
                    pid == service.ProcessId && generation == service.SessionGeneration;
                var details = $"route={selected.Backend}; wrapperEchoAbsent={noWrapperEcho}; wrapperHistoryCountZero={zeroWrappers}; legitimateHistoryPreserved={legitPreserved}; samePidGeneration={pid == service.ProcessId && generation == service.SessionGeneration}.";
                result.Tests.Add(Timed(new TestResult("ECHO-HISTORY-01", "Route-specific wrapper and legitimate history", "Production integration",
                    "ConPTY requires the history guard and no wrapper history; direct PSES requires zero wrapper entries and legitimate history retention.",
                    details, smokePassed ? "PASS" : "FAIL", smokePassed ? null : "Direct PSES smoke/history assertions failed."), smokeTimer));
                result.Tests.Add(Timed(smokePassed
                    ? Pass("C04", "Direct PSES function, variable, output, and session smoke", "Production integration", details)
                    : Fail("C04", "Direct PSES function, variable, output, and session smoke", details), smokeTimer));
            }
            catch (Exception ex)
            {
                result.Tests.Add(Timed(Fail("C04", "Direct PSES function, variable, output, and session smoke", ex), smokeTimer));
                result.Tests.Add(new TestResult("ECHO-HISTORY-01", "Route-specific wrapper and legitimate history", "Production integration", "Read-only history assertions", "Not completed", "FAIL", ShortException(ex)));
            }

            var childTimer = Stopwatch.StartNew();
            var childStdoutVerified = false;
            var childStderrVerified = false;
            var childExitVerified = false;
            try
            {
                var baselineCommand = "echo FAST01_CHILD_STDOUT_OK & echo FAST01_CHILD_STDERR_OK 1>&2 & exit /b 7";
                var baselineEncoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(baselineCommand));
                var baselineSource = $"& $env:ComSpec /d /c ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{baselineEncoded}'))); $FAST01_BASELINE_EXIT=$LASTEXITCODE; Write-Output ('FAST01_CHILD_EXIT=' + $FAST01_BASELINE_EXIT)";
                var baselineBoundary = rawOutput.Boundary;
                var baseline = await ExecuteDirectSelectionAsync(baselineSource, budget).WaitAsync(TimeSpan.FromSeconds(20), budget);
                var baselineCapture = rawOutput.SnapshotAfter(baselineBoundary);
                var baselineText = baselineCapture.Text;
                childStdoutVerified = baselineText.Contains("FAST01_CHILD_STDOUT_OK", StringComparison.Ordinal);
                childStderrVerified = baselineText.Contains("FAST01_CHILD_STDERR_OK", StringComparison.Ordinal);
                childExitVerified = baselineText.Contains("FAST01_CHILD_EXIT=7", StringComparison.Ordinal);
                Require(baseline.Status == EditorExecutionStatus.Completed && baseline.CompletionObserved && !baselineCapture.Truncated && childStdoutVerified && childStderrVerified && childExitVerified,
                    $"Direct PSES native child baseline failed. stdout={childStdoutVerified}; stderr={childStderrVerified}; exit={childExitVerified}; status={baseline.Status}; output={baselineText}");

                var command = "set /p LINE=FAST01_CHILD_INPUT: & echo FAST01_CHILD_STDOUT:!LINE! & echo FAST01_CHILD_STDERR 1>&2 & exit /b 7";
                var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(command));
                var source = $"& $env:ComSpec /v:on /d /c ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'))); $FAST01_CHILD_EXIT=$LASTEXITCODE; Write-Output ('FAST01_CHILD_EXIT=' + $FAST01_CHILD_EXIT)";
                var boundary = rawOutput.Boundary;
                var pending = ExecuteDirectSelectionAsync(source, budget);
                var childPrompt = await WaitForCaptureAsync(rawOutput, boundary,
                    text => text.Contains("FAST01_CHILD_INPUT:", StringComparison.Ordinal), InteractiveTimeout, budget);
                Require(!childPrompt.Truncated && childPrompt.Text.Contains("FAST01_CHILD_INPUT:", StringComparison.Ordinal),
                    "Direct native child stdin prompt was not visible.");
                await service.WriteRawInputAsync("CHILD_INPUT_OK\r", budget).WaitAsync(InteractiveTimeout, budget);
                var completed = await pending.WaitAsync(TimeSpan.FromSeconds(20), budget);
                var childText = rawOutput.SnapshotAfter(boundary).Text;
                Require(completed.Status == EditorExecutionStatus.Completed && completed.CompletionObserved &&
                    childText.Contains("FAST01_CHILD_STDOUT:CHILD_INPUT_OK", StringComparison.Ordinal) &&
                    childText.Contains("FAST01_CHILD_STDERR", StringComparison.Ordinal) &&
                    childText.Contains("FAST01_CHILD_EXIT=7", StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                    $"Direct native child contract incomplete. stdout={childText.Contains("FAST01_CHILD_STDOUT:CHILD_INPUT_OK", StringComparison.Ordinal)}; stderr={childText.Contains("FAST01_CHILD_STDERR", StringComparison.Ordinal)}; exit={childText.Contains("FAST01_CHILD_EXIT=7", StringComparison.Ordinal)}; status={completed.Status}.");

                var esc = ((char)27).ToString();
                var ansiCommand = "echo " + esc + "[31mFAST01_ANSI_OK" + esc + "[0m";
                var ansiEncoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ansiCommand));
                var ansiBoundary = rawOutput.Boundary;
                var ansiResult = await ExecuteDirectSelectionAsync($"& $env:ComSpec /d /c ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{ansiEncoded}')))", budget).WaitAsync(TimeSpan.FromSeconds(20), budget);
                var ansiText = rawOutput.SnapshotAfter(ansiBoundary).Text;
                var ansiVerified = ansiText.Contains(esc + "[31mFAST01_ANSI_OK" + esc + "[0m", StringComparison.Ordinal);
                Require(ansiResult.Status == EditorExecutionStatus.Completed && ansiVerified,
                    $"Direct native child VT output failed. completed={ansiResult.Status}; ansi={ansiVerified}; outputLength={ansiText.Length}.");
                result.Tests.Add(Timed(Pass("C03", "Direct native child I/O and ANSI", "Production integration",
                    $"Direct same-session cmd.exe stdout/stderr/exit/stdin and ANSI output passed; PID/generation stable."), childTimer));
            }
            catch (Exception ex)
            {
                result.Tests.Add(Timed(Fail("C03", "Native child stdin, stdout, stderr, and exit status", ex), childTimer));
                result.Notes.Add($"Direct C03 observed: stdout={childStdoutVerified}, stderr={childStderrVerified}, exitCode7={childExitVerified}. The owner session is shut down after an unqualified child I/O outcome.");
                try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
                return;
            }


        }
        finally
        {
            if (initialCwd is not null && service.IsSessionRunning)
            {
                try { await RunStructuredAsync(service, "FAST01 restore working directory", "Set-Location -LiteralPath '" + initialCwd.Replace("'", "''") + "'", CancellationToken.None); }
                catch { }
            }
            try { if (Directory.Exists(cwd)) Directory.Delete(cwd, recursive: true); } catch { }
        }
    }

    private static string NameForFast01(string id) => id switch
    {
        "C01" => "Run Selection working directory and relative path",
        "C02" => "Sequential Read-Host prompts and session continuity",
        "C03" => "Native child stdin, stdout, stderr, and exit status",
        _ => "Direct PSES smoke and history"
    };

    private static async Task RunCoreCasesAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture rawOutput, SequencedOutputCapture visibleOutput, CancellationToken budget, List<string> redactions)
    {
        var initialPid = service.ProcessId;
        var initialGeneration = service.SessionGeneration;
        var console = new ConcurrentQueue<string>();
        var a02Timer = Stopwatch.StartNew();
        try
        {
            const string marker = "A02_NATIVE_CONSOLE_OK";
            const string source = "$m = 'A02_' + 'NATIVE_CONSOLE_OK'; Write-Output $m";
            var before = rawOutput.Boundary;
            var response = await service.ExecuteConsoleCommandAsync(source, r => console.Enqueue(r.Text))
                .WaitAsync(CaseTimeout, budget);
            var snapshot = await WaitForCaptureAsync(rawOutput, before,
                value => HarnessContractChecks.HasRuntimeMarker(source, marker, value), TimeSpan.FromSeconds(5), budget);
            Require(!snapshot.Truncated && HarnessContractChecks.HasRuntimeMarker(source, marker, snapshot.Text),
                $"Console runtime output marker was absent after source/echo exclusion; outcome={response.Outcome}; output={snapshot.Text}");
            result.Tests.Add(Timed(Pass("A02", "Native console runtime output (completion/readiness not asserted)", "Production integration",
                $"Runtime marker observed after source/echo exclusion; service outcome={response.Outcome}; PID={response.ProcessId}; generation={response.SessionGeneration}. This assertion does not claim command completion or interactive readiness."), a02Timer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("A02", "Native console runtime output (completion/readiness not asserted)", ex), a02Timer)); }

        var a03Timer = Stopwatch.StartNew();
        result.Tests.Add(Timed(service.ProcessId == initialPid && service.SessionGeneration == initialGeneration && service.IsSessionRunning
            ? Pass("A03", "Shared process and terminal generation", "Production integration", $"PID={service.ProcessId}; generation={service.SessionGeneration} unchanged across console command.")
            : Fail("A03", "Shared process and terminal generation", $"Identity changed or session unavailable: PID={service.ProcessId}; generation={service.SessionGeneration}."), a03Timer));

        var originalCwd = service.CurrentWorkingDirectory;
        var a04Timer = Stopwatch.StartNew();
        var cwdCandidate = Path.Combine(Path.GetTempPath(), "PS7SD-A02-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwdCandidate);
        try
        {
            Require(!string.IsNullOrWhiteSpace(originalCwd), "The initial working directory was not observable; A04 cannot safely restore it.");
            var beforePid = service.ProcessId;
            var beforeGeneration = service.SessionGeneration;
            var pathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cwdCandidate));
            var changeSource = $"Set-Location -LiteralPath ([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pathBase64}')))";
            var change = await RunStructuredAsync(service, "A04 change working directory", changeSource, budget);
            Require(change.Status == EditorExecutionStatus.Completed && change.CompletionObserved, "Working-directory change request did not complete: " + change.ErrorMessage);
            var verifySource = "Write-Output ((Get-Location).Path)";
            var verifyBoundary = rawOutput.Boundary;
            var verify = await RunStructuredAsync(service, "A04 independent working-directory verification", verifySource, budget);
            var locationOutput = await WaitForCaptureAsync(rawOutput, verifyBoundary,
                text => HarnessContractChecks.HasRuntimeMarker(verifySource, cwdCandidate, text), TimeSpan.FromSeconds(5), budget);
            Require(verify.Status == EditorExecutionStatus.Completed && verify.CompletionObserved &&
                    !locationOutput.Truncated &&
                    HarnessContractChecks.HasRuntimeMarker(verifySource, cwdCandidate, locationOutput.Text) &&
                    beforePid == service.ProcessId && beforeGeneration == service.SessionGeneration,
                $"Cross-request location persistence failed. result={verify.Status}; resultLocation={verify.CurrentWorkingDirectory}; PID {beforePid}->{service.ProcessId}; generation {beforeGeneration}->{service.SessionGeneration}; output={locationOutput.Text}");
            result.Tests.Add(Timed(Pass("A04", "Working-directory state persists across separate requests", "Production integration",
                $"Change and verification were separate completed requests; observed {cwdCandidate}; PID/generation continuity confirmed."), a04Timer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("A04", "Working-directory state persists across separate requests", ex), a04Timer)); }
        finally
        {
            var restoreTimer = Stopwatch.StartNew();
            if (!string.IsNullOrWhiteSpace(originalCwd))
            {
                try
                {
                    // Restore to the recorded original path using escaped single-quoted PowerShell text.
                    var escapedOriginal = originalCwd.Replace("'", "''");
                    await RunStructuredAsync(service, "A04 restore original working directory", $"Set-Location -LiteralPath '{escapedOriginal}'", budget);
                    var restoreBoundary = rawOutput.Boundary;
                    var restore = await RunStructuredAsync(service, "A04 independent restoration verification", "Write-Output ((Get-Location).Path)", budget);
                    var originalCwdOutput = Path.TrimEndingDirectorySeparator(originalCwd);
                    var restored = await WaitForCaptureAsync(rawOutput, restoreBoundary,
                        text => HarnessContractChecks.HasRuntimeMarker("Write-Output ((Get-Location).Path)", originalCwdOutput, text), TimeSpan.FromSeconds(5), CancellationToken.None);
                    Require(restore.Status == EditorExecutionStatus.Completed && restore.CompletionObserved &&
                            !restored.Truncated &&
                            HarnessContractChecks.HasRuntimeMarker("Write-Output ((Get-Location).Path)", originalCwdOutput, restored.Text),
                        $"Original working directory restoration could not be independently verified. expected={originalCwd}; resultLocation={restore.CurrentWorkingDirectory}; output={restored.Text}");
                    result.Tests.Add(Timed(Pass("A04-CLEANUP", "Restore and independently verify original working directory", "Production integration",
                        $"Separate restoration and verification requests confirmed {originalCwdOutput}."), restoreTimer));
                }
                catch (Exception cleanupError)
                {
                    result.Tests.Add(Timed(Fail("A04-CLEANUP", "Restore and verify original working directory", cleanupError), restoreTimer));
                }
            }
            var directoryCleanupTimer = Stopwatch.StartNew();
            try
            {
                if (Directory.Exists(cwdCandidate)) Directory.Delete(cwdCandidate, recursive: true);
                Require(!Directory.Exists(cwdCandidate), "A04 test-controlled working directory was not removed.");
                result.Tests.Add(Timed(Pass("A04-DIRECTORY", "Remove A04 test-controlled directory", "Harness cleanup",
                    "Unique test directory removal verified."), directoryCleanupTimer));
            }
            catch (Exception cleanupError)
            {
                result.Tests.Add(Timed(Fail("A04-DIRECTORY", "Remove A04 test-controlled directory", cleanupError), directoryCleanupTimer));
            }
        }

        var nativeOutput = new ConcurrentQueue<string>();
        var a05Timer = Stopwatch.StartNew();
        try
        {
            var nativeMarker = "A02_NATIVE_EXE_OK";
            var nativeSource = "$nativeMarker = 'A02_' + 'NATIVE_EXE_OK'; & $env:ComSpec /d /c echo $nativeMarker; $nativeExit = $LASTEXITCODE; Write-Output ('A02_NATIVE_EXIT=' + $nativeExit)";
            var before = rawOutput.Boundary;
            var nativeResult = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "A02 native executable acceptance", nativeSource,
                true, r => nativeOutput.Enqueue(r.Text)).WaitAsync(CaseTimeout, budget);
            var nativeCapture = rawOutput.SnapshotAfter(before);
            var nativeText = nativeCapture.Text;
            Require(nativeResult.Status == EditorExecutionStatus.Completed && nativeResult.CompletionObserved &&
                    !nativeCapture.Truncated && HarnessContractChecks.HasRuntimeMarker(nativeSource, nativeMarker, nativeText) && nativeText.Contains("A02_NATIVE_EXIT=0", StringComparison.OrdinalIgnoreCase), "Native executable marker/exit status missing or capture was truncated: " + nativeText);
            result.Tests.Add(Timed(Pass("A05", "Native Windows executable output and exit status", "Production integration", "cmd.exe marker observed; $LASTEXITCODE=0."), a05Timer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("A05", "Native Windows executable output and exit status", ex), a05Timer)); }

        var streamOutput = new ConcurrentQueue<string>();
        var a06Timer = Stopwatch.StartNew();
        using var a06Cancellation = CancellationTokenSource.CreateLinkedTokenSource(budget);
        try
        {
            var streamSource = "$a='A02_'+'OUT_STREAM'; $b='A02_'+'WARNING_STREAM'; $c='A02_'+'NONTERMINATING_ERROR'; $d='A02_'+'TERMINATING_EXCEPTION'; Write-Output $a; Write-Warning $b; Write-Error $c -ErrorAction Continue; try { throw $d } catch { Write-Output ('A02_CAUGHT=' + $_.Exception.Message) }";
            var before = rawOutput.Boundary;
            var stream = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "A02 stream acceptance", streamSource,
                true, r => streamOutput.Enqueue(r.Text), a06Cancellation.Token).WaitAsync(CaseTimeout, budget);
            var streamCapture = rawOutput.SnapshotAfter(before);
            var streamText = streamCapture.Text;
            Require(stream.Status == EditorExecutionStatus.Completed && stream.CompletionObserved &&
                    !streamCapture.Truncated &&
                    new[] { "A02_OUT_STREAM", "A02_WARNING_STREAM", "A02_NONTERMINATING_ERROR", "A02_CAUGHT=A02_TERMINATING_EXCEPTION" }
                        .All(m => HarnessContractChecks.HasRuntimeMarker(streamSource, m, streamText)), "One or more runtime stream markers were absent: " + streamText);
            result.Tests.Add(Timed(Pass("A06", "Output, warning, nonterminating error and caught exception", "Production integration", "Runtime output lines and successful structured completion observed; stream text is still terminal-rendered."), a06Timer));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("A06", "Output, warning, nonterminating error and caught exception", ex), a06Timer));
            if (ex is TimeoutException or OperationCanceledException)
            {
                a06Cancellation.Cancel();
                var stopped = false;
                try { await service.ShutdownAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)); stopped = !service.IsSessionRunning; }
                catch { }
                result.Notes.Add($"A06 timeout containment: supported request cancellation attempted; owned service shutdown confirmed={stopped}; uncertain session was not reused.");
                foreach (var id in new[] { "A07", "A08", "A09", "A10", "B07", "B09", "B10", "B11", "B12", "B13", "B14", "B15", "B16" })
                    result.Tests.Add(new TestResult(id, NameFor(id), "Production integration", "Existing acceptance case", "Not run after A06 timeout", "BLOCKED", "The runner canceled the request and shut down its owned session because structured completion was not verified."));
                return;
            }
        }

        await RunT15Async(result, service, rawOutput, budget);
        await RunCoordinatorRunSelectionBoundaryAsync(result, service, rawOutput, budget);
        await RunNativeSelectionScopeProbeAsync(result, service, rawOutput, budget);
        await RunNativeSelectionParityAsync(result, service, rawOutput, budget);

        var runtimeOutput = rawOutput.SnapshotAfter(0).Text;
        result.Notes.Add($"Run File direct bridge disabled; Run Selection route={(result.DirectRunSelectionEnabled ? "direct-lsp-run-selection for ordinary current-scope requests; known-interactive requests use native fallback" : "native-conpty-run-selection")}. Raw console capture {runtimeOutput.Length} characters.");
        var interactiveSafe = await RunInteractiveCasesAsync(result, service, rawOutput, budget, redactions);
        if (interactiveSafe)
            await RunNativeSelectionInteractiveParityAsync(result, service, rawOutput, visibleOutput, budget, redactions);
        if (!interactiveSafe)
            result.Notes.Add("Interactive sequence halted after an unsafe/unverified console state; remaining dependent cases were not run.");
        await RunRepeatedSelectionPerformanceAsync(result, service, rawOutput, budget);
        result.Notes.Add("Secure-input artifact redaction values are applied before writing JSON, Markdown, or terminal captures.");
        result.Notes.Add("Interactive request IDs are recorded in result details; prompt events do not expose request IDs in the production API.");
        result.Notes.Add("Interactive test process isolation is the standalone runner process; session operations are sequential and bounded.");
    }

    private static async Task<bool> RunInteractiveCasesAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture output, CancellationToken budget, List<string> redactions)
    {
        var safe = true;
        var pid = service.ProcessId;
        var generation = service.SessionGeneration;

        var a07 = Stopwatch.StartNew();
        try
        {
            foreach (var input in new[] { "A07-ordinary-" + Guid.NewGuid().ToString("N"), "A07 !@#$; 'quoted' [braces] Ω" })
            {
                var prompt = "A07_NATIVE_INPUT_" + Guid.NewGuid().ToString("N");
                var promptBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt));
                var source = $"$v=Read-Host -Prompt ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{promptBase64}'))); Write-Output ('A07_'+'ROUNDTRIP=' + $v)";
                var marker = "A07_ROUNDTRIP=";
                var boundary = output.Boundary;
                var requestId = Guid.NewGuid();
                var promptWait = ObserveNextPromptAsync(service, generation);
                var submitted = await service.ExecuteConsoleCommandAsync(source, _ => { }, requestId, budget).WaitAsync(CaseTimeout, budget);
                Require(submitted.RequestId == requestId && submitted.SessionGeneration == generation && submitted.ProcessId == pid,
                    $"A07 request/session identity mismatch: request={submitted.RequestId}; generation={submitted.SessionGeneration}; PID={submitted.ProcessId}.");
                var promptSeen = await WaitForCaptureAsync(output, boundary,
                    text => HarnessContractChecks.HasRuntimeMarker(source, prompt, text), InteractiveTimeout, budget);
                Require(!promptSeen.Truncated && HarnessContractChecks.HasRuntimeMarker(source, prompt, promptSeen.Text),
                    $"A07 Read-Host prompt was not observed; requestId={requestId}; output={promptSeen.Text}");
                await service.WriteRawInputAsync(input + "\r", budget).WaitAsync(InteractiveTimeout, budget);
                var response = await WaitForCaptureAsync(output, boundary,
                    text => HarnessContractChecks.HasRuntimeMarker(source, marker, text) && text.Contains(input, StringComparison.Ordinal),
                    InteractiveTimeout, budget);
                Require(!response.Truncated && HarnessContractChecks.HasRuntimeMarker(source, marker, response.Text) && response.Text.Contains(input, StringComparison.Ordinal),
                    $"A07 exact input round-trip was not observed; requestId={requestId}; output={response.Text}");
                await promptWait.WaitAsync(InteractiveTimeout, budget);
            }
            Require(service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation,
                $"A07 session identity changed: PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            result.Tests.Add(Timed(Pass("A07", "Native Read-Host input and console usability", "Production integration",
                $"Two values (ordinary and special-character) were sent via WriteRawInputAsync; exact runtime round-trip and post-command prompt observed. PID={pid}; generation={generation}."), a07));
        }
        catch (Exception ex)
        {
            safe = await RecoverAfterInteractiveFailureAsync(service, generation, result, "A07", ex);
            result.Tests.Add(Timed(Fail("A07", "Native Read-Host input and console usability", ex), a07));
            if (!safe) return false;
        }

        var syntheticSecret = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("Q29kZXhTeW50aGV0aWMtQTA4LTdmMyE="));
        redactions.Add(syntheticSecret);
        var a08 = Stopwatch.StartNew();
        try
        {
            var prompt = "A08_SECURE_INPUT_" + Guid.NewGuid().ToString("N");
            var promptBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt));
            const string marker = "A08_SECURE_OK";
            var source = $"$s=Read-Host -AsSecureString -Prompt ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{promptBase64}'))); if (($s -is [System.Security.SecureString]) -and $s.Length -eq {syntheticSecret.Length}) {{ Write-Output ('A08_'+'SECURE_OK') }} else {{ Write-Output 'A08_SECURE_INVALID' }}";
            var boundary = output.Boundary;
            var requestId = Guid.NewGuid();
            var promptWait = ObserveNextPromptAsync(service, generation);
            var submitted = await service.ExecuteConsoleCommandAsync(source, _ => { }, requestId, budget).WaitAsync(CaseTimeout, budget);
            Require(submitted.RequestId == requestId && submitted.SessionGeneration == generation && submitted.ProcessId == pid,
                $"A08 request/session identity mismatch: request={submitted.RequestId}; generation={submitted.SessionGeneration}; PID={submitted.ProcessId}.");
            var promptSeen = await WaitForCaptureAsync(output, boundary,
                text => HarnessContractChecks.HasRuntimeMarker(source, prompt, text), InteractiveTimeout, budget);
            Require(!promptSeen.Truncated && HarnessContractChecks.HasRuntimeMarker(source, prompt, promptSeen.Text),
                $"A08 secure prompt was not observed; requestId={requestId}.");
            await service.WriteRawInputAsync(syntheticSecret + "\r", budget).WaitAsync(InteractiveTimeout, budget);
            var response = await WaitForCaptureAsync(output, boundary,
                text => HarnessContractChecks.HasRuntimeMarker(source, marker, text), InteractiveTimeout, budget);
            Require(!response.Truncated && HarnessContractChecks.HasRuntimeMarker(source, marker, response.Text) &&
                    !HarnessContractChecks.HasRuntimeMarker(source, "A08_SECURE_INVALID", response.Text),
                $"A08 secure value/type/length marker was absent; requestId={requestId}.");
            await promptWait.WaitAsync(InteractiveTimeout, budget);
            Require(service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation,
                $"A08 session identity changed: PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            Require(!response.Text.Contains(syntheticSecret, StringComparison.Ordinal), "A08 secret appeared in the captured output before redaction.");
            result.Tests.Add(Timed(Pass("A08", "Secure Read-Host input and artifact redaction", "Production integration",
                $"Synthetic secret was submitted through WriteRawInputAsync; SecureString type/length marker, prompt recovery, and identity continuity observed. Secret omitted from result detail; redaction token registered."), a08));
        }
        catch (Exception ex)
        {
            safe = await RecoverAfterInteractiveFailureAsync(service, generation, result, "A08", ex);
            result.Tests.Add(Timed(Fail("A08", "Secure Read-Host input and artifact redaction", ex), a08));
            if (!safe) return false;
        }

        var a09 = Stopwatch.StartNew();
        try
        {
            const string source = "Write-Output ('A09_'+'WORKLOAD_STARTED'); while ($true) { Start-Sleep -Seconds 1 }";
            var marker = "A09_WORKLOAD_STARTED";
            var boundary = output.Boundary;
            var requestId = Guid.NewGuid();
            var promptWait = ObserveNextPromptAsync(service, generation);
            var submitted = await service.ExecuteConsoleCommandAsync(source, _ => { }, requestId, budget).WaitAsync(CaseTimeout, budget);
            Require(submitted.RequestId == requestId && submitted.SessionGeneration == generation && submitted.ProcessId == pid,
                $"A09 workload submission identity mismatch: request={submitted.RequestId}; generation={submitted.SessionGeneration}; PID={submitted.ProcessId}.");
            var started = await WaitForCaptureAsync(output, boundary,
                text => HarnessContractChecks.HasRuntimeMarker(source, marker, text), InteractiveTimeout, budget);
            Require(!started.Truncated && HarnessContractChecks.HasRuntimeMarker(source, marker, started.Text),
                $"A09 workload start marker missing; requestId={requestId}.");
            var interrupt = await service.InterruptOrRestartAsync(cancellationToken: budget).WaitAsync(TimeSpan.FromSeconds(25), budget);
            var promptObserved = false;
            try { await promptWait.WaitAsync(InteractiveTimeout, budget); promptObserved = true; }
            catch (TimeoutException) { }
            Require(interrupt.InterruptAttempted && interrupt.SignalDelivered && interrupt.InteractiveReadinessObserved && promptObserved &&
                    !interrupt.SessionRestarted && !interrupt.ProcessTerminationSucceeded && interrupt.OwnedProcessId == pid &&
                    service.ProcessId == pid && service.SessionGeneration == generation && service.IsSessionRunning,
                $"A09 interrupt was not same-session prompt recovery: outcome={interrupt.Outcome}; signal={interrupt.SignalDelivered}; prompt={promptObserved}; restarted={interrupt.SessionRestarted}; processTerminated={interrupt.ProcessTerminationSucceeded}; PID={interrupt.OwnedProcessId}->{service.ProcessId}; generation={generation}->{service.SessionGeneration}.");
            var verifySource = "Write-Output ('A09_'+'AFTER_INTERRUPT_OK')";
            var verifyBoundary = output.Boundary;
            var verifyPrompt = ObserveNextPromptAsync(service, generation);
            var verifyId = Guid.NewGuid();
            var verify = await service.ExecuteConsoleCommandAsync(verifySource, _ => { }, verifyId, budget).WaitAsync(CaseTimeout, budget);
            var verified = await WaitForCaptureAsync(output, verifyBoundary,
                text => HarnessContractChecks.HasRuntimeMarker(verifySource, "A09_AFTER_INTERRUPT_OK", text), InteractiveTimeout, budget);
            await verifyPrompt.WaitAsync(InteractiveTimeout, budget);
            Require(verify.RequestId == verifyId && !verified.Truncated && HarnessContractChecks.HasRuntimeMarker(verifySource, "A09_AFTER_INTERRUPT_OK", verified.Text),
                $"A09 subsequent command did not complete; requestId={verifyId}; output={verified.Text}");
            result.Tests.Add(Timed(Pass("A09", "Native Ctrl+C interruption and same-session recovery", "Production integration",
                $"Long-running command emitted its start marker; production InterruptOrRestartAsync delivered ETX and observed recovered prompt. Subsequent command succeeded; PID={pid}; generation={generation}."), a09));
        }
        catch (Exception ex)
        {
            safe = await RecoverAfterInteractiveFailureAsync(service, generation, result, "A09", ex);
            result.Tests.Add(Timed(Fail("A09", "Native Ctrl+C interruption and same-session recovery", ex), a09));
            if (!safe) return false;
        }

        var a10 = Stopwatch.StartNew();
        try
        {
            var setupSource = "function A10PersistedFunction { 'A10_FUNCTION_OK' }; $global:A10PersistedVariable='A10_VARIABLE_OK'; Write-Output ('A10_'+'STATE_DEFINED')";
            await RunConsoleAndWaitAsync(service, output, setupSource, "A10_STATE_DEFINED", generation, budget);
            var verifySource = "Write-Output ($global:A10PersistedVariable); A10PersistedFunction";
            var beforeInterrupt = output.Boundary;
            var verifySubmitted = await service.ExecuteConsoleCommandAsync(verifySource, _ => { }, Guid.NewGuid(), budget).WaitAsync(CaseTimeout, budget);
            var definitions = await WaitForCaptureAsync(output, beforeInterrupt,
                text => HarnessContractChecks.HasRuntimeMarker(verifySource, "A10_VARIABLE_OK", text) && HarnessContractChecks.HasRuntimeMarker(verifySource, "A10_FUNCTION_OK", text), InteractiveTimeout, budget);
            Require(verifySubmitted.Outcome == LiveConsoleCommandOutcome.Submitted && !definitions.Truncated &&
                    HarnessContractChecks.HasRuntimeMarker(verifySource, "A10_VARIABLE_OK", definitions.Text) && HarnessContractChecks.HasRuntimeMarker(verifySource, "A10_FUNCTION_OK", definitions.Text),
                "A10 pre-interrupt variable/function definitions were not independently confirmed.");
            await ObserveNextPromptAsync(service, generation).WaitAsync(InteractiveTimeout, budget);

            const string longSource = "Write-Output ('A10_'+'RECOVERY_WORKLOAD_STARTED'); while ($true) { Start-Sleep -Seconds 1 }";
            var longBoundary = output.Boundary;
            var requestId = Guid.NewGuid();
            var recoveryPrompt = ObserveNextPromptAsync(service, generation);
            await service.ExecuteConsoleCommandAsync(longSource, _ => { }, requestId, budget).WaitAsync(CaseTimeout, budget);
            var longStarted = await WaitForCaptureAsync(output, longBoundary,
                text => HarnessContractChecks.HasRuntimeMarker(longSource, "A10_RECOVERY_WORKLOAD_STARTED", text), InteractiveTimeout, budget);
            Require(!longStarted.Truncated && HarnessContractChecks.HasRuntimeMarker(longSource, "A10_RECOVERY_WORKLOAD_STARTED", longStarted.Text),
                "A10 recovery workload start was not observed.");
            var interrupt = await service.InterruptOrRestartAsync(cancellationToken: budget).WaitAsync(TimeSpan.FromSeconds(25), budget);
            await recoveryPrompt.WaitAsync(InteractiveTimeout, budget);
            Require(interrupt.InteractiveReadinessObserved && interrupt.SignalDelivered && !interrupt.SessionRestarted &&
                    service.ProcessId == pid && service.SessionGeneration == generation,
                $"A10 interrupt did not recover in-place: outcome={interrupt.Outcome}; pid={service.ProcessId}; generation={service.SessionGeneration}.");
            var stateBoundary = output.Boundary;
            await RunConsoleAndWaitAsync(service, output, verifySource, "A10_FUNCTION_OK", generation, budget,
                additionalMarker: "A10_VARIABLE_OK", boundaryOverride: stateBoundary);

            const string errorSource = "throw ('A10_'+'CONTROLLED_TERMINATING_ERROR')";
            var errorBoundary = output.Boundary;
            var errorPrompt = ObserveNextPromptAsync(service, generation);
            var errorId = Guid.NewGuid();
            var errorResult = await service.ExecuteConsoleCommandAsync(errorSource, _ => { }, errorId, budget).WaitAsync(CaseTimeout, budget);
            var errorOutput = await WaitForCaptureAsync(output, errorBoundary,
                text => text.Contains("A10_CONTROLLED_TERMINATING_ERROR", StringComparison.Ordinal), InteractiveTimeout, budget);
            await errorPrompt.WaitAsync(InteractiveTimeout, budget);
            Require(errorResult.RequestId == errorId && !errorOutput.Truncated && errorOutput.Text.Contains("A10_CONTROLLED_TERMINATING_ERROR", StringComparison.Ordinal),
                "A10 controlled terminating error was not surfaced in terminal output.");
            await RunConsoleAndWaitAsync(service, output, "Write-Output ('A10_'+'AFTER_ERROR_OK')", "A10_AFTER_ERROR_OK", generation, budget);

            var repeated = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var expected = $"A10_REPEAT_{i}_{Guid.NewGuid():N}";
                var source = $"Write-Output ('A10_REPEAT_' + '{i}_' + '{expected.Split('_').Last()}')";
                var boundary = output.Boundary;
                var promptWait = ObserveNextPromptAsync(service, generation);
                var id = Guid.NewGuid();
                var submitted = await service.ExecuteConsoleCommandAsync(source, _ => { }, id, budget).WaitAsync(CaseTimeout, budget);
                var capture = await WaitForCaptureAsync(output, boundary,
                    text => HarnessContractChecks.HasRuntimeMarker(source, expected, text), InteractiveTimeout, budget);
                await promptWait.WaitAsync(InteractiveTimeout, budget);
                Require(submitted.RequestId == id && !capture.Truncated && HarnessContractChecks.HasRuntimeMarker(source, expected, capture.Text),
                    $"A10 repeated request {i + 1} lacked its unique output marker.");
                repeated.Add(id.ToString());
            }
            Require(service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation,
                $"A10 continuity failed: PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            result.Tests.Add(Timed(Pass("A10", "Recovery, state continuity, error handling, repeated execution", "Production integration",
                $"Variable/function survived Ctrl+C; terminating error surfaced and was followed by successful requests; three uniquely identified commands completed. PID={pid}; generation={generation}; requestIds={string.Join(',', repeated)}."), a10));
        }
        catch (Exception ex)
        {
            safe = await RecoverAfterInteractiveFailureAsync(service, generation, result, "A10", ex);
            result.Tests.Add(Timed(Fail("A10", "Recovery, state continuity, error handling, repeated execution", ex), a10));
        }

        var identityTimer = Stopwatch.StartNew();
        result.Tests.Add(Timed(service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation
            ? Pass("A10-CONTINUITY", "Post-interaction process and generation continuity", "Production integration", $"PID={pid}; generation={generation} preserved through interactive input, interruption, and error recovery.")
            : Fail("A10-CONTINUITY", "Post-interaction process and generation continuity", $"PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}; running={service.IsSessionRunning}."), identityTimer));
        return safe;
    }

    private static Task<(int Generation, string Prompt)> ObserveNextPromptAsync(PsesOwnedInteractiveSessionService service, int generation)
    {
        var completion = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, string>? handler = null;
        handler = (observedGeneration, prompt) =>
        {
            if (observedGeneration == generation) completion.TrySetResult((observedGeneration, prompt));
        };
        service.PromptReadyObserved += handler;
        _ = completion.Task.ContinueWith(_ => service.PromptReadyObserved -= handler, TaskScheduler.Default);
        return completion.Task;
    }

    private static async Task RunConsoleAndWaitAsync(PsesOwnedInteractiveSessionService service, SequencedOutputCapture output,
        string source, string marker, int generation, CancellationToken budget, string? additionalMarker = null, long? boundaryOverride = null)
    {
        var boundary = boundaryOverride ?? output.Boundary;
        var prompt = ObserveNextPromptAsync(service, generation);
        var id = Guid.NewGuid();
        var submitted = await service.ExecuteConsoleCommandAsync(source, _ => { }, id, budget).WaitAsync(CaseTimeout, budget);
        var capture = await WaitForCaptureAsync(output, boundary,
            text => HarnessContractChecks.HasRuntimeMarker(source, marker, text) &&
                    (additionalMarker is null || HarnessContractChecks.HasRuntimeMarker(source, additionalMarker, text)), InteractiveTimeout, budget);
        await prompt.WaitAsync(InteractiveTimeout, budget);
        Require(submitted.RequestId == id && submitted.SessionGeneration == generation && !capture.Truncated &&
                HarnessContractChecks.HasRuntimeMarker(source, marker, capture.Text) &&
                (additionalMarker is null || HarnessContractChecks.HasRuntimeMarker(source, additionalMarker, capture.Text)),
            $"Console request {id} failed to produce independent marker(s): {capture.Text}");
    }

    private static async Task<bool> RecoverAfterInteractiveFailureAsync(PsesOwnedInteractiveSessionService service, int generation,
        RunResult result, string testId, Exception cause)
    {
        try
        {
            if (!service.IsSessionRunning || service.SessionGeneration != generation) return false;
            var recovery = await service.InterruptOrRestartAsync().WaitAsync(TimeSpan.FromSeconds(25));
            var recovered = recovery.InteractiveReadinessObserved && !recovery.SessionRestarted &&
                            service.IsSessionRunning && service.SessionGeneration == generation;
            result.Notes.Add($"{testId} bounded failure cleanup: outcome={recovery.Outcome}; recoveredSameSession={recovered}; cause={ShortException(cause)}");
            return recovered;
        }
        catch (Exception cleanupError)
        {
            result.Notes.Add($"{testId} bounded failure cleanup failed: {ShortException(cleanupError)}; original={ShortException(cause)}");
            return false;
        }
    }

    private static string Redact(string value, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }

    private static async Task RunT15Async(RunResult result, PsesOwnedInteractiveSessionService service, SequencedOutputCapture rawOutput, CancellationToken budget)
    {
        var b07Timer = Stopwatch.StartNew();
        var beforePid = service.ProcessId;
        var beforeGeneration = service.SessionGeneration;
        var marker = "T15_FUNCTION_BODY_OK";
        var defineSource = "function A02T15OrdinaryFunction { [string]::Concat([char]84,[char]49,[char]53,[char]95,[char]70,[char]85,[char]78,[char]67,[char]84,[char]73,[char]79,[char]78,[char]95,[char]66,[char]79,[char]68,[char]89,[char]95,[char]79,[char]75) }";
        var before = rawOutput.Boundary;
        var output = new ConcurrentQueue<string>();
        try
        {
            var selected = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), beforeGeneration,
                "A02 T15 Run Selection", defineSource, true, r => output.Enqueue(r.Text),
                CancellationToken.None, new EditorExecutionSourceInfo(Guid.NewGuid(), 1, 1, defineSource.Length), EditorExecutionMode.RunSelection)
                .WaitAsync(CaseTimeout, budget);
            var verifyOutput = new ConcurrentQueue<string>();
            const string verifySource = "if (Get-Command A02T15OrdinaryFunction -ErrorAction SilentlyContinue) { A02T15OrdinaryFunction } else { 'T15_FUNCTION_MISSING' }";
            var verifyBoundary = rawOutput.Boundary;
            var verify = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "A02 T15 function persistence verification", verifySource,
                true, r => verifyOutput.Enqueue(r.Text)).WaitAsync(CaseTimeout, budget);
            var evidence = rawOutput.SnapshotAfter(verifyBoundary);
            var functionPersisted = !evidence.Truncated && HarnessContractChecks.HasRuntimeMarker(verifySource, marker, evidence.Text);
            var stateOk = selected.Status == EditorExecutionStatus.Completed && verify.Status == EditorExecutionStatus.Completed &&
                          selected.CompletionObserved && verify.CompletionObserved &&
                          beforePid == service.ProcessId && beforeGeneration == service.SessionGeneration;
            if (stateOk && functionPersisted)
                result.Tests.Add(Timed(Pass("B07", "Ordinary function persists from Run Selection", "Production integration", "Independent request invoked ordinary function; same PID/generation. Production PSES service route only; WPF GUI remains untested."), b07Timer));
            else
                result.Tests.Add(new TestResult("B07", "Ordinary function persists from Run Selection", "Production integration",
                    "Selection defines ordinary function visible to a later console/editor request in same session", evidence.Text.Replace(Environment.NewLine, " ").Trim(), "FAIL",
                    $"Mode=RunSelection; native route; selectedStatus={selected.Status}; selectedCompletion={selected.CompletionObserved}; verifyStatus={verify.Status}; verifyCompletion={verify.CompletionObserved}; PID {beforePid}->{service.ProcessId}; generation {beforeGeneration}->{service.SessionGeneration}; functionPersisted={functionPersisted}; verifyOutput={evidence.Text}.", DurationSeconds: Math.Round(b07Timer.Elapsed.TotalSeconds, 3)));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B07", "Ordinary function persists from Run Selection", ex), b07Timer)); }
    }

    private static async Task RunCoordinatorRunSelectionBoundaryAsync(
        RunResult result, PsesOwnedInteractiveSessionService service, SequencedOutputCapture rawOutput, CancellationToken budget)
    {
        var timer = Stopwatch.StartNew();
        var pid = service.ProcessId;
        var generation = service.SessionGeneration;
        var featureGate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, featureGate);
        var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured },
            featureGate,
            shadowMode: false,
            ExecutionBackendAvailability.Available,
            new PowerShellExecutionRequestClassifier(),
            new ExperimentalRunSelectionBridgeFeatureGate(result.DirectRunSelectionEnabled));

        try
        {
            const string source = "$A02GuiBoundaryValue='T01_VARIABLE_PERSISTED'; $dynamicCommand='Write-Host'; & $dynamicCommand 'T01_SELECTION_EXECUTED'; @('PIPELINE_OK') | ForEach-Object { Write-Output $_ }; for ($index=0; $index -lt 1; $index++) { Write-Output 'LOOP_OK' }; function A02GuiBoundaryFunction { 'GUI_BOUNDARY_FUNCTION_OK' }";
            var requestId = Guid.NewGuid();
            var request = new EditorExecutionRequest(
                requestId,
                generation,
                EditorExecutionMode.RunSelection,
                "A02 GUI-boundary selection",
                source,
                WorkingDirectory: service.CurrentWorkingDirectory,
                ExecuteInCurrentScope: true,
                CapabilityRequirements: new ExecutionCapabilityRequirements(
                    RequiresCurrentScope: true,
                    RequiresWorkingDirectoryContinuity: true),
                SourceInfo: new EditorExecutionSourceInfo(Guid.NewGuid(), 1, 0, source.Length));
            var routing = coordinator.Route(request);
            var executionTask = coordinator.ExecuteAsync(request, budget);
            var completionDuringBridgeTask = service.GetLiveCompletionsAsync(
                "untitled:ps7scriptdesk/bridge-r1-during.ps1", "Get-Pro", "Get-Pro".Length, budget);
            var execution = await executionTask.WaitAsync(CaseTimeout, budget);
            var completionDuringBridge = await completionDuringBridgeTask.WaitAsync(TimeSpan.FromSeconds(15), budget);
            if (execution.Status != EditorExecutionStatus.Completed)
                throw new InvalidOperationException($"Bridge Run Selection failed before console verification: status={execution.Status}; error={execution.ErrorMessage}; backend={routing.SelectedBackend}; classification={routing.Classification}; reason={routing.SelectionReason}.");
            var verifySource = "Write-Output ('VARIABLE: [' + $A02GuiBoundaryValue + ']'); if (Get-Command A02GuiBoundaryFunction -ErrorAction SilentlyContinue) { A02GuiBoundaryFunction } else { 'GUI_BOUNDARY_FUNCTION_MISSING' }; & { $A02GuiBoundaryNegative='CHILD_SCOPE_ONLY'; function A02GuiBoundaryNegativeFunction { 'CHILD_SCOPE_ONLY' } }; if ($null -eq (Get-Variable A02GuiBoundaryNegative -ErrorAction SilentlyContinue) -and $null -eq (Get-Command A02GuiBoundaryNegativeFunction -ErrorAction SilentlyContinue)) { Write-Output 'GUI_BOUNDARY_NEGATIVE_CONTROL_OK' } else { Write-Output 'GUI_BOUNDARY_NEGATIVE_CONTROL_LEAK' }; Write-Output ('GUI_BOUNDARY_'+'VERIFY_DONE')";
            var boundary = rawOutput.Boundary;
            await RunConsoleAndWaitAsync(service, rawOutput, verifySource,
                "VARIABLE: [T01_VARIABLE_PERSISTED]", generation, budget, "GUI_BOUNDARY_VERIFY_DONE");
            var verification = rawOutput.SnapshotAfter(boundary);
            Require(routing.Accepted && routing.SelectedBackend == EditorExecutionCoordinator.StructuredBackendId &&
                    routing.SelectionReason == "PsesSchedulerAdmission" &&
                    execution.Status == EditorExecutionStatus.Completed && execution.SessionGeneration == generation &&
                    execution.RequestId == requestId && verification.Text.Contains("GUI_BOUNDARY_FUNCTION_OK", StringComparison.Ordinal) &&
                    verification.Text.Contains("GUI_BOUNDARY_NEGATIVE_CONTROL_OK", StringComparison.Ordinal) &&
                    completionDuringBridge?.Items.Any(item => item.CompletionText.Contains("Get-Process", StringComparison.OrdinalIgnoreCase)) == true &&
                    !rawOutput.SnapshotAfter(0).Text.Contains(source, StringComparison.Ordinal) &&
                    service.IsSessionRunning && service.ProcessId == pid && service.SessionGeneration == generation,
                $"Coordinator Run Selection boundary failed. backend={routing.SelectedBackend}; classification={routing.Classification}; status={execution.Status}; variable/function output={verification.Text}; PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            result.Tests.Add(Timed(Pass("B16", "Coordinator-to-PSES Run Selection scope boundary", "Production integration",
                $"Coordinator Run Selection used the authenticated bridge; request source stayed out of terminal input/output/history; shared variable/function persisted; child-scope control remained isolated; an LSP Get-Process completion returned during the bridge request; PID={pid}; generation={generation}."), timer));
        }
        catch (Exception ex)
        {
            result.Tests.Add(Timed(Fail("B16", "Coordinator-to-PSES Run Selection scope boundary", ex), timer));
        }
    }

    private static async Task RunNativeSelectionScopeProbeAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture rawOutput, CancellationToken budget)
    {
        var timer = Stopwatch.StartNew();
        var evidence = Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_EVIDENCE")
            ?? throw new InvalidOperationException("Native scope probe requires the assigned evidence directory.");
        var snapshot = Path.Combine(evidence, "native-selection-probe.ps1");
        var pid = service.ProcessId;
        var generation = service.SessionGeneration;
        try
        {
            await File.WriteAllTextAsync(snapshot,
                "$A02NativeSelectionValue = 'NATIVE_VARIABLE_OK'\nfunction Test-A02NativeSelection { 'NATIVE_FUNCTION_OK' }\nWrite-Output 'NATIVE_SELECTION_DONE'\n", budget);
            var escaped = snapshot.Replace("'", "''", StringComparison.Ordinal);
            await RunConsoleAndWaitAsync(service, rawOutput, ". '" + escaped + "'", "NATIVE_SELECTION_DONE", generation, budget);
            var verify = "Write-Output ('A02_NATIVE_VALUE=' + $A02NativeSelectionValue); Test-A02NativeSelection";
            var boundary = rawOutput.Boundary;
            await RunConsoleAndWaitAsync(service, rawOutput, verify, "NATIVE_FUNCTION_OK", generation, budget);
            var text = rawOutput.SnapshotAfter(boundary).Text;
            Require(text.Contains("A02_NATIVE_VALUE=NATIVE_VARIABLE_OK", StringComparison.Ordinal) &&
                    text.Contains("NATIVE_FUNCTION_OK", StringComparison.Ordinal) &&
                    service.ProcessId == pid && service.SessionGeneration == generation,
                "Native dot-sourced selection did not preserve ordinary state or session identity.");
            result.Tests.Add(Timed(Pass("B08", "Native console dot-source scope probe", "Native console interaction",
                "Ordinary variable and function were visible to a later native console command; PID/generation unchanged."), timer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B08", "Native console dot-source scope probe", ex), timer)); }
        finally { try { File.Delete(snapshot); } catch { } }
    }

    private static async Task RunNativeSelectionParityAsync(RunResult result, PsesOwnedInteractiveSessionService service,
        SequencedOutputCapture rawOutput, CancellationToken budget)
    {
        async Task<AuthoritativeStructuredExecutionResult> SelectAsync(string source)
            => await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                "A02 native selection parity", source, true, _ => { }, budget,
                new EditorExecutionSourceInfo(Guid.NewGuid(), 1, 0, source.Length), EditorExecutionMode.RunSelection)
                .WaitAsync(CaseTimeout, budget);

        var stateTimer = Stopwatch.StartNew();
        try
        {
            var pid = service.ProcessId;
            var generation = service.SessionGeneration;
            await RunConsoleAndWaitAsync(service, rawOutput,
                "$A02ExistingValue='INITIAL'; function Test-A02Existing { 'ORIGINAL' }; Write-Output ('B09_'+'SETUP_OK')",
                "B09_SETUP_OK", generation, budget);
            var first = await SelectAsync("$A02ExistingValue='MODIFIED'; $A02SelectionArray=@(2,3,5); $A02SelectionMap=@{key='MAP_OK'}; function Test-A02Existing { 'REDEFINED' }; function Test-A02New { 'NEW_OK' }");
            var second = await SelectAsync("$A02ExistingValue += '_TWICE'; $A02SelectionArray += 7");
            var verify = "Write-Output ('B09_VALUE=' + $A02ExistingValue); Write-Output ('B09_ARRAY=' + ($A02SelectionArray -join ',')); Write-Output ('B09_MAP=' + $A02SelectionMap.key); Test-A02Existing; Test-A02New";
            var boundary = rawOutput.Boundary;
            await RunConsoleAndWaitAsync(service, rawOutput, verify, "B09_VALUE=MODIFIED_TWICE", generation, budget);
            var text = rawOutput.SnapshotAfter(boundary).Text;
            Require(first.Status == EditorExecutionStatus.Completed && second.Status == EditorExecutionStatus.Completed &&
                    first.CompletionObserved && second.CompletionObserved && pid == service.ProcessId && generation == service.SessionGeneration &&
                    text.Contains("B09_ARRAY=2,3,5,7", StringComparison.Ordinal) && text.Contains("B09_MAP=MAP_OK", StringComparison.Ordinal) &&
                    text.Contains("REDEFINED", StringComparison.Ordinal) && text.Contains("NEW_OK", StringComparison.Ordinal),
                "Native console did not observe sequential selection variable/function state or identity continuity.");
            result.Tests.Add(Timed(Pass("B09", "Combined selection state and redefinition", "Native console interaction",
                "Two selections modified console state; native console observed scalar, array, map, new/redefined functions, PID/generation continuity."), stateTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B09", "Combined selection state and redefinition", ex), stateTimer)); }

        var fileTimer = Stopwatch.StartNew();
        try
        {
            var pid = service.ProcessId;
            var generation = service.SessionGeneration;
            var source = "$A02RunFileOnly='FILE_ONLY'; $global:A02RunFileGlobal='GLOBAL_OK'; Write-Output ('B10_'+'FILE_DONE')";
            var file = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), generation,
                "A02 Run File isolation", source, false, _ => { }, budget,
                mode: EditorExecutionMode.ScriptCall).WaitAsync(CaseTimeout, budget);
            var verify = "if ($null -eq (Get-Variable A02RunFileOnly -ErrorAction SilentlyContinue)) { Write-Output ('B10_'+'LOCAL_ABSENT') }; Write-Output ('B10_GLOBAL=' + $global:A02RunFileGlobal)";
            var boundary = rawOutput.Boundary;
            await RunConsoleAndWaitAsync(service, rawOutput, verify, "B10_LOCAL_ABSENT", generation, budget,
                additionalMarker: "B10_GLOBAL=GLOBAL_OK");
            var text = rawOutput.SnapshotAfter(boundary).Text;
            Require(file.Status == EditorExecutionStatus.Completed && file.CompletionObserved &&
                    text.Contains("B10_GLOBAL=GLOBAL_OK", StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                $"Run File script scope or explicit global persistence differed from expected native console result. status={file.Status}; completion={file.CompletionObserved}; error={file.ErrorMessage}; output={text}; PID {pid}->{service.ProcessId}; generation {generation}->{service.SessionGeneration}.");
            result.Tests.Add(Timed(Pass("B10", "Run File script scope isolation", "Native console interaction",
                "Native console saw no ordinary Run File variable, saw explicit global variable, and retained PID/generation."), fileTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B10", "Run File script scope isolation", ex), fileTimer)); }

        var fallbackTimer = Stopwatch.StartNew();
        try
        {
            var generation = service.SessionGeneration;
            var pid = service.ProcessId;
            var source = "$A02FallbackValue='FALLBACK_OK'; function Test-A02Fallback { 'FALLBACK_FUNCTION_OK' }";
            var fallback = await service.ExecuteScriptForRequestAsync(Guid.NewGuid(), "A02 legacy selection fallback",
                source, _ => { }, true, budget).WaitAsync(CaseTimeout, budget);
            var verify = "Write-Output ('B14_VALUE=' + $A02FallbackValue); Test-A02Fallback";
            var boundary = rawOutput.Boundary;
            await RunConsoleAndWaitAsync(service, rawOutput, verify, "B14_VALUE=FALLBACK_OK", generation, budget);
            var text = rawOutput.SnapshotAfter(boundary).Text;
            Require(fallback.Outcome == LiveConsoleCommandOutcome.Completed &&
                    text.Contains("FALLBACK_FUNCTION_OK", StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                "Legacy selection fallback did not preserve ordinary state through native console input.");
            result.Tests.Add(Timed(Pass("B14", "Legacy selection fallback scope parity", "Production-service integration",
                "Legacy service dispatch used the same native selection route; native console observed variable/function state."), fallbackTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B14", "Legacy selection fallback scope parity", ex), fallbackTimer)); }

        var errorTimer = Stopwatch.StartNew();
        try
        {
            var generation = service.SessionGeneration;
            var pid = service.ProcessId;
            var source = "Write-Error ('B15_'+'NONTERMINATING_ERROR') -ErrorAction Continue; Write-Output ('B15_'+'AFTER_ERROR')";
            var boundary = rawOutput.Boundary;
            var selected = await SelectAsync(source);
            var text = rawOutput.SnapshotAfter(boundary).Text;
            Require(selected.Status == EditorExecutionStatus.Completed && selected.CompletionObserved &&
                    text.Contains("B15_NONTERMINATING_ERROR", StringComparison.Ordinal) &&
                    text.Contains("B15_AFTER_ERROR", StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                "Run Selection did not preserve native nonterminating error/output behavior.");
            result.Tests.Add(Timed(Pass("B15", "Run Selection native error stream", "Native console interaction",
                "Nonterminating error and subsequent output were visible; PID/generation unchanged."), errorTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B15", "Run Selection native error stream", ex), errorTimer)); }
    }

    private static async Task RunNativeSelectionInteractiveParityAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture output, SequencedOutputCapture visibleOutput,
        CancellationToken budget, List<string> redactions)
    {
        var generation = service.SessionGeneration;
        var pid = service.ProcessId;
        var featureGate = new EditorExecutionFeatureGate(structuredExecutionEnabled: true);
        var adapter = new AuthoritativeSessionStructuredExecutionAdapter(service, featureGate);
        var structured = new AuthoritativeSessionStructuredExecutionBackend(adapter);
        var legacy = new LegacyLiveConsoleExecutionBackend(service);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, featureGate, shadowMode: false,
            ExecutionBackendAvailability.Available, new PowerShellExecutionRequestClassifier());
        async Task<EditorExecutionResult> SelectAsync(string source, CancellationToken token)
        {
            var id = Guid.NewGuid();
            var request = new EditorExecutionRequest(id, generation, EditorExecutionMode.RunSelection,
                "A02 interactive Run Selection", source,
                WorkingDirectory: service.CurrentWorkingDirectory,
                ExecuteInCurrentScope: true,
                CapabilityRequirements: new ExecutionCapabilityRequirements(RequiresInteractiveInput: true, RequiresCurrentScope: true,
                    RequiresWorkingDirectoryContinuity: true),
                SourceInfo: new EditorExecutionSourceInfo(Guid.NewGuid(), 1, 0, source.Length));
            var route = coordinator.Route(request);
            Require(route.Accepted && route.SelectedBackend == EditorExecutionCoordinator.LegacyBackendId,
                $"Interactive Run Selection did not route through the native interactive backend: {route.SelectedBackend}/{route.SelectionReason}.");
            return await coordinator.ExecuteAsync(request, token).WaitAsync(CaseTimeout, budget);
        }

        var inputTimer = Stopwatch.StartNew();
        try
        {
            await RunConsoleAndWaitAsync(service, output, "Write-Output ('B11_'+'USER_HISTORY_SENTINEL')",
                "B11_USER_HISTORY_SENTINEL", generation, budget);
            var prompt = "B11_INPUT_" + Guid.NewGuid().ToString("N");
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt));
            var source = $"$A02SelectionInput=Read-Host -Prompt ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'))); Write-Output ('B11_'+'INPUT_DONE')";
            var boundary = output.Boundary;
            var visibleBoundary = visibleOutput.Boundary;
            var task = SelectAsync(source, budget);
            var seen = await WaitForCaptureAsync(output, boundary, text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(!seen.Truncated && seen.Text.Contains(prompt, StringComparison.Ordinal), "Selection Read-Host prompt was absent.");
            await service.WriteRawInputAsync("INPUT_OK\r", budget).WaitAsync(InteractiveTimeout, budget);
            var selected = await task;
            var visibleCapture = visibleOutput.SnapshotAfter(visibleBoundary);
            var visible = visibleCapture.Text;
            var internalCommandVisible = visible.Contains("WriteAllText", StringComparison.Ordinal) || visible.Contains("selection-", StringComparison.Ordinal);
            var verify = "Write-Output ('B11_VALUE=' + $A02SelectionInput)";
            var checkBoundary = output.Boundary;
            await RunConsoleAndWaitAsync(service, output, verify, "B11_VALUE=INPUT_OK", generation, budget);
            Require(selected.Status == EditorExecutionStatus.Completed && coordinator.ActiveExecution is null &&
                    pid == service.ProcessId && generation == service.SessionGeneration &&
                    visible.Contains(prompt, StringComparison.Ordinal) && visible.Contains("B11_INPUT_DONE", StringComparison.Ordinal) &&
                    output.SnapshotAfter(checkBoundary).Text.Contains("B11_VALUE=INPUT_OK", StringComparison.Ordinal),
                $"Coordinator Run Selection Read-Host input/persistence failed. status={selected.Status}; visibleLength={visible.Length}; output={output.SnapshotAfter(boundary).Text}");
            result.Tests.Add(Timed(Pass("B11", "Run Selection Read-Host and console state", "Native console interaction",
                "Selection accepted native input; later console command read the result in the same PID/generation."), inputTimer));
            var echoTimer = Stopwatch.StartNew();
            result.Tests.Add(Timed(new TestResult("ECHO-01", "Native Run Selection internal invocation visibility", "Production integration",
                "The actual coordinator output callback omits the request-owned invocation while preserving prompts and selected output.",
                internalCommandVisible ? "Request-owned invocation text reached the actual output callback." : "Request-owned invocation was absent from the actual output callback.",
                internalCommandVisible ? "FAIL" : "PASS",
                $"visibleLength={visible.Length}; wrapperVisible={internalCommandVisible}; inputPromptPreserved={visible.Contains(prompt, StringComparison.Ordinal)}; scriptOutputPreserved={visible.Contains("B11_INPUT_DONE", StringComparison.Ordinal)}; rawTerminalCaptureContainsWrapper={output.SnapshotAfter(boundary).Text.Contains("selection-", StringComparison.Ordinal)}.",
                Math.Round(echoTimer.Elapsed.TotalSeconds, 3)), echoTimer));
            const string historyCheck = "& { $handler=(Get-PSReadLineOption -ErrorAction Stop).AddToHistoryHandler; $guard=($null -ne $handler -and $handler.ToString().Contains('PS7SD-INTERNAL-HISTORY-GUARD')); $h=@(Get-History -Count 128 | Where-Object { $_.CommandLine -match '^\\$__ps7sd_selection_success_[0-9a-f]{32} = \\$true; try \\{ \\. ''[^'']*selection-[0-9a-f]{32}\\.ps1'';' }); $legit=@(Get-History -Count 128 | Where-Object { $_.CommandLine -like '*B11_USER_HISTORY_SENTINEL*' }); if ($h.Count -eq 0 -and $guard -and $legit.Count -gt 0) { $result='CLEAN' } else { $result='CONTAMINATED;count=' + $h.Count + ';guard=' + $guard + ';legitimate=' + $legit.Count }; Write-Output ('B11_HISTORY_'+'RESULT=' + $result) }";
            var historyTimer = Stopwatch.StartNew();
            var historyBoundary = output.Boundary;
            await RunConsoleAndWaitAsync(service, output, historyCheck, "B11_HISTORY_RESULT=", generation, budget);
            var historyText = output.SnapshotAfter(historyBoundary).Text;
            var historyClean = historyText.Contains("B11_HISTORY_RESULT=CLEAN", StringComparison.Ordinal);
            result.Tests.Add(Timed(new TestResult("ECHO-HISTORY-01", "Native Run Selection PSReadLine history", "Production integration",
                "The internal Run Selection wrapper is excluded from session history without deleting or disabling user history.",
                historyClean ? "No request-owned Run Selection wrapper was present in the inspected history window." : historyText.Trim(),
                historyClean ? "PASS" : "FAIL", "Read-only Get-History inspection after a coordinator Run Selection.",
                Math.Round(historyTimer.Elapsed.TotalSeconds, 3)), historyTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B11", "Run Selection Read-Host and console state", ex), inputTimer)); return; }

        var secureTimer = Stopwatch.StartNew();
        try
        {
            var secret = "B12-" + Guid.NewGuid().ToString("N");
            redactions.Add(secret);
            var prompt = "B12_SECURE_" + Guid.NewGuid().ToString("N");
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt));
            var source = $"$A02SelectionSecure=Read-Host -AsSecureString -Prompt ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'))); if ($A02SelectionSecure.Length -eq {secret.Length}) {{ Write-Output ('B12_'+'SECURE_OK') }}";
            var boundary = output.Boundary;
            var task = SelectAsync(source, budget);
            var seen = await WaitForCaptureAsync(output, boundary, text => text.Contains(prompt, StringComparison.Ordinal), InteractiveTimeout, budget);
            Require(!seen.Truncated && seen.Text.Contains(prompt, StringComparison.Ordinal), "Selection secure Read-Host prompt was absent.");
            await service.WriteRawInputAsync(secret + "\r", budget).WaitAsync(InteractiveTimeout, budget);
            var selected = await task;
            var text = output.SnapshotAfter(boundary).Text;
            Require(selected.Status == EditorExecutionStatus.Completed && coordinator.ActiveExecution is null &&
                    text.Contains("B12_SECURE_OK", StringComparison.Ordinal) && !text.Contains(secret, StringComparison.Ordinal) &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                "Secure Run Selection input or same-session continuity failed.");
            result.Tests.Add(Timed(Pass("B12", "Secure Run Selection Read-Host", "Native console interaction",
                "SecureString length confirmed; secret absent from output; PID/generation unchanged."), secureTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B12", "Secure Run Selection Read-Host", ex), secureTimer)); return; }

        var cancelTimer = Stopwatch.StartNew();
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(budget);
            var source = "Write-Output ('B13_'+'STARTED'); while ($true) { Start-Sleep -Milliseconds 200 }";
            var boundary = output.Boundary;
            var task = SelectAsync(source, cancel.Token);
            var started = await WaitForCaptureAsync(output, boundary,
                text => HarnessContractChecks.HasRuntimeMarker(source, "B13_STARTED", text), InteractiveTimeout, budget);
            Require(!started.Truncated && HarnessContractChecks.HasRuntimeMarker(source, "B13_STARTED", started.Text),
                "Selection cancellation workload did not start.");
            cancel.Cancel();
            var selected = await task.WaitAsync(TimeSpan.FromSeconds(25), budget);
            var verify = "Write-Output ('B13_'+'RECOVERED')";
            await RunConsoleAndWaitAsync(service, output, verify, "B13_RECOVERED", generation, budget);
            Require(selected.Status == EditorExecutionStatus.Cancelled && coordinator.ActiveExecution is null && !selected.RecoveryRequired &&
                    pid == service.ProcessId && generation == service.SessionGeneration,
                "Native Run Selection cancellation or prompt recovery was unverified.");
            result.Tests.Add(Timed(Pass("B13", "Run Selection cancellation and recovery", "Native console interaction",
                "Ctrl+C recovered prompt; next console command succeeded in the same PID/generation."), cancelTimer));
        }
        catch (Exception ex) { result.Tests.Add(Timed(Fail("B13", "Run Selection cancellation and recovery", ex), cancelTimer)); }
    }

    private static void AddBlockedPrerequisites(RunResult result, string reason)
    {
        result.Tests.Add(new TestResult("A01", "PSES-owned PowerShell session startup", "Production integration", "PowerShell 7 and PSES prerequisites are available and PSES starts within 45 seconds.", "Not run", "BLOCKED", reason));
        foreach (var id in new[] { "A02", "A03", "A04", "A05", "A06", "A07", "A08", "A09", "A10", "B07" })
            if (!result.Tests.Any(t => t.Id == id)) result.Tests.Add(new TestResult(id, NameFor(id), "Production integration", "Execute against isolated PSES session", "Not run", "BLOCKED", reason));
    }

    private static void AddNotRunAfterFatal(RunResult result, string reason)
    {
        foreach (var id in new[] { "A02", "A03", "A04", "A05", "A06", "B07" })
            if (!result.Tests.Any(t => t.Id == id))
                result.Tests.Add(new TestResult(id, NameFor(id), "Production integration", "Execute against isolated PSES session", "Not run", "FAIL", reason));
        foreach (var id in new[] { "A07", "A08", "A09", "A10" })
            if (!result.Tests.Any(t => t.Id == id))
                result.Tests.Add(new TestResult(id, NameFor(id), "Production integration", "Execute when implemented", "Not run", "BLOCKED", "Intentionally unimplemented; session startup failure was separately recorded as FAIL."));
    }

    private static async Task<AuthoritativeStructuredExecutionResult> RunStructuredAsync(
        PsesOwnedInteractiveSessionService service, string name, string source, CancellationToken cancellationToken)
        => await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration, name, source, true,
            _ => { }, cancellationToken).WaitAsync(CaseTimeout, cancellationToken);

    private static async Task RunRepeatedSelectionPerformanceAsync(RunResult result,
        PsesOwnedInteractiveSessionService service, SequencedOutputCapture rawOutput, CancellationToken cancellationToken)
    {
        const int warmupCount = 2;
        const int sampleCount = 7;
        var route = result.DirectRunSelectionEnabled ? "direct-lsp-run-selection" : "native-conpty-run-selection";
        var measuredProcessId = service.ProcessId ?? throw new InvalidOperationException("No PSES process ID is available for performance measurement.");
        var workloads = new (string Id, string Name, Func<int, string> Source)[]
        {
            ("PERF-SMALL", "small ordinary selection", i => $"$global:__PS7SDPerfSmall={i}; Write-Output 'PERF_SMALL_OK'"),
            ("PERF-FUNCTION", "function definition selection", i => $"function __PS7SDPerfFunction{i} {{ 'PERF_FUNCTION_OK' }}; __PS7SDPerfFunction{i}"),
            ("PERF-MULTILINE", "multiline selection", i => $"$global:__PS7SDPerfMulti{i} = @'\nalpha\nbeta\ngamma\n'@\nWrite-Output ('PERF_MULTILINE_' + $global:__PS7SDPerfMulti{i}.Length)"),
            ("PERF-LARGE", "larger selection", i => string.Join(Environment.NewLine, Enumerable.Range(0, 60).Select(n => $"Write-Output 'PERF_LARGE_{i}_{n}'"))),
            ("PERF-MIXED", "mixed output selection", i => $"Write-Output 'PERF_MIXED_{i}'; Write-Warning 'PERF_WARNING'; Write-Information 'PERF_INFORMATION' -InformationAction Continue; Write-Verbose 'PERF_VERBOSE' -Verbose")
        };

        var tempRoot = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "EditorExecution");
        static (int Files, long Bytes) SnapshotTemp(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return (0, 0);
                var files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToArray();
                return (files.Length, files.Sum(file => file.Length));
            }
            catch { return (-1, -1); }
        }

        var tempBefore = SnapshotTemp(tempRoot);
        long memoryBefore = 0;
        try { using var ps = Process.GetProcessById(measuredProcessId); memoryBefore = ps.WorkingSet64; } catch { }
        foreach (var workload in workloads)
        {
            var timings = new List<double>(sampleCount);
            var rawTerminalCharacters = 0;
            try
            {
                for (var i = 0; i < warmupCount + sampleCount; i++)
                {
                    var source = workload.Source(i);
                    var outputBoundary = rawOutput.Boundary;
                    var timer = Stopwatch.StartNew();
                    var execution = await service.ExecuteStructuredScriptAsync(Guid.NewGuid(), service.SessionGeneration,
                        "A02 repeated performance " + workload.Id, source, true,
                        _ => { },
                        cancellationToken, mode: EditorExecutionMode.RunSelection).WaitAsync(CaseTimeout, cancellationToken);
                    timer.Stop();
                    if (execution.Status != EditorExecutionStatus.Completed || !execution.CompletionObserved)
                        throw new InvalidOperationException($"{workload.Id} did not complete: {execution.Status}; {execution.ErrorMessage}");
                    rawTerminalCharacters += rawOutput.SnapshotAfter(outputBoundary).Text.Length;
                    if (i >= warmupCount) timings.Add(timer.Elapsed.TotalMilliseconds);
                }
                var ordered = timings.Order().ToArray();
                var p95 = ordered[Math.Clamp((int)Math.Ceiling(0.95 * ordered.Length) - 1, 0, ordered.Length - 1)];
                var detail = $"route={route}; PID={service.ProcessId}; generation={service.SessionGeneration}; warmups={warmupCount}; n={sampleCount}; p50={Percentile(ordered, 0.50):0.0}ms; p95={p95:0.0}ms; min={ordered[0]:0.0}ms; max={ordered[^1]:0.0}ms; rawTerminalCaptureCharacters={rawTerminalCharacters} across {warmupCount + sampleCount} requests.";
                result.Tests.Add(new TestResult(workload.Id, "Repeated selection performance: " + workload.Name, "Performance comparison",
                    "Matched current-scope Run Selection requests complete in one fresh PSES-owned session after explicit per-workload warmup.",
                    detail, "PASS", "Descriptive measurements only; sample count is small and control/candidate runner order was control then candidate.",
                    Math.Round(ordered.Average() / 1000, 3)));
            }
            catch (Exception ex)
            {
                result.Tests.Add(new TestResult(workload.Id, "Repeated selection performance: " + workload.Name, "Performance comparison",
                    "All bounded measured selection requests complete.", ShortException(ex), "FAIL",
                    $"route={route}; samplesCompleted={timings.Count}; session recovery was not presumed.", 0));
                break;
            }
        }
        var tempAfter = SnapshotTemp(tempRoot);
        long memoryAfter = 0;
        try { using var ps = Process.GetProcessById(measuredProcessId); memoryAfter = ps.WorkingSet64; } catch { }
        result.Notes.Add($"Performance route={route}; PID={service.ProcessId}; generation={service.SessionGeneration}; PowerShell working-set delta={memoryAfter - memoryBefore} bytes; EditorExecution temp files/bytes {tempBefore.Files}/{tempBefore.Bytes}->{tempAfter.Files}/{tempAfter.Bytes}; shared temp folder may include unrelated live work.");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static async Task BoundedProcessFallbackAsync(int? processId, DateTimeOffset? expectedStartUtc)
    {
        if (processId is null || expectedStartUtc is null) return;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited) return;
            if (Math.Abs((process.StartTime.ToUniversalTime() - expectedStartUtc.Value.UtcDateTime).TotalSeconds) > 2) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException) { }
    }

    private static async Task<bool> IsProcessExitedAsync(int processId, DateTimeOffset? expectedStartUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (expectedStartUtc is not null && Math.Abs((process.StartTime.ToUniversalTime() - expectedStartUtc.Value.UtcDateTime).TotalSeconds) > 2)
                return true;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (TimeoutException) { return false; }
    }

    private static string NameFor(string id) => id switch
    {
        "A02" => "Native console runtime output", "A03" => "Shared process/generation identity", "A04" => "Cross-request working-directory state",
        "A05" => "Native executable execution", "A06" => "PowerShell output/error streams", "A07" => "Interactive Read-Host",
        "A08" => "Secure Read-Host", "A09" => "Interrupt recovery", "A10" => "Session recovery", _ => "T15 Run Selection scope"
    };

    private static TestResult Pass(string id, string name, string category, string actual) => new(id, name, category, ExpectedFor(id), actual, "PASS", null);
    private static TestResult Timed(TestResult result, Stopwatch timer)
    {
        timer.Stop();
        return result with { DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 3) };
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<(string Text, bool Truncated, long LastSequence)> WaitForCaptureAsync(SequencedOutputCapture output, long boundary,
        Func<string, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = output.SnapshotAfter(boundary);
            if (predicate(capture.Text)) return capture;
            try { await output.WaitForChangeAsync(capture.LastSequence, timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return output.SnapshotAfter(boundary); }
        }
    }
    private static TestResult Fail(string id, string name, Exception ex) => Fail(id, name, ShortException(ex));
    private static TestResult Fail(string id, string name, string actual) => new(id, name, "Production integration", ExpectedFor(id), actual, "FAIL", actual);
    private static string ExpectedFor(string id) => id switch
    {
        "A01" => "PSES starts within 45 seconds and exposes a live session, PID, and generation.",
        "A02" => "A native console command emits runtime output; command completion and prompt readiness are not asserted.",
        "A03" => "The production session PID and generation remain stable across requests.",
        "A04" => "The working directory persists across separate requests, process/generation continuity holds, and restoration is independently confirmed.",
        "A05" => "A harmless Windows executable emits its marker and returns exit status 0.",
        "A06" => "Output, warning, nonterminating error, and caught exception are observable.",
        "B07" => "An ordinary function defined by Run Selection is visible to a later request in the same session.",
        _ => "The named production integration contract is satisfied."
    };
    private static string ShortException(Exception ex) => ex.GetType().Name + ": " + ex.Message;
    private static string? ResolvePwshPath() => Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH") is { Length: > 0 } configured ? configured : (File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe") ? @"C:\Program Files\PowerShell\7\pwsh.exe" : null);
    private static string? ResolvePsesRoot() => Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT") is { Length: > 0 } configured ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
    private static Version ParseMajorMinor(string? value) => Version.TryParse(value, out var version) ? new Version(version.Major, Math.Max(0, version.Minor)) : new Version(7, 0);

    private static async Task<string?> ReadPwshVersionAsync(string path, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(path, "-NoLogo -NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        try
        {
            process.Start(); var read = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(timeout);
            var text = await read;
            return process.ExitCode == 0 ? text.Trim() : null;
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } return null; }
    }
    private static string? ReadPsesVersion(string? root)
    {
        var manifest = root is null ? null : Path.Combine(root, "PowerShellEditorServices", "PowerShellEditorServices.psd1");
        if (manifest is null || !File.Exists(manifest)) return null;
        foreach (var line in File.ReadLines(manifest)) if (line.TrimStart().StartsWith("ModuleVersion", StringComparison.OrdinalIgnoreCase)) return line.Split('=', 2).Last().Trim().Trim('"', '\'');
        return null;
    }

    private static string RenderMarkdown(RunResult r) => "# A02 standalone acceptance run\n\n" +
        $"- Started: {r.StartedAt:O}\n- Completed: {r.CompletedAt:O}\n- PowerShell: {r.PowerShellVersion ?? "unavailable"}\n- PSES: {r.PsesVersion ?? "unavailable"}\n- Runtime: .NET {r.DotnetVersion}\n- Duration: {r.DurationSeconds}s\n- Cleanup: {r.Cleanup}\n\n" +
        $"- Direct Run Selection candidate enabled in this runner process: {r.DirectRunSelectionEnabled} (runner-only PS7SD_ACCEPTANCE_DIRECT_RUNSELECTION override; application default unchanged)\n\n" +
        "| ID | Test | Evidence level | Expected | Actual | Status | Duration (s) | Evidence | Detail |\n|---|---|---|---|---|---|---:|---|---|\n" + string.Join("\n", r.Tests.Select(t => $"| {Cell(t.Id)} | {Cell(t.Name)} | {Cell(t.Category)} | {Cell(t.Expected)} | {Cell(t.Actual)} | **{t.Status}** | {t.DurationSeconds:0.000} | {Cell(t.EvidenceReference)} | {Cell(t.Detail ?? "")} |")) + "\n";
    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static void PrintSummary(RunResult r)
    {
        Console.WriteLine("PS7 ScriptDesk — A02 Architecture Acceptance");
        Console.WriteLine($"Runtime: PowerShell {r.PowerShellVersion ?? "unavailable"} / PSES {r.PsesVersion ?? "unavailable"} / .NET {r.DotnetVersion}");
        var fast01 = string.Equals(Environment.GetEnvironmentVariable("PS7SD_ACCEPTANCE_FAST01"), "1", StringComparison.Ordinal);
        Console.WriteLine($"Mode: {(fast01 ? "A02-FAST-01 targeted parity" : "Core Production Integration")} ({(r.DirectRunSelectionEnabled ? "direct PSES Run Selection candidate" : "native ConPTY Run Selection control")})");
        foreach (var t in r.Tests) Console.WriteLine($"{t.Status,-8} {t.Id} {t.Name}");
        Console.WriteLine();
        Console.WriteLine($"Executed: {r.Tests.Count(t => t.Status is "PASS" or "FAIL")}");
        Console.WriteLine($"Passed:   {r.Tests.Count(t => t.Status == "PASS")}");
        Console.WriteLine($"Failed:   {r.Tests.Count(t => t.Status == "FAIL")}");
        Console.WriteLine($"Blocked:  {r.Tests.Count(t => t.Status == "BLOCKED")}");
        Console.WriteLine($"Manual:   {r.Tests.Count(t => t.Status == "MANUAL")}");
        if (r.RunnerError is not null) Console.WriteLine("Runner error: " + r.RunnerError);
        Console.WriteLine("Evidence: " + r.EvidenceDirectory);
    }

    private sealed class RunResult(DateTimeOffset startedAt, string dotnetVersion, string osVersion)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset? CompletedAt { get; set; }
        public string DotnetVersion { get; } = dotnetVersion;
        public string OsVersion { get; } = osVersion;
        public string? PowerShellPath { get; set; }
        public string? PowerShellVersion { get; set; }
        public string? PsesRoot { get; set; }
        public string? PsesVersion { get; set; }
        public int? ProcessId { get; set; }
        public int? SessionGeneration { get; set; }
        public double DurationSeconds { get; set; }
        public string? Cleanup { get; set; }
        public string? EvidenceDirectory { get; set; }
        public string? RunnerError { get; set; }
        public bool DirectRunSelectionEnabled { get; set; }
        public List<string> Notes { get; } = [];
        public List<TestResult> Tests { get; } = [];
    }
    private sealed record TestResult(string Id, string Name, string Category, string Expected, string Actual, string Status, string? Detail,
        double DurationSeconds = 0, string EvidenceReference = "results.json; runtime-output.log");
}
