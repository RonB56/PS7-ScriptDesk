using System.Diagnostics;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using Xunit.Abstractions;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class ExecutionCorrelationTelemetryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EnabledRecorderPersistsIdentifiableCorrelatedEvents()
    {
        DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings
        {
            IsDeveloperDiagnosticsEnabled = true,
            DeveloperDiagnosticsWriteJsonLines = true,
            DeveloperDiagnosticsWriteReadableLog = false
        }, "B1 telemetry persistence test");
        var session = DeveloperDiagnostics.CurrentSessionDirectory!;
        var request = Guid.NewGuid();
        try
        {
            var recorder = new ExecutionCorrelationTelemetry(true, persist: true);
            recorder.LogActivation();
            Assert.True(recorder.IsEnabled);
            recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: request,
                RequestCorrelation: "authoritative"));
            recorder.Record(new ExecutionCorrelationEvent("AdmissionGranted", RequestId: request,
                RequestCorrelation: "authoritative"));
            recorder.Record(new ExecutionCorrelationEvent("ExecutionOutcomeObserved", RequestId: request,
                RequestCorrelation: "authoritative", Outcome: "Completed"));
            Assert.Equal(new[] { "RequestCreated", "AdmissionGranted", "ExecutionOutcomeObserved" },
                recorder.Snapshot().Select(e => e.Code));
        }
        finally
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "B1 telemetry persistence test cleanup");
        }
        var persisted = await File.ReadAllTextAsync(Path.Combine(session, "developer-diagnostics.ndjson"));
        Assert.Contains("B1 execution telemetry activation evaluated.", persisted, StringComparison.Ordinal);
        Assert.Contains("ExecutionCorrelation", persisted, StringComparison.Ordinal);
        Assert.Contains("RequestCreated", persisted, StringComparison.Ordinal);
        Assert.Contains("AdmissionGranted", persisted, StringComparison.Ordinal);
        Assert.Contains("ExecutionOutcomeObserved", persisted, StringComparison.Ordinal);
        Assert.Contains(request.ToString(), persisted, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void DisabledRecorderDropsEventsWithoutAllocatingRecords()
    {
        var recorder = new ExecutionCorrelationTelemetry(false);
        Assert.False(recorder.IsEnabled);
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: Guid.NewGuid()));
        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public void CorrelationRetainsAvailableIdentitiesAndGlobalPsesHasNoRequest()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var request = Guid.NewGuid();
        var session = Guid.NewGuid();
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: request,
            RequestCorrelation: "authoritative", RuntimeSessionId: session, RuntimeGeneration: 11,
            TerminalGeneration: 7, ProcessId: 123, ProcessStartUtcTicks: 456));
        recorder.RecordPsesBusy(true);
        recorder.RecordPsesBusy(false);
        var events = recorder.Snapshot();
        Assert.Equal(request, events[0].RequestId);
        Assert.Equal(session, events[0].RuntimeSessionId);
        Assert.Equal(11, events[0].RuntimeGeneration);
        Assert.Equal(7, events[0].TerminalGeneration);
        Assert.Equal(123, events[0].ProcessId);
        Assert.Equal(456, events[0].ProcessStartUtcTicks);
        Assert.All(events.Skip(1), e => { Assert.Null(e.RequestId); Assert.Equal("global-uncorrelated", e.RequestCorrelation); });
        Assert.True(events[0].Sequence < events[1].Sequence);
        Assert.True(events[0].Timestamp > 0);
    }

    [Fact]
    public void ShadowDivergenceDoesNotCompleteProductionSignal()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var shadow = new ExecutionShadowReadiness(recorder, Guid.NewGuid(), 4, 123, 456);
        var production = new PromptRestorationSignal(4);
        shadow.ObserveStatus(true, production.Snapshot().Completed, false, true);
        production.ObserveStatusFileVisible();
        production.ObservePrompt(4, true, false, true);
        shadow.ObservePrompt(4, true, production.Snapshot().Completed, false, true);
        Assert.False(production.Completion.IsCompleted);
        Assert.Contains(recorder.Snapshot(), e => e.Code == "ShadowDivergence" && e.ShadowReady == true && e.ActualReady == false);
    }

    [Fact]
    public void ShadowRejectsStaleGenerationInvalidStatusAndPreStatusPrompt()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var shadow = new ExecutionShadowReadiness(recorder, Guid.NewGuid(), 4, 123, null);
        shadow.ObservePrompt(4, true, false, false, true);
        shadow.ObserveStatus(true, false, false, true);
        Assert.DoesNotContain(recorder.Snapshot(), e => e.ShadowReady == true);
        shadow.ObservePrompt(3, true, false, false, false);
        Assert.DoesNotContain(recorder.Snapshot(), e => e.ShadowReady == true);
        shadow.ObserveStatus(false, false, false, true);
        shadow.ObservePrompt(4, true, false, false, true);
        Assert.DoesNotContain(recorder.Snapshot(), e => e.ShadowReady == true);
    }

    [Fact]
    public async Task ConcurrentRecordingStaysBoundedAndCountsEvictions()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) recorder.Record(new ExecutionCorrelationEvent("PromptCandidate"));
        })));
        Assert.InRange(recorder.Snapshot().Length, 1, 1024);
        Assert.True(recorder.EvictedCount > 0);
        Assert.Equal(0, recorder.FailureCount);
    }

    [Fact]
    public void ObserverFailureDoesNotEscapeOrChangeProductionReadiness()
    {
        var recorder = new ExecutionCorrelationTelemetry(true, observer: _ => throw new InvalidOperationException());
        var production = new PromptRestorationSignal(4);
        recorder.Record(new ExecutionCorrelationEvent("PromptCandidate"));
        Assert.Equal(1, recorder.FailureCount);
        Assert.False(production.Completion.IsCompleted);
    }

    [Fact]
    public void PerRequestRetentionAndRequestWindowStayBounded()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var request = Guid.NewGuid();
        for (var i = 0; i < ExecutionCorrelationTelemetry.MaximumEventsPerRequest + 17; i++)
            recorder.Record(new ExecutionCorrelationEvent("PromptCandidate", RequestId: request, Reason: i.ToString()));
        Assert.Equal(ExecutionCorrelationTelemetry.MaximumEventsPerRequest,
            recorder.Snapshot().Count(item => item.RequestId == request));

        for (var i = 0; i <= ExecutionCorrelationTelemetry.MaximumRecentRequests; i++)
            recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: Guid.NewGuid()));
        Assert.InRange(recorder.Snapshot().Where(item => item.RequestId.HasValue).Select(item => item.RequestId).Distinct().Count(),
            1, ExecutionCorrelationTelemetry.MaximumRecentRequests);
        Assert.InRange(recorder.Snapshot().Sum(item => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item).Length),
            0, ExecutionCorrelationTelemetry.MaximumRetainedBytes);
        Assert.True(recorder.EvictedCount > 0);
    }

    [Fact]
    public void GlobalIdleRemainsUncorrelatedAndRequestIncompleteUntilEvaluationResponse()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var request = Guid.NewGuid();
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: request));
        recorder.RecordPsesBusy(true, 22);
        recorder.RecordPsesBusy(false, 23);
        var idle = Assert.Single(recorder.Snapshot(), item => item.Code == "PsesIdle");
        Assert.Null(idle.RequestId);
        Assert.Equal("GlobalObservation", idle.CorrelationConfidence);
        Assert.Equal("Incomplete", recorder.Classify(request));

        recorder.Record(new ExecutionCorrelationEvent("LspEvaluationResponse", RequestId: request,
            ProtocolRequestId: 17, ResponseSequence: 44, RuntimeSessionId: Guid.NewGuid(),
            RuntimeGeneration: 3, ProcessId: 123, EventOutcome: "Completed",
            CorrelationConfidence: "Correlated"));
        Assert.Equal("Correlated", recorder.Classify(request));
    }

    [Fact]
    public void LateOrDuplicateResponseEvidenceIsAmbiguousAndDisposalIsSafe()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var request = Guid.NewGuid();
        var response = new ExecutionCorrelationEvent("LspEvaluationResponse", RequestId: request,
            ProtocolRequestId: 2, ResponseSequence: 8, RuntimeSessionId: Guid.NewGuid(),
            RuntimeGeneration: 1, ProcessId: 456, CorrelationConfidence: "Correlated");
        recorder.Record(response);
        recorder.Record(response with { ResponseSequence = 9 });
        Assert.Equal("Ambiguous", recorder.Classify(request));
        recorder.Dispose();
        recorder.Record(new ExecutionCorrelationEvent("AfterDispose", RequestId: request));
        Assert.False(recorder.IsEnabled);
        Assert.Empty(recorder.Snapshot());
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("terminal")]
    [InlineData("process")]
    public void IdentityMismatchIsClassifiedStale(string mismatch)
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var request = Guid.NewGuid();
        var runtime = Guid.NewGuid();
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: request,
            RuntimeSessionId: runtime, RuntimeGeneration: 5, TerminalGeneration: 8, ProcessId: 300));
        var response = new ExecutionCorrelationEvent("LspEvaluationResponse", RequestId: request,
            ProtocolRequestId: 15, ResponseSequence: 23, RuntimeSessionId: runtime,
            RuntimeGeneration: 5, TerminalGeneration: 8, ProcessId: 300,
            CorrelationConfidence: "Correlated");
        response = mismatch switch
        {
            "runtime" => response with { RuntimeGeneration = 6 },
            "terminal" => response with { TerminalGeneration = 9 },
            "process" => response with { ProcessId = 301 },
            _ => response
        };
        recorder.Record(response);
        Assert.Equal("Stale", recorder.Classify(request));
    }

    [Fact]
    public void DebuggerOwnershipAndCancellationRemainDiagnosticClassifications()
    {
        var request = Guid.NewGuid();
        var recorder = new ExecutionCorrelationTelemetry(true);
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: request));
        recorder.Record(new ExecutionCorrelationEvent("LspEvaluationResponse", RequestId: request,
            ProtocolRequestId: 4, ResponseSequence: 7, RuntimeSessionId: Guid.NewGuid(),
            RuntimeGeneration: 2, ProcessId: 123, DebuggerMayOwnSession: true));
        Assert.Equal("Ambiguous", recorder.Classify(request));

        recorder.Record(new ExecutionCorrelationEvent("CancellationRequested", RequestId: request,
            EventOutcome: "Requested"));
        Assert.Equal("Canceled", recorder.Classify(request));
    }

    [Fact]
    public void LateResponseStaysWithItsOriginalRequestAndDoesNotCompleteNewRequest()
    {
        var recorder = new ExecutionCorrelationTelemetry(true);
        var earlier = Guid.NewGuid();
        var current = Guid.NewGuid();
        var runtime = Guid.NewGuid();
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: earlier,
            RuntimeSessionId: runtime, RuntimeGeneration: 3, TerminalGeneration: 5, ProcessId: 77));
        recorder.Record(new ExecutionCorrelationEvent("RequestCreated", RequestId: current,
            RuntimeSessionId: runtime, RuntimeGeneration: 3, TerminalGeneration: 5, ProcessId: 77));
        recorder.Record(new ExecutionCorrelationEvent("LspEvaluationResponse", RequestId: earlier,
            ProtocolRequestId: 10, ResponseSequence: 14, RuntimeSessionId: runtime,
            RuntimeGeneration: 3, TerminalGeneration: 5, ProcessId: 77,
            CorrelationConfidence: "Correlated"));
        Assert.Equal("Correlated", recorder.Classify(earlier));
        Assert.Equal("Incomplete", recorder.Classify(current));
    }

    [Fact]
    public void EventSchemaExcludesUserContentFields()
    {
        var fields = typeof(ExecutionCorrelationEvent).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(fields, name => name.Contains("script", StringComparison.OrdinalIgnoreCase) ||
            (name.Contains("output", StringComparison.OrdinalIgnoreCase) && name != nameof(ExecutionCorrelationEvent.OutputFramesSinceRegistration)) ||
            name.Contains("argument", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("environment", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PersistedReconciliationTraceSeparatesVisibilityValidationAndUiRejection()
    {
        DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings
        {
            IsDeveloperDiagnosticsEnabled = true,
            DeveloperDiagnosticsWriteJsonLines = true,
            DeveloperDiagnosticsWriteReadableLog = false
        }, "A02 P0 readiness trace test");
        var sessionDirectory = DeveloperDiagnostics.CurrentSessionDirectory!;
        var requestId = Guid.NewGuid();
        try
        {
            var recorder = new ExecutionCorrelationTelemetry(true, persist: true);
            recorder.Record(new ExecutionCorrelationEvent("StatusMarkerObserved", RequestId: requestId,
                TerminalGeneration: 7, ProcessId: 123));
            recorder.Record(new ExecutionCorrelationEvent("PromptCandidate", RequestId: requestId,
                TerminalGeneration: 7, ProcessId: 123, Reason: "prompt-before-status-only",
                StatusValidated: null, PostStatusPrompt: false, ActualReady: false));
            recorder.Record(new ExecutionCorrelationEvent("StatusValidated", RequestId: requestId,
                TerminalGeneration: 7, ProcessId: 123, StatusValidated: true, Outcome: "OK"));
            recorder.Record(new ExecutionCorrelationEvent("UiPromptReconciliationRejected", RequestId: requestId,
                TerminalGeneration: 7, ProcessId: 123, Reason: "runtime-session-mismatch",
                InteractiveState: "InteractiveCommandRunning", RuntimeState: "Ready", LeaseState: "released"));
            var trace = recorder.Snapshot();
            Assert.Equal(4, trace.Length);
            Assert.All(trace, item => Assert.Equal(requestId, item.RequestId));
            Assert.True(trace.Zip(trace.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
            Assert.Null(trace[0].StatusValidated);
            Assert.True(trace[2].StatusValidated);
            Assert.Equal("runtime-session-mismatch", trace[3].Reason);
        }
        finally
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "A02 P0 trace test cleanup");
        }
        var persisted = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "developer-diagnostics.ndjson"));
        Assert.Contains(requestId.ToString(), persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime-session-mismatch", persisted, StringComparison.Ordinal);
        Assert.Contains("InteractiveCommandRunning", persisted, StringComparison.Ordinal);
        Assert.Contains("statusValidated", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Read-Host value", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuresDisabledAndEnabledRecordingCosts()
    {
        const int samples = 100_000;
        var item = new ExecutionCorrelationEvent("PromptCandidate", RequestId: Guid.NewGuid());
        var disabled = new ExecutionCorrelationTelemetry(false);
        var enabled = new ExecutionCorrelationTelemetry(true);
        static (double ms, long bytes) Measure(ExecutionCorrelationTelemetry recorder, ExecutionCorrelationEvent item, int samples)
        {
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < samples; i++) recorder.Record(item);
            stopwatch.Stop();
            return (stopwatch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        var off = Measure(disabled, item, samples);
        var on = Measure(enabled, item, samples);
        output.WriteLine($"samples={samples}; disabledMs={off.ms:F3}; disabledBytes={off.bytes}; enabledMs={on.ms:F3}; enabledBytes={on.bytes}; retained={enabled.Snapshot().Length}; evicted={enabled.EvictedCount}");
        Assert.Empty(disabled.Snapshot());
        Assert.InRange(enabled.Snapshot().Length, 1, 1024);
    }
}
