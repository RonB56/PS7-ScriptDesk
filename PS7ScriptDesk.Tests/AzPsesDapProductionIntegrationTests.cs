using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class AzPsesDapProductionIntegrationTests
{
    [Fact(Timeout = 240_000)]
    public async Task AuthoritativePsesSession_PreservesMigratedDebuggerBehaviorAcrossDap()
    {
        var runtime = FindRuntime();
        var root = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        if (runtime is null || string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("AZ production PSES integration requires PS7SCRIPTDESK_PSES_ROOT and PowerShell 7.");

        var fixtureRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_AZ_FIXTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(fixtureRoot) || !Path.IsPathFullyQualified(fixtureRoot))
            throw new InvalidOperationException("AZ production integration requires a repository-local PS7SCRIPTDESK_AZ_FIXTURE_DIRECTORY.");
        Directory.CreateDirectory(fixtureRoot);
        var directory = Directory.CreateDirectory(Path.Combine(fixtureRoot, $"run-{Guid.NewGuid():N}"));
        var evidencePath = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_AZ_EVIDENCE_PATH");
        var output = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        var completedScenarios = new ConcurrentQueue<string>();
        var processId = 0;
        var sessionGeneration = 0;
        var debugServicePipePublished = false;
        var firstDebuggerSessionId = Guid.Empty;
        var firstPauseFrameCount = 0;
        var firstPauseVariableCount = 0;
        var stepOutObserved = false;
        var stepOverObserved = false;
        var secondDebuggerSessionId = Guid.Empty;
        var exceptionDebuggerSessionId = Guid.Empty;
        var shutdownObserved = false;
        var firstSessionCompletionObserved = false;
        var firstSessionTeardownObserved = false;
        var hostReadyAfterFirstSession = false;
        var postFirstSessionCommandObserved = false;
        var secondSessionStarted = false;
        var laterSessionAllowed = false;
        var ownershipAfterFirstTeardown = "unobserved";
        var fixtureEvidence = new ConcurrentQueue<object>();
        var unreachedScenarios = new ConcurrentQueue<string>();
        var terminalPromptObservationCount = 0;
        var promptObservationsBeforeCompletion = 0;

        using var service = new PsesOwnedInteractiveSessionService();
        try
        {
            await service.StartSessionAsync(runtime, record => output.Enqueue(record.Text), directory.FullName).WaitAsync(TimeSpan.FromSeconds(45));
            Assert.True(service.IsSessionRunning);
            Assert.NotNull(service.ProcessId);
            processId = service.ProcessId!.Value;
            sessionGeneration = service.SessionGeneration;
            debugServicePipePublished = !string.IsNullOrWhiteSpace(service.DebugServicePipeName);
            service.PromptReadyObserved += (generation, _) =>
            {
                if (generation == sessionGeneration) Interlocked.Increment(ref terminalPromptObservationCount);
            };

            await VerifyBreakpointInspectionSteppingAndCompletionAsync();
            if (laterSessionAllowed)
            {
                await VerifyHandledAndUnhandledExceptionsAsync();
                await VerifyStopEventContainmentAndHostContinuityAsync();
            }
            else
            {
                unreachedScenarios.Enqueue("handled/unhandled exception classification and explicit Stop were not run because the production endpoint did not establish eligibility for another DAP session after MainWindow teardown");
            }

            var shutdownSucceeded = await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
            shutdownObserved = shutdownSucceeded && !service.IsSessionRunning;
            Check(shutdownObserved, "authoritative PSES process shuts down cleanly after the bounded integration scenario");
            Assert.True(failures.IsEmpty, "Production DAP replacement contract failures:\n" + string.Join("\n", failures));
            Assert.Equal(laterSessionAllowed ? 4 : secondSessionStarted ? 2 : 1, completedScenarios.Count);
        }
        finally
        {
            if (!shutdownObserved)
            {
                try { await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            }
            if (!string.IsNullOrWhiteSpace(evidencePath))
            {
                File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
                {
                    test = nameof(AuthoritativePsesSession_PreservesMigratedDebuggerBehaviorAcrossDap),
                    passed = failures.IsEmpty && completedScenarios.Count == (laterSessionAllowed ? 4 : secondSessionStarted ? 2 : 1),
                    scenariosCompleted = completedScenarios.ToArray(),
                    contractFailures = failures.ToArray(),
                    psesRoot = root,
                    powershellVersion = runtime.VersionText,
                    processId,
                    sessionGeneration,
                    debugServicePipePublished,
                    firstDebuggerSessionId,
                    firstPauseFrameCount,
                    firstPauseVariableCount,
                    stepOutObserved,
                    stepOverObserved,
                    secondDebuggerSessionId,
                    exceptionDebuggerSessionId,
                    firstSessionCompletionObserved,
                    firstSessionTeardownObserved,
                    hostReadyAfterFirstSession,
                    postFirstSessionCommandObserved,
                    secondSessionStarted,
                    laterSessionAllowed,
                    ownershipAfterFirstTeardown,
                    terminalPromptObservationCount,
                    promptObservationsBeforeCompletion,
                    fixtureEvidence = fixtureEvidence.ToArray(),
                    unreachedScenarios = unreachedScenarios.ToArray(),
                    postDebugHostOutputObserved = TerminalContainsMarkerLine(output, "AZ_POST_DEBUG|HOST_CONTINUITY"),
                    processRunningAfterShutdown = service.IsSessionRunning,
                    shutdownObserved
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            try { directory.Delete(recursive: true); } catch { }
        }

        async Task VerifyBreakpointInspectionSteppingAndCompletionAsync()
        {
            var script = Path.Combine(directory.FullName, "nested-debug.ps1");
            var lines = new[]
            {
                "function Outer([string]$ParamOuter) {",       // 1
                "    $LocalOuter = 'OUTER'",                   // 2
                "    $ArrayOuter = @(4, 5, 6)",                // 3
                "    $ObjectOuter = [pscustomobject]@{ Name = 'OUTER_OBJ' }", // 4
                "    Inner 'PARAM-INNER'",                     // 5
                "    $AfterInner = 'AFTER'",                   // 6
                "}",                                           // 7
                "function Inner([string]$ParamInner) {",       // 8
                "    $LocalInner = 'INNER'",                   // 9
                "    $UniqueInner = 'INNER_ONLY'",              // 10
                "    $ArrayInner = @(1, 2, 3)",                // 11
                "    $ObjectInner = [pscustomobject]@{ Name = 'INNER_OBJ' }", // 12
                "    $BreakHere = 'BREAK'",                     // 13
                "    Write-Output '__PSS_DEBUG_SESSION_ENDED__ USER_MARKER'", // 14
                "}",                                           // 15
                "Outer 'PARAM-OUTER'"                           // 16
            };
            await File.WriteAllTextAsync(script, string.Join(Environment.NewLine, lines) + Environment.NewLine);

            using var debugger = new PsesDapDebugSession(service);
            firstDebuggerSessionId = debugger.SessionId;
            var recorder = new EventRecorder(debugger).Attach();
            var stateObserverCalls = 0;
            var typedObserverCalls = 0;
            debugger.StateChanged += _ => throw new InvalidOperationException("synthetic state subscriber failure");
            debugger.StateChanged += _ => Interlocked.Increment(ref stateObserverCalls);
            debugger.TypedEventReceived += _ => throw new InvalidOperationException("synthetic typed-event subscriber failure");
            debugger.TypedEventReceived += _ => Interlocked.Increment(ref typedObserverCalls);

            await debugger.StartAsync(runtime, script, new[] { new DebugBreakpointInfo(script, 14) });
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
            Check(debugger.CurrentPauseReason == DebuggerPauseReason.Breakpoint, "breakpoint pause reason is preserved");
            var frames = await debugger.GetCallStackAsync().WaitAsync(TimeSpan.FromSeconds(15));
            firstPauseFrameCount = frames.Count;
            var inner = frames.FirstOrDefault(frame => frame.FunctionName == "Inner" &&
                Path.GetFullPath(frame.ScriptName).Equals(Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase) && frame.LineNumber == 14);
            var outer = frames.FirstOrDefault(frame => frame.FunctionName == "Outer" &&
                Path.GetFullPath(frame.ScriptName).Equals(Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase));
            Check(inner is not null && inner.IsCurrentFrame && !string.IsNullOrWhiteSpace(inner.ProviderFrameId) && !string.IsNullOrWhiteSpace(inner.FrameId),
                "nested current frame is identified by provider identity and fixture source location");
            Check(outer is not null && !string.IsNullOrWhiteSpace(outer.ProviderFrameId) && !string.IsNullOrWhiteSpace(outer.FrameId),
                "selected caller frame is identified by provider identity and fixture source location");
            Check(inner is not null && inner.LineNumber == 14,
                "breakpoint reports the exact portable source location after fixture initialization");

            DebuggerVariableInspectionResult? innerVariables = null;
            DebuggerVariableInspectionResult? outerVariables = null;
            DebuggerFrameInspectionIdentity? innerIdentity = null;
            if (inner is not null)
            {
                innerIdentity = Identity(debugger, inner, 1);
                innerVariables = await debugger.GetFrameVariablesAsync(innerIdentity).WaitAsync(TimeSpan.FromSeconds(15));
                firstPauseVariableCount = innerVariables.Variables.Count;
                Check(innerVariables.Availability == DebuggerVariableAvailability.Available, "current-frame DAP scopes are available");
                Check(innerVariables.FrameIdentity.ProviderFrameId == inner.ProviderFrameId && innerVariables.FrameIdentity.FrameId == inner.FrameId,
                    "current-frame inspection result retains the selected provider frame identity");
                Check(HasDisplayValue(innerVariables, "ParamInner", "PARAM-INNER") && HasDisplayValue(innerVariables, "LocalInner", "INNER") && HasDisplayValue(innerVariables, "BreakHere", "BREAK"),
                    "current-frame parameters and locals retain values");
                Check(HasDisplayValue(innerVariables, "UniqueInner", "INNER_ONLY") &&
                    HasDisplayValue(innerVariables, "ArrayInner", "1, 2, 3", "[1, 2, 3]") &&
                    HasDisplayValue(innerVariables, "ObjectInner", "@{Name=INNER_OBJ}"),
                    "current-frame distinct local, array contents and object contents remain inspectable");
                Check(!HasName(innerVariables, "LocalOuter"), "current-frame projection excludes caller locals");
                fixtureEvidence.Enqueue(CreateFixtureInspectionEvidence(inner, innerVariables));
            }
            if (outer is not null)
            {
                outerVariables = await debugger.GetFrameVariablesAsync(Identity(debugger, outer, 2)).WaitAsync(TimeSpan.FromSeconds(15));
                Check(outerVariables.Availability == DebuggerVariableAvailability.Available &&
                    outerVariables.FrameIdentity.ProviderFrameId == outer.ProviderFrameId &&
                    outerVariables.FrameIdentity.FrameId == outer.FrameId && !outer.IsCurrentFrame &&
                    HasDisplayValue(outerVariables, "ParamOuter", "PARAM-OUTER") &&
                    HasDisplayValue(outerVariables, "LocalOuter", "OUTER") &&
                    HasDisplayValue(outerVariables, "ArrayOuter", "4, 5, 6", "[4, 5, 6]") &&
                    HasDisplayValue(outerVariables, "ObjectOuter", "@{Name=OUTER_OBJ}"),
                    "selected caller frame exposes its own parameter, local, array and object values");
                Check(!HasName(outerVariables, "LocalInner"), "caller-frame projection excludes callee locals");
                fixtureEvidence.Enqueue(CreateFixtureInspectionEvidence(outer, outerVariables));
            }
            Check(debugger.CurrentState == DebugSessionState.Paused, "inspection leaves the debugger paused");

            var sourceMap = new DebugSourceMap(debugger.SessionId);
            var documentId = Guid.NewGuid();
            sourceMap.Register(script, documentId, 0, script, string.Join(Environment.NewLine, lines) + Environment.NewLine, isSnapshot: false);
            var mapped = inner is null ? default : sourceMap.Map(inner.ScriptName, inner.LineNumber, _ => 0);
            Check(inner is not null && mapped is not null && mapped.Status == DebugSourceMappingStatus.Exact && mapped.CanNavigate && mapped.SourceDocumentId == documentId,
                "production frame location maps exactly to an editor document");

            var firstPauseGeneration = debugger.PauseGeneration;
            await debugger.StepOutAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused && debugger.PauseGeneration > firstPauseGeneration, TimeSpan.FromSeconds(20));
            stepOutObserved = true;
            var callerPause = await debugger.GetCallStackAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(callerPause.Any(frame => frame.FunctionName == "Outer") && callerPause.All(frame => frame.FunctionName != "Inner"),
                "Step Out returns to the caller with a fresh pause");
            if (innerIdentity is not null)
            {
                var staleInspection = await debugger.GetFrameVariablesAsync(innerIdentity).WaitAsync(TimeSpan.FromSeconds(15));
                Check(staleInspection.Availability == DebuggerVariableAvailability.Stale,
                    "inspection identity from an earlier pause is rejected as stale");
            }
            Check(recorder.Locations.Any(location => location.LineNumber == 6 && Path.GetFullPath(location.ScriptPath ?? string.Empty).Equals(Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase)),
                "Step Out publishes the caller's source location");

            var callerPauseGeneration = debugger.PauseGeneration;
            await debugger.StepOverAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused && debugger.PauseGeneration > callerPauseGeneration, TimeSpan.FromSeconds(20));
            stepOverObserved = true;
            promptObservationsBeforeCompletion = Volatile.Read(ref terminalPromptObservationCount);
            await debugger.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await recorder.WaitForSessionEndedAsync();
            Check(debugger.TerminationInfo?.Reason == DebugTerminationReason.NormalCompletion, "continue completes the DAP debug session");
            Check(recorder.Terminations.Count == 1 && recorder.SessionEndedCount == 1, "normal completion publishes one terminal notification");
            Check(recorder.TypedEvents.Any(item => item.Category == DebuggerEventCategory.Breakpoint && item.IsNavigable && item.LineNumber == 14),
                "typed breakpoint event retains navigation data");
            Check(recorder.TypedEvents.Zip(recorder.TypedEvents.Skip(1), (left, right) => left.Sequence < right.Sequence).All(value => value),
                "typed event sequence advances monotonically");
            Check(recorder.TypedEvents.All(item => item.SessionId == debugger.SessionId), "typed events retain their debugger session identity");
            Check(Volatile.Read(ref stateObserverCalls) > 0, "a throwing state subscriber does not block later subscribers");
            Check(Volatile.Read(ref typedObserverCalls) > 0, "a throwing typed-event subscriber does not block later subscribers");
            Check(TerminalContainsMarkerLine(output, "__PSS_DEBUG_SESSION_ENDED__ USER_MARKER"),
                "marker-like user output remains ordinary output on the authoritative PSES/ConPTY terminal route");
            Check(service.IsSessionRunning, "PSES interactive session remains alive after debug completion");
            firstSessionCompletionObserved = recorder.SessionEndedCount == 1 && debugger.TerminationInfo?.Reason == DebugTerminationReason.NormalCompletion;

            var firstEndpoint = service as IAuthoritativePsesDebugEndpoint;
            Check(firstEndpoint is not null && firstEndpoint.IsSessionRunning, "first session completion retains the authoritative PSES host");
            var firstTeardown = await debugger.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            debugger.Dispose();
            firstSessionTeardownObserved = firstTeardown;
            Check(firstSessionTeardownObserved, "first DAP session follows production disconnect and disposal teardown");

            await WaitForAsync(() => Volatile.Read(ref terminalPromptObservationCount) > promptObservationsBeforeCompletion && service.IsInputWritable,
                TimeSpan.FromSeconds(15));
            hostReadyAfterFirstSession = Volatile.Read(ref terminalPromptObservationCount) > promptObservationsBeforeCompletion && service.IsInputWritable;
            Check(hostReadyAfterFirstSession, "authoritative interactive host reports writable readiness after first teardown");
            await service.WriteRawInputAsync("Write-Output 'AZ_POST_DEBUG_FIRST|HOST_READY'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => TerminalContainsMarkerLine(output, "AZ_POST_DEBUG_FIRST|HOST_READY"), TimeSpan.FromSeconds(15));
            postFirstSessionCommandObserved = TerminalContainsMarkerLine(output, "AZ_POST_DEBUG_FIRST|HOST_READY");
            Check(postFirstSessionCommandObserved, "interactive command succeeds through the authoritative terminal after first teardown");

            var nextEndpoint = service as IAuthoritativePsesDebugEndpoint;
            var ownership = nextEndpoint?.GetDebuggerOwnershipSnapshot();
            ownershipAfterFirstTeardown = ownership is null ? "unavailable" : $"{ownership.State};valid={ownership.IsValid};session={ownership.DebuggerSessionId}";
            Check(nextEndpoint is not null && nextEndpoint.IsSessionRunning && nextEndpoint.ProcessId == processId &&
                nextEndpoint.SessionGeneration == sessionGeneration && !string.IsNullOrWhiteSpace(nextEndpoint.DebugServicePipeName),
                "fresh production endpoint lookup retains the same live host, generation and published DAP route");
            var secondSessionAllowed = nextEndpoint is not null && ownership is { IsValid: true } &&
                ownership.State is DebuggerLifecycleObservation.None or DebuggerLifecycleObservation.Terminated;
            Check(secondSessionAllowed,
                "production debugger ownership contract establishes that a second DAP session may start after teardown");
            if (secondSessionAllowed)
            {
                var secondPath = Path.Combine(directory.FullName, "second-session.ps1");
                await File.WriteAllTextAsync(secondPath, "Write-Output 'AZ_SECOND_DAP_SESSION'" + Environment.NewLine);
                using var second = new PsesDapDebugSession(nextEndpoint!);
                secondDebuggerSessionId = second.SessionId;
                var secondRecorder = new EventRecorder(second).Attach();
                await second.StartAsync(runtime, secondPath, Array.Empty<DebugBreakpointInfo>());
                secondSessionStarted = true;
                await secondRecorder.WaitForSessionEndedAsync();
                Check(second.SessionId != firstDebuggerSessionId && second.TerminationInfo?.Reason == DebugTerminationReason.NormalCompletion,
                    "second DAP session has a fresh identity and completes on the existing host");
                Check(service.IsSessionRunning && service.ProcessId == processId && service.SessionGeneration == sessionGeneration,
                    "second DAP session preserves authoritative PSES process and generation continuity");
                Check(TerminalContainsMarkerLine(output, "AZ_SECOND_DAP_SESSION"),
                    "second-session script output reaches the authoritative interactive terminal");
                await second.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                second.Dispose();
                var postSecondOwnership = nextEndpoint.GetDebuggerOwnershipSnapshot();
                laterSessionAllowed = postSecondOwnership.IsValid &&
                    postSecondOwnership.State is DebuggerLifecycleObservation.None or DebuggerLifecycleObservation.Terminated;
                completedScenarios.Enqueue("second DAP session identity, host continuity and terminal output");
                if (!laterSessionAllowed)
                    unreachedScenarios.Enqueue($"later DAP exception/Stop scenarios were not started; post-second-session ownership was {postSecondOwnership.State} (valid={postSecondOwnership.IsValid})");
            }
            completedScenarios.Enqueue("breakpoint, frame inspection, source mapping, Step Out/Over, event delivery, output and normal completion");
        }

        async Task VerifyHandledAndUnhandledExceptionsAsync()
        {
            var handledPath = Path.Combine(directory.FullName, "handled-exception.ps1");
            await File.WriteAllTextAsync(handledPath, "try { throw 'AZ_HANDLED_SOURCE' } catch { Write-Output 'AZ_HANDLED' }" + Environment.NewLine);
            using (var handled = new PsesDapDebugSession(service))
            {
                var recorder = new EventRecorder(handled).Attach();
                await handled.StartAsync(runtime, handledPath, Array.Empty<DebugBreakpointInfo>());
                await recorder.WaitForSessionEndedAsync();
                Check(handled.TerminationInfo?.Reason == DebugTerminationReason.NormalCompletion, "handled exception completes normally");
                Check(!recorder.TypedEvents.Any(value => value.Category == DebuggerEventCategory.Exception), "handled exception is not published as terminating");
                Check(recorder.Output.Any(value => value.Contains("AZ_HANDLED", StringComparison.Ordinal)), "handled exception output is preserved");
            }

            var unhandledPath = Path.Combine(directory.FullName, "unhandled-exception.ps1");
            await File.WriteAllTextAsync(unhandledPath, "throw [System.InvalidOperationException]::new('AZ_UNHANDLED_SOURCE')" + Environment.NewLine);
            using (var unhandled = new PsesDapDebugSession(service))
            {
                exceptionDebuggerSessionId = unhandled.SessionId;
                var recorder = new EventRecorder(unhandled).Attach();
                await unhandled.StartAsync(runtime, unhandledPath, Array.Empty<DebugBreakpointInfo>());
                await recorder.WaitForSessionEndedAsync();
                Check(unhandled.TerminationInfo?.Reason == DebugTerminationReason.TerminatingException,
                    "unhandled PowerShell exception retains terminating-exception classification");
                Check(recorder.TypedEvents.Any(value => value.Category == DebuggerEventCategory.Exception && value.DisplayText.Contains("AZ_UNHANDLED_SOURCE", StringComparison.Ordinal)),
                    "unhandled exception carries its diagnostic event");
                Check(unhandled.SessionId != firstDebuggerSessionId && unhandled.SessionId != secondDebuggerSessionId, "repeated debug sessions receive distinct identities");
                Check(recorder.TypedEvents.All(value => value.SessionId == unhandled.SessionId), "repeated-session events use only the new identity");
            }
            completedScenarios.Enqueue("handled and unhandled exception behavior plus repeated DAP session identity");
        }

        async Task VerifyStopEventContainmentAndHostContinuityAsync()
        {
            var script = Path.Combine(directory.FullName, "stop-debug.ps1");
            await File.WriteAllTextAsync(script, "while ($true) { Start-Sleep -Milliseconds 100 }" + Environment.NewLine);
            using var debugger = new PsesDapDebugSession(service);
            var recorder = new EventRecorder(debugger).Attach();
            var endedObserverCalls = 0;
            debugger.SessionEnded += () => throw new InvalidOperationException("synthetic ended subscriber failure");
            debugger.SessionEnded += () => Interlocked.Increment(ref endedObserverCalls);
            await debugger.StartAsync(runtime, script, new[] { new DebugBreakpointInfo(script, 1) });
            await WaitForAsync(() => debugger.CurrentState == DebugSessionState.Paused, TimeSpan.FromSeconds(30));
            Check(await debugger.StopAsync().WaitAsync(TimeSpan.FromSeconds(20)), "explicit Stop succeeds");
            await recorder.WaitForSessionEndedAsync();
            Check(debugger.TerminationInfo?.Reason == DebugTerminationReason.UserStop && debugger.TerminationInfo.WasUserRequested,
                "explicit Stop is classified as user requested");
            Check(recorder.Terminations.Count == 1 && recorder.SessionEndedCount == 1, "Stop publishes terminal notification once");
            Check(Volatile.Read(ref endedObserverCalls) > 0,
                "a throwing SessionEnded subscriber does not block later subscribers");
            Check(service.IsSessionRunning, "stopping DAP leaves the authoritative PSES process alive");
            await service.WriteRawInputAsync("Write-Output 'AZ_POST_DEBUG|HOST_CONTINUITY'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => TerminalContainsMarkerLine(output, "AZ_POST_DEBUG|HOST_CONTINUITY"), TimeSpan.FromSeconds(15));
            completedScenarios.Enqueue("intentional stop, terminal notification, and interactive host continuity");
        }

        void Check(bool condition, string description)
        {
            if (!condition) failures.Enqueue(description);
        }
    }

    private static DebuggerFrameInspectionIdentity Identity(PsesDapDebugSession debugger, DebugCallStackFrame frame, long requestGeneration)
        => new(debugger.SessionId, debugger.PauseGeneration, requestGeneration, frame.FrameId, frame.FrameIndex)
        {
            ThreadId = frame.ThreadId,
            ProviderFrameId = frame.ProviderFrameId
        };

    private static bool HasDisplayValue(DebuggerVariableInspectionResult result, string name, params string[] acceptedDisplayValues)
        => result.Variables.Any(variable => variable.Name == name && acceptedDisplayValues.Contains(variable.Value, StringComparer.Ordinal));

    private static bool HasName(DebuggerVariableInspectionResult result, string name)
        => result.Variables.Any(variable => variable.Name == name);

    private static object CreateFixtureInspectionEvidence(DebugCallStackFrame frame, DebuggerVariableInspectionResult result)
    {
        var fixtureNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ParamInner", "LocalInner", "UniqueInner", "ArrayInner", "ObjectInner", "BreakHere",
            "ParamOuter", "LocalOuter", "ArrayOuter", "ObjectOuter", "AfterInner"
        };
        var selected = result.Variables
            .Where(variable => fixtureNames.Contains(variable.Name))
            .Take(12)
            .Select(variable => new
            {
                variable.Name,
                variable.Type,
                Value = variable.Value.Length <= 120 ? variable.Value : variable.Value[..120],
                variable.Scope,
                variable.IsExpandable,
                variable.ProviderFrameId,
                variable.FrameId
            })
            .ToArray();
        return new
        {
            frame.FunctionName,
            providerFrameId = frame.ProviderFrameId,
            frame.FrameId,
            frame.ThreadId,
            sourceFile = Path.GetFileName(frame.ScriptName),
            frame.LineNumber,
            scopeNames = selected.Select(variable => variable.Scope).Distinct(StringComparer.Ordinal).Take(8).ToArray(),
            variables = selected,
            rawDapVariablesReferenceIdsAvailable = false
        };
    }

    private static bool TerminalContainsMarkerLine(IEnumerable<string> output, string marker)
    {
        var terminalText = Regex.Replace(string.Concat(output), "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        return terminalText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), marker, StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.True(condition(), "The expected production PSES/DAP state was not observed before the timeout.");
    }

    private static PowerShellRuntimeInfo? FindRuntime()
    {
        var path = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (!File.Exists(path)) return null;
        return new PowerShellRuntimeInfo("AZ PowerShell", "Core", "7.x", new Version(7, 0), "x64", path, "Phase2C-AZ", true, false, true, true);
    }

    private sealed class EventRecorder(PsesDapDebugSession debugger)
    {
        private readonly ConcurrentQueue<DebuggerEvent> _typedEvents = new();
        private readonly ConcurrentQueue<string> _output = new();
        private readonly ConcurrentQueue<DebugTerminationInfo> _terminations = new();
        private readonly ConcurrentQueue<(string? ScriptPath, int LineNumber)> _locations = new();
        private int _sessionEndedCount;

        public IReadOnlyList<DebuggerEvent> TypedEvents => _typedEvents.ToArray();
        public IReadOnlyList<string> Output => _output.ToArray();
        public IReadOnlyList<DebugTerminationInfo> Terminations => _terminations.ToArray();
        public IReadOnlyList<(string? ScriptPath, int LineNumber)> Locations => _locations.ToArray();
        public int SessionEndedCount => Volatile.Read(ref _sessionEndedCount);

        public TaskCompletionSource<bool> SessionEndedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventRecorder Attach()
        {
            debugger.TypedEventReceived += value => _typedEvents.Enqueue(value);
            debugger.OutputReceived += value => _output.Enqueue(value);
            debugger.Terminated += value => _terminations.Enqueue(value);
            debugger.SessionEnded += () =>
            {
                Interlocked.Increment(ref _sessionEndedCount);
                SessionEndedSignal.TrySetResult(true);
            };
            debugger.BreakpointHit += (path, line) => _locations.Enqueue((path, line));
            return this;
        }

        public async Task WaitForSessionEndedAsync()
        {
            // The event recorder is attached lazily so callers can attach faulting
            // observers first when checking multicast isolation.
            await SessionEndedSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
