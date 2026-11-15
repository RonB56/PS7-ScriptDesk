using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Opt-in, content-free observations. This type has no execution authority.</summary>
public sealed class ExecutionCorrelationTelemetry
{
    private const int Capacity = 1024;
    private readonly ConcurrentQueue<ExecutionCorrelationEvent> _recent = new();
    private readonly bool _optedIn;
    private readonly bool _persist;
    private readonly Action<ExecutionCorrelationEvent>? _observer;
    private int _count;
    private long _sequence;
    private long _evicted;
    private long _failures;

    public static ExecutionCorrelationTelemetry Shared { get; } = new(ReadOptIn(), true);

    private static bool ReadOptIn()
    {
        try { return string.Equals(Environment.GetEnvironmentVariable("PS7SD_EXECUTION_TELEMETRY"), "1", StringComparison.Ordinal); }
        catch { return false; }
    }

    public ExecutionCorrelationTelemetry(bool optedIn, bool persist = false, Action<ExecutionCorrelationEvent>? observer = null)
    {
        _optedIn = optedIn;
        _persist = persist;
        _observer = observer;
    }

    public bool IsEnabled => _optedIn && (!_persist || DeveloperDiagnostics.IsEnabled);

    public void LogActivation()
    {
        DeveloperDiagnostics.LogInfo("Startup", "B1 execution telemetry activation evaluated.",
            new Dictionary<string, object?>
            {
                ["executionTelemetryOptedIn"] = _optedIn,
                ["developerDiagnosticsEnabled"] = DeveloperDiagnostics.IsEnabled,
                ["executionTelemetryEnabled"] = IsEnabled,
                ["recorderModuleVersionId"] = typeof(ExecutionCorrelationTelemetry).Module.ModuleVersionId,
                ["processId"] = Environment.ProcessId,
                ["contentOmitted"] = true
            });
    }
    public long EvictedCount => Interlocked.Read(ref _evicted);
    public long FailureCount => Interlocked.Read(ref _failures);
    public ExecutionCorrelationEvent[] Snapshot() => _recent.ToArray();

    public void RecordPsesBusy(bool busy, long? notificationSequence = null) => Record(new ExecutionCorrelationEvent(
        busy ? "PsesBusy" : "PsesIdle", RequestCorrelation: "global-uncorrelated",
        Reason: busy ? "busy" : "idle", PsesNotificationSequence: notificationSequence));

    public void Record(ExecutionCorrelationEvent item)
    {
        if (!IsEnabled) return;
        try
        {
            item = item with
            {
                Sequence = Interlocked.Increment(ref _sequence),
                Timestamp = Stopwatch.GetTimestamp()
            };
            _recent.Enqueue(item);
            if (Interlocked.Increment(ref _count) > Capacity && _recent.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
                Interlocked.Increment(ref _evicted);
            }
            _observer?.Invoke(item);
            if (_persist)
            {
                DeveloperDiagnostics.LogInfo("ExecutionCorrelation", item.Code, new Dictionary<string, object?>
                {
                    ["sequence"] = item.Sequence,
                    ["monotonicTimestamp"] = item.Timestamp,
                    ["stopwatchFrequency"] = Stopwatch.Frequency,
                    ["requestId"] = item.RequestId,
                    ["protocolRequestId"] = item.ProtocolRequestId,
                    ["psesVersion"] = item.PsesVersion,
                    ["responseSequence"] = item.ResponseSequence,
                    ["idleSequence"] = item.IdleSequence,
                    ["psesNotificationSequence"] = item.PsesNotificationSequence,
                    ["requestCorrelation"] = item.RequestCorrelation,
                    ["runtimeSessionId"] = item.RuntimeSessionId,
                    ["runtimeGeneration"] = item.RuntimeGeneration,
                    ["terminalGeneration"] = item.TerminalGeneration,
                    ["processId"] = item.ProcessId,
                    ["processStartUtcTicks"] = item.ProcessStartUtcTicks,
                    ["outcome"] = item.Outcome,
                    ["reason"] = item.Reason,
                    ["statusValidated"] = item.StatusValidated,
                    ["postStatusPrompt"] = item.PostStatusPrompt,
                    ["cursorReady"] = item.CursorReady,
                    ["identityValid"] = item.IdentityValid,
                    ["echoObserved"] = item.EchoObserved,
                    ["shadowReady"] = item.ShadowReady,
                    ["actualReady"] = item.ActualReady,
                    ["interactiveState"] = item.InteractiveState,
                    ["runtimeState"] = item.RuntimeState,
                    ["leaseState"] = item.LeaseState,
                    ["outputFramesSinceRegistration"] = item.OutputFramesSinceRegistration,
                    ["evictedRecords"] = EvictedCount,
                    ["recordFailures"] = FailureCount,
                    ["contentOmitted"] = true
                });
            }
        }
        catch
        {
            Interlocked.Increment(ref _failures);
        }
    }
}

public readonly record struct ExecutionCorrelationEvent(
    string Code,
    Guid? RequestId = null,
    int? ProtocolRequestId = null,
    string? PsesVersion = null,
    long? ResponseSequence = null,
    long? IdleSequence = null,
    long? PsesNotificationSequence = null,
    string? RequestCorrelation = null,
    Guid? RuntimeSessionId = null,
    long? RuntimeGeneration = null,
    int? TerminalGeneration = null,
    int? ProcessId = null,
    long? ProcessStartUtcTicks = null,
    string? Outcome = null,
    string? Reason = null,
    bool? StatusValidated = null,
    bool? PostStatusPrompt = null,
    bool? CursorReady = null,
    bool? IdentityValid = null,
    bool? EchoObserved = null,
    bool? ShadowReady = null,
    bool? ActualReady = null,
    string? InteractiveState = null,
    string? RuntimeState = null,
    string? LeaseState = null,
    long? OutputFramesSinceRegistration = null,
    long Sequence = 0,
    long Timestamp = 0);
