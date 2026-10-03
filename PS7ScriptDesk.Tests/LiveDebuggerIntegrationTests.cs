using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Debug;
using Xunit.Sdk;

namespace PS7ScriptDesk.Tests;

public sealed class LiveDebuggerIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task RealPowerShellDebugger_BreakpointQueriesStepOutputCompletionAndRestart()
    {
        var runtime = FindRuntime();
        if (runtime is null)
        {
            throw SkipException.ForSkip("No validated PowerShell 7 runtime was discovered.");
        }

        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-Debugger-");
        try
        {
            var firstScript = Path.Combine(root.FullName, "first.ps1");
            await File.WriteAllTextAsync(firstScript, "$value = 41\n$value = $value + 1\nWrite-Output 'LIVE_TYPED_OUTPUT'\nWrite-Host 'LIVE_TYPED_HOST'\n");

            using var first = new PsesDebugSession();
            var firstRecorder = new EventRecorder(first);
            var firstPresentation = new DebugOutputPresentationModel();
            firstPresentation.BeginSession(first.SessionId, firstScript);
            first.TypedEventReceived += firstPresentation.Append;
            await first.StartAsync(runtime, firstScript, new[] { new DebugBreakpointInfo(firstScript, 2) });
            await firstRecorder.WaitForStateAsync(DebugSessionState.Paused);
            await firstRecorder.WaitForTypedEventAsync(value => value.Category == DebuggerEventCategory.Breakpoint);

            Assert.Equal(first.SessionId, firstRecorder.TypedEvents.First(value => value.Category == DebuggerEventCategory.DebuggerLifecycle).SessionId);
            Assert.True(firstRecorder.TypedEvents.Zip(firstRecorder.TypedEvents.Skip(1), (left, right) => left.Sequence < right.Sequence).All(value => value));
            Assert.Contains(firstRecorder.TypedEvents, value => value.Category == DebuggerEventCategory.Breakpoint && value.IsNavigable && value.LineNumber == 2);

            var variables = await first.GetVariablesAsync();
            await firstRecorder.WaitForCurrentStateAsync(DebugSessionState.Paused);
            var callStack = await first.GetCallStackAsync();
            await firstRecorder.WaitForCurrentStateAsync(DebugSessionState.Paused);
            Assert.NotNull(variables);
            Assert.NotNull(callStack);

            await first.StepOverAsync();
            await firstRecorder.WaitForCurrentStateAsync(DebugSessionState.Paused);
            await first.ContinueAsync();
            await firstRecorder.WaitForSessionEndedAsync();

            Assert.Equal(DebugTerminationReason.NormalCompletion, first.TerminationInfo?.Reason);
            Assert.Equal(1, firstRecorder.Terminations.Count);
            Assert.Contains(firstRecorder.Output, value => value.Contains("LIVE_TYPED_OUTPUT", StringComparison.Ordinal));
            Assert.Contains(firstRecorder.Output, value => value.Contains("LIVE_TYPED_HOST", StringComparison.Ordinal));
            Assert.DoesNotContain(firstRecorder.Output, value => value.Contains("__PSS_DEBUG_", StringComparison.Ordinal));
            Assert.Contains(firstPresentation.Items, value => value.Event?.Category == DebuggerEventCategory.Breakpoint);
            Assert.Contains(firstPresentation.Items, value => value.Event?.Category == DebuggerEventCategory.Step);
            Assert.Contains(firstPresentation.Items, value => value.Event?.Category == DebuggerEventCategory.NativeStdout && value.DisplayText.Contains("LIVE_TYPED_OUTPUT", StringComparison.Ordinal));
            Assert.Contains(firstPresentation.Items, value => value.Event?.Category == DebuggerEventCategory.DebuggerLifecycle && value.DisplayText.Contains("Stopped", StringComparison.Ordinal));
            Assert.DoesNotContain(firstPresentation.Items, value => value.DisplayText.Contains("__PSS_DEBUG_", StringComparison.Ordinal));
            Assert.Equal(firstRecorder.TypedEvents.Count(value => value.Category != DebuggerEventCategory.Protocol), firstPresentation.Items.Count - 1);

            var secondScript = Path.Combine(root.FullName, "second.ps1");
            await File.WriteAllTextAsync(secondScript, "Write-Output 'LIVE_SECOND_SESSION'\n");
            using var second = new PsesDebugSession();
            var secondRecorder = new EventRecorder(second);
            var secondPresentation = new DebugOutputPresentationModel();
            secondPresentation.BeginSession(second.SessionId, secondScript);
            second.TypedEventReceived += secondPresentation.Append;
            await second.StartAsync(runtime, secondScript, Array.Empty<DebugBreakpointInfo>());
            await secondRecorder.WaitForSessionEndedAsync();

            Assert.Equal(DebugTerminationReason.NormalCompletion, second.TerminationInfo?.Reason);
            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.All(secondRecorder.TypedEvents, value => Assert.Equal(second.SessionId, value.SessionId));
            Assert.DoesNotContain(secondRecorder.TypedEvents, value => firstRecorder.TypedEvents.Contains(value));
            Assert.Contains(secondRecorder.Output, value => value.Contains("LIVE_SECOND_SESSION", StringComparison.Ordinal));
            Assert.Single(secondPresentation.Items, value => value.IsSessionBoundary);
            Assert.All(secondPresentation.Items.Where(value => !value.IsSessionBoundary), value => Assert.Equal(second.SessionId, value.SessionId));
        }
        finally
        {
            try { Directory.Delete(root.FullName, recursive: true); } catch { }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task RealPowerShellDebugger_DistinguishesHandledAndUnhandledExceptions()
    {
        var runtime = FindRuntime();
        if (runtime is null)
        {
            throw SkipException.ForSkip("No validated PowerShell 7 runtime was discovered.");
        }

        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-Debugger-exception-");
        try
        {
            var handledScript = Path.Combine(root.FullName, "handled.ps1");
            await File.WriteAllTextAsync(handledScript, "try { throw 'LIVE_HANDLED_SOURCE' } catch { Write-Output 'LIVE_HANDLED' }\n");
            using (var handled = new PsesDebugSession())
            {
                var recorder = new EventRecorder(handled);
                await handled.StartAsync(runtime, handledScript, Array.Empty<DebugBreakpointInfo>());
                await recorder.WaitForSessionEndedAsync();
                Assert.Equal(DebugTerminationReason.NormalCompletion, handled.TerminationInfo?.Reason);
                Assert.DoesNotContain(recorder.TypedEvents, value => value.Category == DebuggerEventCategory.Exception);
            }

            var unhandledScript = Path.Combine(root.FullName, "unhandled.ps1");
            await File.WriteAllTextAsync(unhandledScript, "throw [System.InvalidOperationException]::new('LIVE_UNHANDLED_SOURCE')\n");
            using var unhandled = new PsesDebugSession();
            var unhandledRecorder = new EventRecorder(unhandled);
            await unhandled.StartAsync(runtime, unhandledScript, Array.Empty<DebugBreakpointInfo>());
            await unhandledRecorder.WaitForSessionEndedAsync();

            Assert.Equal(DebugTerminationReason.TerminatingException, unhandled.TerminationInfo?.Reason);
            var exceptionEvent = Assert.Single(unhandledRecorder.TypedEvents.Where(value => value.Category == DebuggerEventCategory.Exception));
            Assert.False(unhandled.TerminationInfo!.Exception!.IsHandled);
            Assert.Contains("LIVE_UNHANDLED_SOURCE", exceptionEvent.DisplayText, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root.FullName, recursive: true); } catch { }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task RealPowerShellDebugger_StepOutReturnsToCallerAndSourceMapAcceptsLocation()
    {
        var runtime = FindRuntime();
        if (runtime is null)
        {
            throw SkipException.ForSkip("No validated PowerShell 7 runtime was discovered.");
        }

        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-Debugger-stepout-");
        try
        {
            var scriptPath = Path.Combine(root.FullName, "stepout.ps1");
            var script = string.Join(Environment.NewLine,
                "function Inner-Test {",
                "    $inside1 = 'inside 1'",
                "    $inside2 = 'inside 2'",
                "}",
                "",
                "function Outer-Test {",
                "    $before = 'before'",
                "    Inner-Test",
                "    $after = 'after'",
                "}",
                "",
                "Outer-Test",
                string.Empty);
            await File.WriteAllTextAsync(scriptPath, script);

            using var session = new PsesDebugSession();
            var recorder = new EventRecorder(session);
            await session.StartAsync(runtime, scriptPath, new[] { new DebugBreakpointInfo(scriptPath, 2) });
            await recorder.WaitForStateAsync(DebugSessionState.Paused);
            var firstPause = session.PauseGeneration;
            var firstStack = await session.GetCallStackAsync();
            Assert.Contains(firstStack, frame => frame.FunctionName.Contains("Inner-Test", StringComparison.OrdinalIgnoreCase));

            await session.StepOutAsync();
            await recorder.WaitForNextPauseAsync(firstPause);

            var callerStack = await session.GetCallStackAsync();
            var caller = Assert.Single(callerStack, frame => frame.FunctionName.Contains("Outer-Test", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recorder.Locations, location => location.LineNumber == caller.LineNumber && string.Equals(Path.GetFullPath(location.ScriptPath ?? string.Empty), scriptPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(session.PauseGeneration > firstPause);
            Assert.Equal(scriptPath, Path.GetFullPath(caller.ScriptName));
            Assert.InRange(caller.LineNumber, 8, 9);

            var map = new DebugSourceMap(session.SessionId);
            var documentId = Guid.NewGuid();
            map.Register(scriptPath, documentId, 0, scriptPath, script, isSnapshot: false);
            var mapped = map.Map(caller.ScriptName, caller.LineNumber, _ => 0);
            Assert.Equal(DebugSourceMappingStatus.Exact, mapped.Status);
            Assert.True(mapped.CanNavigate);
            Assert.Equal(documentId, mapped.SourceDocumentId);
            Assert.Equal(caller.LineNumber, mapped.EditorLine);
        }
        finally
        {
            try { Directory.Delete(root.FullName, recursive: true); } catch { }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task RealPowerShellDebugger_LineBreakpointNestedFrameCapturesCurrentLocals()
    {
        var runtime = FindRuntime();
        if (runtime is null)
        {
            throw SkipException.ForSkip("No validated PowerShell 7 runtime was discovered.");
        }

        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-LineBreakpointFrameVariables-");
        try
        {
            var scriptPath = Path.Combine(root.FullName, "line-breakpoint-nested.ps1");
            var lines = new[]
            {
                "function FunctionA([string]$ParamA) {",
                "    $LocalA = 'LOCAL-A'",
                "    FunctionB 'PARAM-B'",
                "}",
                "",
                "function FunctionB([string]$ParamB) {",
                "    $LocalB = 'LOCAL-B'",
                "    FunctionC 'PARAM-C'",
                "}",
                "",
                "function FunctionC([string]$ParamC) {",
                "    $LocalC = 'LOCAL-C'",
                "    $NumberC = 333",
                "    $ArrayC = @(1, 2, 3)",
                "    $ObjectC = [pscustomobject]@{ Name = 'OBJECT-C' }",
                "    $BreakHere = 'BREAKPOINT-HERE'",
                "    Write-Output $BreakHere",
                "}",
                "FunctionA 'PARAM-A'"
            };
            var script = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            await File.WriteAllTextAsync(scriptPath, script);

            using var session = new PsesDebugSession();
            var recorder = new EventRecorder(session);
            await session.StartAsync(runtime, scriptPath, new[] { new DebugBreakpointInfo(scriptPath, 17) });
            await recorder.WaitForStateAsync(DebugSessionState.Paused);

            var callStack = await session.GetCallStackAsync();
            var functionC = Assert.Single(callStack, frame => frame.FunctionName == "FunctionC");
            var functionB = Assert.Single(callStack, frame => frame.FunctionName == "FunctionB");
            var functionA = Assert.Single(callStack, frame => frame.FunctionName == "FunctionA");
            Assert.True(functionC.IsCurrentFrame);

            var current = await session.GetFrameVariablesAsync(
                new DebuggerFrameInspectionIdentity(session.SessionId, session.PauseGeneration, 1, functionC.FrameId, functionC.FrameIndex)
                {
                    ThreadId = functionC.ThreadId,
                    ProviderFrameId = functionC.ProviderFrameId
                });
            var outer = await session.GetFrameVariablesAsync(
                new DebuggerFrameInspectionIdentity(session.SessionId, session.PauseGeneration, 2, functionB.FrameId, functionB.FrameIndex)
                {
                    ThreadId = functionB.ThreadId,
                    ProviderFrameId = functionB.ProviderFrameId
                });

            var scopeDiagnostic = await session.QueryCurrentProviderScopeDiagnosticAsync();
            Assert.Equal("System.Management.Automation.CallStackFrame", scopeDiagnostic.FrameType);
            Assert.Equal("FunctionC", scopeDiagnostic.FrameFunctionName);
            Assert.Contains("ParamC", scopeDiagnostic.GetVariableScopeZeroNames);
            Assert.Contains("LocalC", scopeDiagnostic.GetVariableScopeZeroNames);
            Assert.Contains("NumberC", scopeDiagnostic.GetVariableScopeZeroNames);
            Assert.DoesNotContain("LocalA", scopeDiagnostic.GetVariableScopeZeroNames);
            Assert.DoesNotContain("LocalB", scopeDiagnostic.GetVariableScopeZeroNames);
            Assert.True(scopeDiagnostic.DirectParamCPresent && scopeDiagnostic.DirectLocalCPresent && scopeDiagnostic.DirectNumberCPresent);
            Assert.Equal(DebuggerVariableAvailability.Available, current.Availability);
            Assert.Contains(current.Variables, variable => variable.Name == "ParamC" && variable.Value == "PARAM-C");
            Assert.Contains(current.Variables, variable => variable.Name == "LocalC" && variable.Value == "LOCAL-C");
            Assert.Contains(current.Variables, variable => variable.Name == "NumberC" && variable.Value == "333");
            Assert.Contains(current.Variables, variable => variable.Name == "ArrayC");
            Assert.Contains(current.Variables, variable => variable.Name == "ObjectC");
            Assert.Contains(current.Variables, variable => variable.Name == "BreakHere" && variable.Value == "BREAKPOINT-HERE");
            Assert.Equal(DebuggerVariableAvailability.Unavailable, outer.Availability);
            Assert.DoesNotContain(current.Variables, variable => variable.Name == "LocalA" || variable.Name == "LocalB");
            Assert.Equal(functionC.ProviderFrameId, callStack.Single(frame => frame.IsCurrentFrame).ProviderFrameId);
            Assert.False(functionA.IsCurrentFrame);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task RealPowerShellDebugger_TestDebugLine83ScopeProjectionCompletes()
    {
        var scriptPath = @"C:\Users\rbarn\Downloads\TestDebug.ps1";
        if (!File.Exists(scriptPath))
        {
            throw SkipException.ForSkip("The local TestDebug.ps1 reproduction is not available.");
        }

        var runtime = FindRuntime();
        if (runtime is null)
        {
            throw SkipException.ForSkip("No validated PowerShell 7 runtime was discovered.");
        }

        using var session = new PsesDebugSession();
        var recorder = new EventRecorder(session);
        await session.StartAsync(runtime, scriptPath, new[] { new DebugBreakpointInfo(scriptPath, 83) });
        await recorder.WaitForStateAsync(DebugSessionState.Paused);

        var callStack = await session.GetCallStackAsync();
        var current = Assert.Single(callStack, frame => frame.FunctionName == "Invoke-Level2" && frame.IsCurrentFrame);
        var variables = await session.GetFrameVariablesAsync(
            new DebuggerFrameInspectionIdentity(session.SessionId, session.PauseGeneration, 1, current.FrameId, current.FrameIndex)
            {
                ThreadId = current.ThreadId,
                ProviderFrameId = current.ProviderFrameId
            });

        Assert.Equal(DebuggerVariableAvailability.Available, variables.Availability);
        Assert.Contains(variables.Variables, variable => variable.Name == "Value");
        Assert.Contains(variables.Variables, variable => variable.Name == "args");
        Assert.Equal(DebugSessionState.Paused, session.CurrentState);
        Assert.True(await session.StopAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task RealPowerShellDebugger_InspectsDistinctNestedFrameVariables()
    {
        var runtime = FindRuntime();
        if (runtime is null)
        {
            throw SkipException.ForSkip("No validated PowerShell 7 runtime was discovered.");
        }

        var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-FrameVariables-");
        try
        {
            var scriptPath = Path.Combine(root.FullName, "nested.ps1");
            await File.WriteAllTextAsync(
                scriptPath,
                "function FunctionA([string]$ParamA) {\n" +
                "    $LocalA = 'Local-A'\n" +
                "    FunctionB 'Parameter-B'\n" +
                "}\n" +
                "function FunctionB([string]$ParamB) {\n" +
                "    $LocalB = 'Local-B'\n" +
                "    FunctionC 'Param-C'\n" +
                "}\n" +
                "function FunctionC([string]$ParamC) {\n" +
                "    $LocalC = 'Local-C'\n" +
                "    $NumberC = 333\n" +
                "    $ArrayC = @(1, 2, 3)\n" +
                "    $ObjectC = [pscustomobject]@{ Name = 'Object-C' }\n" +
                "    $BreakHere = 'BREAKPOINT-HERE'\n" +
                "    Wait-Debugger\n" +
                "    $AfterFirstPause = 'AFTER-FIRST-PAUSE'\n" +
                "    Wait-Debugger\n" +
                "}\n" +
                "FunctionA 'Parameter-A'\n");

            using var session = new PsesDebugSession();
            var recorder = new EventRecorder(session);
            await session.StartAsync(runtime, scriptPath, Array.Empty<DebugBreakpointInfo>());
            await recorder.WaitForStateAsync(DebugSessionState.Paused);

            var callStack = await session.GetCallStackAsync();
            var functionA = Assert.Single(callStack, frame => frame.FunctionName == "FunctionA");
            var functionB = Assert.Single(callStack, frame => frame.FunctionName == "FunctionB");
            var functionC = Assert.Single(callStack, frame => frame.FunctionName == "FunctionC");

            var requestGeneration = 1L;
            var variablesA = await session.GetFrameVariablesAsync(
                new DebuggerFrameInspectionIdentity(session.SessionId, session.PauseGeneration, requestGeneration, functionA.FrameId, functionA.FrameIndex) { ThreadId = functionA.ThreadId, ProviderFrameId = functionA.ProviderFrameId });
            var variablesB = await session.GetFrameVariablesAsync(
                new DebuggerFrameInspectionIdentity(session.SessionId, session.PauseGeneration, requestGeneration + 1, functionB.FrameId, functionB.FrameIndex) { ThreadId = functionB.ThreadId, ProviderFrameId = functionB.ProviderFrameId });
            var variablesC = await session.GetFrameVariablesAsync(
                new DebuggerFrameInspectionIdentity(session.SessionId, session.PauseGeneration, requestGeneration + 2, functionC.FrameId, functionC.FrameIndex) { ThreadId = functionC.ThreadId, ProviderFrameId = functionC.ProviderFrameId });

            Assert.Equal(DebuggerVariableAvailability.Unavailable, variablesA.Availability);
            Assert.Equal(DebuggerVariableAvailability.Unavailable, variablesB.Availability);
            Assert.Equal(DebuggerVariableAvailability.Available, variablesC.Availability);
            Assert.Empty(variablesA.Variables);
            Assert.Empty(variablesB.Variables);
            Assert.Contains(variablesC.Variables, variable => variable.Name == "ParamC" && variable.Value == "Param-C");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "LocalC" && variable.Value == "Local-C");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "NumberC" && variable.Value == "333");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "ArrayC");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "ObjectC");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "BreakHere" && variable.Value == "BREAKPOINT-HERE");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "args");
            Assert.Contains(variablesC.Variables, variable => variable.Name == "MyInvocation");
            Assert.DoesNotContain(variablesC.Variables, variable => variable.Name == "LocalA" || variable.Name == "LocalB");
            Assert.Equal(DebugSessionState.Paused, session.CurrentState);
            Assert.Equal(functionC.ProviderFrameId, callStack.Single(frame => frame.IsCurrentFrame).ProviderFrameId);

            // Exercise the same pause/selection identity sequence used by the WPF
            // inspection path: publish the provider stack, select an outer frame,
            // then return to the current frame and issue a fresh request identity.
            using var inspection = new DebuggerInspectionContext();
            inspection.BeginSession(session.SessionId);
            inspection.PreparePaused(session.SessionId, session.PauseGeneration);
            Assert.True(inspection.PublishCallStack(callStack));
            Assert.Equal(functionC.FrameId, inspection.CurrentExecutionFrame?.FrameId);
            Assert.Equal(functionC.FrameId, inspection.SelectedInspectionFrame?.FrameId);

            Assert.True(inspection.TrySelectFrame(functionB, out var selectionError), selectionError);
            var outerRequest = inspection.BeginRequest();
            var outerResult = await session.GetFrameVariablesAsync(
                DebuggerFrameInspectionIdentity.FromFrame(inspection.SelectedInspectionFrame!, outerRequest.Identity.RequestGeneration));
            Assert.Equal(DebuggerVariableAvailability.Unavailable, outerResult.Availability);
            Assert.Empty(outerResult.Variables);

            Assert.True(inspection.TrySelectFrame(functionC, out selectionError), selectionError);
            var currentRequest = inspection.BeginRequest();
            var currentResult = await session.GetFrameVariablesAsync(
                DebuggerFrameInspectionIdentity.FromFrame(inspection.SelectedInspectionFrame!, currentRequest.Identity.RequestGeneration));
            Assert.Equal(DebuggerVariableAvailability.Available, currentResult.Availability);
            Assert.Contains(currentResult.Variables, variable => variable.Name == "ParamC");

            await session.StepOverAsync();
            await recorder.WaitForNextPauseAsync(session.PauseGeneration);
            var staleResult = await session.GetFrameVariablesAsync(
                DebuggerFrameInspectionIdentity.FromFrame(functionC.Identity, currentRequest.Identity.RequestGeneration));
            Assert.Equal(DebuggerVariableAvailability.Stale, staleResult.Availability);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static PowerShellRuntimeInfo? FindRuntime()
        => new RuntimeService().DiscoverRuntimes(requireLaunchValidation: true).PreferredRuntime;

    private sealed class EventRecorder
    {
        private readonly IDebugSession _session;
        private readonly object _gate = new();
        private readonly List<DebuggerEvent> _typedEvents = new();
        private readonly List<string> _output = new();
        private readonly List<DebugSessionState> _states = new();
        private readonly List<DebugTerminationInfo> _terminations = new();
        private readonly List<(string? ScriptPath, int LineNumber)> _locations = new();
        private bool _sessionEnded;

        public EventRecorder(IDebugSession session)
        {
            _session = session;
            _session.TypedEventReceived += OnTypedEvent;
            _session.OutputReceived += OnOutput;
            _session.StateChanged += OnStateChanged;
            _session.SessionEnded += OnSessionEnded;
            _session.Terminated += OnTerminated;
            _session.BreakpointHit += OnBreakpointHit;
        }

        public IReadOnlyList<DebuggerEvent> TypedEvents { get { lock (_gate) return _typedEvents.ToArray(); } }
        public IReadOnlyList<string> Output { get { lock (_gate) return _output.ToArray(); } }
        public IReadOnlyList<DebugTerminationInfo> Terminations { get { lock (_gate) return _terminations.ToArray(); } }
        public IReadOnlyList<(string? ScriptPath, int LineNumber)> Locations { get { lock (_gate) return _locations.ToArray(); } }

        public async Task WaitForStateAsync(DebugSessionState expected)
            => await WaitUntilAsync(() => { lock (_gate) return _states.Contains(expected); }, $"state {expected}");

        public async Task WaitForSessionEndedAsync()
            => await WaitUntilAsync(() => { lock (_gate) return _sessionEnded; }, "session ended");

        public async Task WaitForTypedEventAsync(Func<DebuggerEvent, bool> predicate)
            => await WaitUntilAsync(() => { lock (_gate) return _typedEvents.Any(predicate); }, "typed debugger event");

        public async Task WaitForCurrentStateAsync(DebugSessionState expected)
            => await WaitUntilAsync(() => _session.CurrentState == expected, $"current state {expected}");

        public async Task WaitForNextPauseAsync(long previousPauseGeneration)
            => await WaitUntilAsync(() => _session.CurrentState == DebugSessionState.Paused && _session.PauseGeneration > previousPauseGeneration, "next paused generation");

        private void OnTypedEvent(DebuggerEvent value) { lock (_gate) _typedEvents.Add(value); }
        private void OnOutput(string value) { lock (_gate) _output.Add(value); }
        private void OnStateChanged(DebugSessionState value) { lock (_gate) _states.Add(value); }
        private void OnSessionEnded() { lock (_gate) _sessionEnded = true; }
        private void OnTerminated(DebugTerminationInfo value) { lock (_gate) _terminations.Add(value); }
        private void OnBreakpointHit(string? scriptPath, int lineNumber) { lock (_gate) _locations.Add((scriptPath, lineNumber)); }

        private static async Task WaitUntilAsync(Func<bool> condition, string description)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.True(condition(), $"Timed out waiting for {description}.");
        }
    }
}
