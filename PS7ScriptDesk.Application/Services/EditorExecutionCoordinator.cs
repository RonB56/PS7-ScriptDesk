using System.Collections.Concurrent;
using System.Diagnostics;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Application-owned authority for editor execution routing and dispatch.
/// </summary>
public sealed class EditorExecutionCoordinator : IEditorExecutionCoordinator
{
    public const string LegacyBackendId = "legacy-live-console";
    public const string StructuredBackendId = "structured-session-broker";
    public const string RollbackEnvironmentVariableName = "PS7SCRIPTDESK_C2_ROUTING_ROLLBACK";

    private static readonly ExecutionBackendCapabilities EmptyCapabilities = new(
        false, false, false, false, false, false, false, false, false, false);
    private readonly IReadOnlyDictionary<string, IEditorExecutionBackend> _backends;
    private readonly EditorExecutionFeatureGate _featureGate;
    private readonly ExecutionBackendAvailability _structuredAvailability;
    private readonly IEditorExecutionRequestClassifier _classifier;
    private readonly ConcurrentDictionary<Guid, byte> _activeRequests = new();
    private readonly ConcurrentDictionary<Guid, ExecutionRoutingDecision> _preparedDecisions = new();
    private ActiveEditorExecutionSnapshot? _activeExecution;
    private int _disposed;

    public EditorExecutionCoordinator(
        IEnumerable<IEditorExecutionBackend> backends,
        EditorExecutionFeatureGate featureGate,
        bool shadowMode = true,
        ExecutionBackendAvailability structuredAvailability = ExecutionBackendAvailability.Available,
        IEditorExecutionRequestClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        _backends = backends.ToDictionary(backend => backend.BackendId, StringComparer.OrdinalIgnoreCase);
        IsShadowMode = shadowMode;
        IsRollbackEnabled = IsDevelopmentRollbackEnabled();
        _structuredAvailability = structuredAvailability;
        _classifier = classifier ?? new UnknownExecutionRequestClassifier();
        foreach (var backend in _backends.Values)
        {
            backend.EventPublished += OnBackendEventPublished;
        }
    }

    public bool IsShadowMode { get; }

    public bool IsRollbackEnabled { get; }

    public ActiveEditorExecutionSnapshot? ActiveExecution => Volatile.Read(ref _activeExecution);

    public event Action<EditorExecutionEvent>? EventPublished;

    public event Action? ActiveExecutionChanged;

    public ExecutionRoutingDecision Route(EditorExecutionRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        var decision = SelectBackend(request);
        _preparedDecisions[request.RequestId] = decision;
        LogRoutingDecision(request, decision, "ExecutionRoutingSelected");
        return decision;
    }

