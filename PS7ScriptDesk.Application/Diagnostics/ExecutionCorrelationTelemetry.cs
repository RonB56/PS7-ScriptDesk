using System.Diagnostics;
using System.Text.Json;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Opt-in, content-free observations. This type has no execution authority.</summary>
public sealed class ExecutionCorrelationTelemetry : IDisposable
{
    public const int MaximumEventsPerRequest = 250;
    public const int MaximumRecentRequests = 20;
    public const int MaximumRetainedBytes = 2 * 1024 * 1024;
    private const int MaximumFieldLength = 256;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Queue<ExecutionCorrelationEvent>> _byRequest = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _requestOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _requestNodes = new(StringComparer.Ordinal);
    private readonly bool _optedIn;
    private readonly bool _persist;
    private readonly Action<ExecutionCorrelationEvent>? _observer;
    private int _retainedBytes;
    private long _sequence;
    private long _evicted;
    private long _failures;
    private long _persistedBytes;
    private int _persistedEvents;
    private volatile bool _disposed;

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

    public bool IsEnabled => !_disposed && _optedIn && (!_persist || DeveloperDiagnostics.IsEnabled);

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
    public ExecutionCorrelationEvent[] Snapshot()
    {
        lock (_syncRoot) return _byRequest.Values.SelectMany(queue => queue).OrderBy(item => item.Sequence).ToArray();
    }

    public string Classify(Guid requestId)
    {
        var events = Snapshot().Where(item => item.RequestId == requestId).ToArray();
        if (events.Length == 0) return "Incomplete";
        if (events.Any(item => item.EventOutcome?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true ||
            item.Code.Contains("Cancel", StringComparison.OrdinalIgnoreCase))) return "Canceled";
        if (events.Any(item => item.EventOutcome == "Faulted" || item.Code.Contains("Fault", StringComparison.OrdinalIgnoreCase))) return "Faulted";
        if (events.Any(item => item.CorrelationConfidence == "Stale" || item.RejectionReason?.StartsWith("stale-", StringComparison.Ordinal) == true)) return "Stale";
        var responses = events.Where(item => item.Code == "LspEvaluationResponse").ToArray();
        if (responses.Length > 1) return "Ambiguous";
        // Global PSES idle has no request identity and is intentionally absent from
        // this request-scoped evidence set. Without a correlated response, evidence
        // is incomplete; timing alone cannot make it ambiguous for this request.
        if (responses.Length == 0) return "Incomplete";
        var request = events.FirstOrDefault(item => item.Code == "RequestCreated");
        var response = responses[0];
        if (request.RequestId.HasValue &&
            (Mismatch(request.RuntimeSessionId, response.RuntimeSessionId) ||
             Mismatch(request.RuntimeGeneration, response.RuntimeGeneration) ||
             Mismatch(request.TerminalGeneration, response.TerminalGeneration) ||
             Mismatch(request.ProcessId, response.ProcessId))) return "Stale";
        if (response.ProtocolRequestId is null || response.ResponseSequence is null ||
            response.RuntimeSessionId is null || response.RuntimeGeneration is null || response.ProcessId is null)
            return "Partially correlated";
        if (response.CorrelationConfidence == "Rejected" || response.DebuggerMayOwnSession == true) return "Ambiguous";
        return "Correlated";
    }

    private static bool Mismatch<T>(T? expected, T? actual) where T : struct =>
        expected.HasValue && actual.HasValue && !EqualityComparer<T>.Default.Equals(expected.Value, actual.Value);

    public void RecordPsesBusy(bool busy, long? notificationSequence = null) => Record(new ExecutionCorrelationEvent(
        busy ? "PsesBusy" : "PsesIdle", RequestCorrelation: "global-uncorrelated",
        EventSource: "PSES", EventType: busy ? "GlobalBusyNotification" : "GlobalIdleNotification",
        CorrelationConfidence: "GlobalObservation", Reason: busy ? "busy" : "idle", PsesNotificationSequence: notificationSequence));

    public void Record(ExecutionCorrelationEvent item)
    {
        if (!IsEnabled) return;
        ExecutionCorrelationEvent recorded;
        try
        {
            recorded = Normalize(item with
            {
                EventSource = item.EventSource ?? "Application",
                EventType = item.EventType ?? item.Code,
                CorrelationConfidence = item.CorrelationConfidence ?? (item.RequestId.HasValue ? "RequestIdentity" : "Uncorrelated")
            });
            lock (_syncRoot)
            {
                if (_disposed) return;
                recorded = recorded with
                {
                    Sequence = ++_sequence,
                    Timestamp = Stopwatch.GetTimestamp(),
                    TimestampSource = "Stopwatch.GetTimestamp"
                };
                var key = recorded.RequestId?.ToString("N") ?? "<global>";
                if (!_byRequest.TryGetValue(key, out var queue))
                {
                    if (key != "<global>")
                    {
                        while (_requestNodes.Count >= MaximumRecentRequests)
                            EvictRequest(_requestOrder.First!.Value);
                        _requestNodes[key] = _requestOrder.AddLast(key);
                    }
                    queue = new Queue<ExecutionCorrelationEvent>();
                    _byRequest[key] = queue;
                }
                else if (key != "<global>" && _requestNodes.TryGetValue(key, out var node))
                {
                    _requestOrder.Remove(node);
                    _requestNodes[key] = _requestOrder.AddLast(key);
                }
                queue.Enqueue(recorded);
                _retainedBytes += EstimateBytes(recorded);
                while (queue.Count > MaximumEventsPerRequest) EvictOldest(queue, key);
                while (_retainedBytes > MaximumRetainedBytes && _byRequest.Count > 0)
                {
                    var oldest = _byRequest.OrderBy(pair => pair.Value.Count == 0 ? long.MaxValue : pair.Value.Peek().Sequence)
                        .FirstOrDefault(pair => pair.Value.Count > 0);
                    if (oldest.Value is null) break;
                    EvictOldest(oldest.Value, oldest.Key);
                }
            }
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            return;
        }

        try { _observer?.Invoke(recorded); }
        catch { Interlocked.Increment(ref _failures); }
        if (_persist) PersistBounded(recorded);
    }

