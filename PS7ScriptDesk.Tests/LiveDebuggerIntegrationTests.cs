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