    public Task<ExecutionRoutingDecision> ObserveAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var decision = SelectBackend(request);
        LogRoutingDecision(request, decision, "CanonicalRoutingObserved");
        return Task.FromResult(decision);
    }

    public async Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.StartNew();
        var decision = _preparedDecisions.TryRemove(request.RequestId, out var prepared)
            ? prepared
            : SelectBackend(request);

        if (!decision.Accepted || !_backends.TryGetValue(decision.BackendId, out var backend))
        {
            return Rejected(request, decision.SelectionReason ?? decision.Reason ?? "No compatible execution backend is available.", decision.BackendId);
        }

        if (IsShadowMode)
        {
            DeveloperDiagnostics.LogDecision(
                "Execution",
                "CanonicalExecutionSuppressed",
                "Canonical execution was suppressed because the coordinator remains in shadow mode.",
                "ShadowOnly",
                new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["backendId"] = backend.BackendId });
            return Rejected(request, "Shadow mode does not execute requests.");
        }

        if (!_activeRequests.TryAdd(request.RequestId, 0))
        {
            return Rejected(request, "A request with the same identifier is already executing.", backend.BackendId);
        }

        var activeExecution = new ActiveEditorExecutionSnapshot(
            request.RequestId,
            request.SessionGeneration,
            backend.BackendId,
            backend.Capabilities,
            DateTimeOffset.UtcNow);
        Volatile.Write(ref _activeExecution, activeExecution);
        DeveloperDiagnostics.LogStateTransition(
            "Execution",
            "ActiveEditorExecution",
            "Idle",
            "Running",
            "Coordinator published an active interruptible editor execution.",
            new Dictionary<string, object?>
            {
                ["requestId"] = request.RequestId,
                ["selectedBackend"] = backend.BackendId,
                ["supportsTerminalInterrupt"] = backend.Capabilities.SupportsTerminalInterrupt,
                ["supportsCancellation"] = backend.Capabilities.SupportsCancellation
            });
        ActiveExecutionChanged?.Invoke();

        try
        {
            DeveloperDiagnostics.LogOperationStart(
                "Execution",
                "CanonicalExecution",
                "Authoritative execution backend dispatch started.",
                request.RequestId.ToString("N"),
                new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["gateRequested"] = decision.GateRequested,
                    ["structuredAvailable"] = decision.StructuredAvailability == ExecutionBackendAvailability.Available,
                    ["compatibilityState"] = decision.StructuredCompatibility.ToString(),
                    ["classification"] = decision.Classification.ToString(),
                    ["requiredCapabilities"] = decision.RequiredCapabilities,
                    ["selectedBackend"] = decision.SelectedBackend,
                    ["selectionReason"] = decision.SelectionReason,
                    ["sessionGeneration"] = request.SessionGeneration
                });
            var result = await backend.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            DeveloperDiagnostics.LogOperationStop(
                "Execution",
                "CanonicalExecution",
                "Authoritative execution backend dispatch completed.",
                started.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["selectedBackend"] = backend.BackendId,
                    ["result"] = result.Status.ToString(),
                    ["durationMs"] = started.ElapsedMilliseconds
                });
            return result with { BackendId = backend.BackendId };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeveloperDiagnostics.LogInfo("Execution", "Authoritative execution canceled.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["selectedBackend"] = backend.BackendId, ["durationMs"] = started.ElapsedMilliseconds });
            throw;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("Execution", ex, "Authoritative execution backend dispatch failed.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["selectedBackend"] = backend.BackendId, ["durationMs"] = started.ElapsedMilliseconds });
            return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Failed, Array.Empty<EditorOutputRecord>(), request.WorkingDirectory, null, ex.Message, DateTimeOffset.UtcNow - started.Elapsed, DateTimeOffset.UtcNow, backend.BackendId);
        }
        finally
        {
            _activeRequests.TryRemove(request.RequestId, out _);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _activeExecution, null, activeExecution), activeExecution))
            {
                DeveloperDiagnostics.LogStateTransition(
                    "Execution",
                    "ActiveEditorExecution",
                    "Running",
                    "Idle",
                    "Coordinator cleared the active editor execution after backend completion.",
                    new Dictionary<string, object?>
                    {
                        ["requestId"] = request.RequestId,
                        ["selectedBackend"] = backend.BackendId
                    });
                ActiveExecutionChanged?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var backend in _backends.Values)
        {
            backend.EventPublished -= OnBackendEventPublished;
        }
        _preparedDecisions.Clear();
        _activeRequests.Clear();
        Interlocked.Exchange(ref _activeExecution, null);
        EventPublished = null;
        ActiveExecutionChanged = null;
    }

    private ExecutionRoutingDecision SelectBackend(EditorExecutionRequest request)
    {
        var classification = _classifier.Classify(request);
        var requiredCapabilities = MergeRequirements(request.CapabilityRequirements, classification.RequiredCapabilities);
        var structuredCompatibility = _structuredAvailability switch
        {
            ExecutionBackendAvailability.Available => ExecutionCompatibilityState.Compatible,
            ExecutionBackendAvailability.RuntimeIncompatible => ExecutionCompatibilityState.RuntimeIncompatible,
            ExecutionBackendAvailability.InitializationFailed => ExecutionCompatibilityState.InitializationFailed,
            _ => ExecutionCompatibilityState.Unknown
        };

        if (!_featureGate.IsStructuredExecutionEnabled)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "GateOff", null);
        }

        if (_structuredAvailability == ExecutionBackendAvailability.RuntimeIncompatible)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "StructuredUnavailableRuntimeIncompatible", "The structured runtime is incompatible with the loaded in-process SMA assembly.");
        }

        if (_structuredAvailability == ExecutionBackendAvailability.InitializationFailed)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "StructuredInitializationFailed", "Structured backend initialization failed during startup.");
        }

        if (classification.Classification == ExecutionRequestClassification.KnownInteractive)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "InteractiveCapabilityRequired", classification.Reason);
        }

        if (classification.Classification != ExecutionRequestClassification.KnownNonInteractive)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "CapabilityNotProven", classification.Reason);
        }

        if (!_backends.TryGetValue(StructuredBackendId, out var structured) || structured.Availability != ExecutionBackendAvailability.Available)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "StructuredUnavailable", structured?.AvailabilityReason ?? "The structured backend is not composed or available.");
        }

        var capabilityFailure = GetCapabilityFailure(requiredCapabilities, structured.Capabilities);
        if (capabilityFailure is not null)
        {
            return SelectLegacy(request, classification, requiredCapabilities, structuredCompatibility, "CapabilityNotSupported", capabilityFailure);
        }

        return new ExecutionRoutingDecision(
            request.RequestId,
            StructuredBackendId,
            structured.Capabilities,
            true,
            IsShadowMode,
            null,
            true,
            structured.Availability,
            structuredCompatibility,
            classification.Classification,
            requiredCapabilities,
            "StructuredEligible",
            null,
            DateTimeOffset.UtcNow);
    }

    private ExecutionRoutingDecision SelectLegacy(
        EditorExecutionRequest request,
        ExecutionRequestClassificationResult classification,
        ExecutionCapabilityRequirements requirements,
        ExecutionCompatibilityState compatibility,
        string reason,
        string? downgradeReason)
    {
        if (!_backends.TryGetValue(LegacyBackendId, out var legacy) || legacy.Availability != ExecutionBackendAvailability.Available)
        {
            return new ExecutionRoutingDecision(request.RequestId, LegacyBackendId, EmptyCapabilities, false, IsShadowMode, "The legacy backend is not available.", _featureGate.IsStructuredExecutionEnabled, _structuredAvailability, compatibility, classification.Classification, requirements, "LegacyUnavailable", downgradeReason, DateTimeOffset.UtcNow);
        }

        var capabilityFailure = GetCapabilityFailure(requirements, legacy.Capabilities);
        if (capabilityFailure is not null)
        {
            return new ExecutionRoutingDecision(request.RequestId, LegacyBackendId, legacy.Capabilities, false, IsShadowMode, capabilityFailure, _featureGate.IsStructuredExecutionEnabled, _structuredAvailability, compatibility, classification.Classification, requirements, reason, downgradeReason, DateTimeOffset.UtcNow);
        }

        return new ExecutionRoutingDecision(
            request.RequestId,
            LegacyBackendId,
            legacy.Capabilities,
            true,
            IsShadowMode,
            null,
            _featureGate.IsStructuredExecutionEnabled,
            _structuredAvailability,
            compatibility,
            classification.Classification,
            requirements,
            reason,
            downgradeReason,
            DateTimeOffset.UtcNow);
    }

    private void LogRoutingDecision(EditorExecutionRequest request, ExecutionRoutingDecision decision, string eventName)
    {
        DeveloperDiagnostics.LogDecision(
            "Execution",
            eventName,
            "Editor execution routing decision recorded.",
            decision.SelectedBackend,
            new Dictionary<string, object?>
            {
                ["requestId"] = decision.RequestId,
                ["gateRequested"] = decision.GateRequested,
                ["structuredAvailable"] = decision.StructuredAvailability == ExecutionBackendAvailability.Available,
                ["structuredAvailability"] = decision.StructuredAvailability.ToString(),
                ["compatibilityState"] = decision.StructuredCompatibility.ToString(),
                ["classification"] = decision.Classification.ToString(),
                ["requiredCapabilities"] = decision.RequiredCapabilities,
                ["selectedBackend"] = decision.SelectedBackend,
                ["selectionReason"] = decision.SelectionReason,
                ["fallbackOrDowngradeReason"] = decision.FallbackOrDowngradeReason,
                ["sessionGeneration"] = request.SessionGeneration,
                ["scriptLength"] = request.ScriptText?.Length ?? 0,
                ["contentOmitted"] = true
            });
    }

    private static ExecutionCapabilityRequirements MergeRequirements(ExecutionCapabilityRequirements? request, ExecutionCapabilityRequirements classified)
    {
        request ??= new ExecutionCapabilityRequirements();
        return new ExecutionCapabilityRequirements(
            request.RequiresInteractiveInput || classified.RequiresInteractiveInput,
            request.RequiresSecureInput || classified.RequiresSecureInput,
            request.RequiresCurrentScope || classified.RequiresCurrentScope,
            request.RequiresTerminalInterrupt || classified.RequiresTerminalInterrupt,
            request.RequiresScriptCallIsolation || classified.RequiresScriptCallIsolation,
            request.RequiresWorkingDirectoryContinuity || classified.RequiresWorkingDirectoryContinuity,
            request.RequiresStructuredStreams || classified.RequiresStructuredStreams);
    }

    private static string? GetCapabilityFailure(ExecutionCapabilityRequirements requirements, ExecutionBackendCapabilities capabilities)
    {
        if (requirements.RequiresInteractiveInput && !capabilities.SupportsInteractiveInput) return "Interactive input is not supported by the selected backend.";
        if (requirements.RequiresSecureInput && !capabilities.SupportsSecureInput) return "Secure input is not supported by the selected backend.";
        if (requirements.RequiresCurrentScope && !capabilities.SupportsCurrentScope) return "Current-scope execution is not supported by the selected backend.";
        if (requirements.RequiresTerminalInterrupt && !capabilities.SupportsTerminalInterrupt) return "Terminal interrupt is not supported by the selected backend.";
        if (requirements.RequiresScriptCallIsolation && !capabilities.SupportsScriptCallIsolation) return "Script-call isolation is not supported by the selected backend.";
        if (requirements.RequiresWorkingDirectoryContinuity && !capabilities.SupportsWorkingDirectoryTracking) return "Working-directory continuity is not supported by the selected backend.";
        if (requirements.RequiresStructuredStreams && !capabilities.SupportsStructuredStreams) return "Structured streams are not supported by the selected backend.";
        return null;
    }

    private static EditorExecutionResult Rejected(EditorExecutionRequest request, string reason, string backendId = "none")
    {
        var now = DateTimeOffset.UtcNow;
        return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Rejected, Array.Empty<EditorOutputRecord>(), request.WorkingDirectory, null, reason, now, now, backendId, reason);
    }

    private void OnBackendEventPublished(EditorExecutionEvent executionEvent) => EventPublished?.Invoke(executionEvent);

    private static bool IsDevelopmentRollbackEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RollbackEnvironmentVariableName);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EditorExecutionCoordinator));
        }
    }

    private sealed class UnknownExecutionRequestClassifier : IEditorExecutionRequestClassifier
    {
        public ExecutionRequestClassificationResult Classify(EditorExecutionRequest request)
            => new(ExecutionRequestClassification.Unknown, new ExecutionCapabilityRequirements(RequiresWorkingDirectoryContinuity: true), "No request classifier was configured.");
    }
}