    private static ExecutionCorrelationEvent Normalize(ExecutionCorrelationEvent item) => item with
    {
        Code = Limit(item.Code) ?? "Unknown", EventSource = Limit(item.EventSource), EventType = Limit(item.EventType),
        CorrelationConfidence = Limit(item.CorrelationConfidence),
        RejectionReason = Limit(item.RejectionReason), RequestCorrelation = Limit(item.RequestCorrelation),
        PsesVersion = Limit(item.PsesVersion), Outcome = Limit(item.Outcome), Reason = Limit(item.Reason),
        InteractiveState = Limit(item.InteractiveState), RuntimeState = Limit(item.RuntimeState),
        LeaseState = Limit(item.LeaseState), DebuggerState = Limit(item.DebuggerState),
        EventOutcome = Limit(item.EventOutcome ?? item.Outcome)
    };

    private static string? Limit(string? value) => value is { Length: > MaximumFieldLength } ? value[..MaximumFieldLength] : value;
    private static int EstimateBytes(ExecutionCorrelationEvent item) => JsonSerializer.SerializeToUtf8Bytes(item).Length;

    private void EvictRequest(string key)
    {
        if (!_byRequest.Remove(key, out var queue)) return;
        while (queue.Count > 0)
        {
            _retainedBytes -= EstimateBytes(queue.Dequeue());
            Interlocked.Increment(ref _evicted);
        }
        if (_requestNodes.Remove(key, out var node)) _requestOrder.Remove(node);
    }

    private void EvictOldest(Queue<ExecutionCorrelationEvent> queue, string key)
    {
        if (queue.Count == 0) return;
        _retainedBytes -= EstimateBytes(queue.Dequeue());
        Interlocked.Increment(ref _evicted);
        if (queue.Count == 0 && key != "<global>")
        {
            _byRequest.Remove(key);
            if (_requestNodes.Remove(key, out var node)) _requestOrder.Remove(node);
        }
    }

    private void PersistBounded(ExecutionCorrelationEvent item)
    {
        try
        {
            var estimate = EstimateBytes(item) + 2048; // Conservative allowance for DeveloperDiagnostics record framing.
            if (Interlocked.Increment(ref _persistedEvents) > MaximumRecentRequests * MaximumEventsPerRequest ||
                Interlocked.Add(ref _persistedBytes, estimate) > MaximumRetainedBytes)
                return;
            DeveloperDiagnostics.LogInfo("ExecutionCorrelation", item.Code, new Dictionary<string, object?>
            {
                ["sequence"] = item.Sequence, ["monotonicTimestamp"] = item.Timestamp,
                ["timestampSource"] = item.TimestampSource, ["stopwatchFrequency"] = Stopwatch.Frequency,
                ["requestId"] = item.RequestId, ["protocolRequestId"] = item.ProtocolRequestId,
                ["psesVersion"] = item.PsesVersion, ["responseSequence"] = item.ResponseSequence,
                ["idleSequence"] = item.IdleSequence, ["psesNotificationSequence"] = item.PsesNotificationSequence,
                ["requestCorrelation"] = item.RequestCorrelation, ["eventSource"] = item.EventSource,
                ["eventType"] = item.EventType, ["eventOutcome"] = item.EventOutcome,
                ["correlationConfidence"] = item.CorrelationConfidence, ["rejectionReason"] = item.RejectionReason,
                ["runtimeSessionId"] = item.RuntimeSessionId, ["runtimeGeneration"] = item.RuntimeGeneration,
                ["terminalGeneration"] = item.TerminalGeneration, ["processId"] = item.ProcessId,
                ["processStartUtcTicks"] = item.ProcessStartUtcTicks, ["leaseId"] = item.LeaseId,
                ["debuggerSessionId"] = item.DebuggerSessionId, ["debuggerState"] = item.DebuggerState,
                ["debuggerMayOwnSession"] = item.DebuggerMayOwnSession, ["outcome"] = item.Outcome,
                ["reason"] = item.Reason, ["statusValidated"] = item.StatusValidated,
                ["postStatusPrompt"] = item.PostStatusPrompt, ["cursorReady"] = item.CursorReady,
                ["identityValid"] = item.IdentityValid, ["echoObserved"] = item.EchoObserved,
                ["shadowReady"] = item.ShadowReady, ["actualReady"] = item.ActualReady,
                ["interactiveState"] = item.InteractiveState, ["runtimeState"] = item.RuntimeState,
                ["leaseState"] = item.LeaseState,
                ["outputFramesSinceRegistration"] = item.OutputFramesSinceRegistration,
                ["evictedRecords"] = EvictedCount, ["recordFailures"] = FailureCount, ["contentOmitted"] = true
            });
        }
        catch { Interlocked.Increment(ref _failures); }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _byRequest.Clear(); _requestOrder.Clear(); _requestNodes.Clear(); _retainedBytes = 0;
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
    long Timestamp = 0,
    string? TimestampSource = null,
    string? EventSource = null,
    string? EventType = null,
    string? EventOutcome = null,
    string? CorrelationConfidence = null,
    string? RejectionReason = null,
    Guid? LeaseId = null,
    Guid? DebuggerSessionId = null,
    string? DebuggerState = null,
    bool? DebuggerMayOwnSession = null);
