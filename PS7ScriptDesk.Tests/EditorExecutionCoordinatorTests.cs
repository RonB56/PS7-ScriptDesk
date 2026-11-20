using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class EditorExecutionCoordinatorTests
{
    [Fact]
    public async Task GateOff_SelectsLegacyBackend()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(false));

        var decision = await coordinator.ObserveAsync(CreateRequest());

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, decision.SelectedBackend);
        Assert.Equal("GateOff", decision.SelectionReason);
        Assert.Equal(0, legacy.ExecutionCount);
        Assert.Equal(0, structured.ExecutionCount);
    }

    [Fact]
    public async Task GateOn_RuntimeIncompatible_SelectsLegacyWithExplicitReason()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(true), ExecutionBackendAvailability.RuntimeIncompatible);

        var decision = await coordinator.ObserveAsync(CreateRequest());

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, decision.SelectedBackend);
        Assert.Equal("StructuredUnavailableRuntimeIncompatible", decision.SelectionReason);
        Assert.Equal(ExecutionCompatibilityState.RuntimeIncompatible, decision.StructuredCompatibility);
    }

    [Fact]
    public async Task GateOn_InitializationFailed_SelectsLegacyWithExplicitReason()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(true), ExecutionBackendAvailability.InitializationFailed);

        var decision = await coordinator.ObserveAsync(CreateRequest());

        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, decision.SelectedBackend);
        Assert.Equal("StructuredInitializationFailed", decision.SelectionReason);
    }

    [Fact]
    public async Task GateOn_KnownInteractive_SelectsLegacy()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(true), classifier: new FakeClassifier(ExecutionRequestClassification.KnownInteractive));

        var decision = await coordinator.ObserveAsync(CreateRequest());

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, decision.SelectedBackend);
        Assert.Equal("InteractiveCapabilityRequired", decision.SelectionReason);
    }

    [Fact]
    public async Task RunSelection_UsesPsesSchedulerRegardlessOfClassifierResult()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, InteractiveStructuredCapabilities);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, new EditorExecutionFeatureGate(true),
            shadowMode: false,
            classifier: new FakeClassifier(ExecutionRequestClassification.KnownInteractive,
                new ExecutionCapabilityRequirements(RequiresInteractiveInput: true, RequiresCurrentScope: true,
                    RequiresTerminalInterrupt: true), "DirectRunSelectionReadHost"),
            directSelectionGate: new ExperimentalRunSelectionBridgeFeatureGate(true));

        var decision = await coordinator.ObserveAsync(CreateRequest() with { Mode = EditorExecutionMode.RunSelection, ExecuteInCurrentScope = true });

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.StructuredBackendId, decision.SelectedBackend);
        Assert.Equal("PsesSchedulerAdmission", decision.SelectionReason);
    }

    [Fact]
    public async Task RunSelection_OptOutCannotRestoreLegacyRoute()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, InteractiveStructuredCapabilities);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, new EditorExecutionFeatureGate(true),
            shadowMode: false,
            classifier: new FakeClassifier(ExecutionRequestClassification.KnownInteractive,
                new ExecutionCapabilityRequirements(RequiresInteractiveInput: true, RequiresCurrentScope: true,
                    RequiresTerminalInterrupt: true), "DirectRunSelectionReadHost"),
            directSelectionGate: new ExperimentalRunSelectionBridgeFeatureGate(false));

        var decision = await coordinator.ObserveAsync(CreateRequest() with { Mode = EditorExecutionMode.RunSelection, ExecuteInCurrentScope = true });

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.StructuredBackendId, decision.SelectedBackend);
        Assert.Equal("PsesSchedulerAdmission", decision.SelectionReason);
    }

    [Fact]
    public async Task DirectReadHostApproval_IsNarrowlyPropagatedToAuthoritativeAdapter()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, InteractiveStructuredCapabilities);
        using var coordinator = new EditorExecutionCoordinator(
            new IEditorExecutionBackend[] { legacy, structured }, new EditorExecutionFeatureGate(true),
            shadowMode: false,
            classifier: new FakeClassifier(ExecutionRequestClassification.KnownInteractive,
                new ExecutionCapabilityRequirements(RequiresInteractiveInput: true, RequiresCurrentScope: true,
                    RequiresTerminalInterrupt: true), "DirectRunSelectionReadHost"),
            directSelectionGate: new ExperimentalRunSelectionBridgeFeatureGate(true));
        var request = CreateRequest() with { Mode = EditorExecutionMode.RunSelection, ExecuteInCurrentScope = true };

        await coordinator.ExecuteAsync(request);

        Assert.True(structured.LastRequest?.DirectRunSelectionApproved);
        Assert.False(structured.LastRequest?.DirectReadHostSelectionApproved);
        Assert.Equal(1, structured.ExecutionCount);
        Assert.Equal(0, legacy.ExecutionCount);
    }

    [Fact]
    public async Task GateOn_UnknownUsesStructuredBackendWhenNoInteractiveCapabilityIsRequired()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(true), classifier: new FakeClassifier(ExecutionRequestClassification.Unknown));

        var decision = await coordinator.ObserveAsync(CreateRequest());

        Assert.Equal(EditorExecutionCoordinator.StructuredBackendId, decision.SelectedBackend);
        Assert.Equal("StructuredEligibleUnknown", decision.SelectionReason);
    }

    [Fact]
    public async Task GateOn_KnownNonInteractiveAndCompatible_SelectsStructured()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(true), classifier: new FakeClassifier(ExecutionRequestClassification.KnownNonInteractive));

        var decision = await coordinator.ObserveAsync(CreateRequest());

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.StructuredBackendId, decision.SelectedBackend);
        Assert.Equal("StructuredEligible", decision.SelectionReason);
    }

    [Fact]
    public async Task UnsupportedInteractiveRequirement_DowngradesToLegacyInsteadOfRejecting()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities);
        using var coordinator = CreateCoordinator(legacy, structured, new EditorExecutionFeatureGate(true), classifier: new FakeClassifier(ExecutionRequestClassification.KnownNonInteractive));

        var decision = await coordinator.ObserveAsync(CreateRequest(new ExecutionCapabilityRequirements(RequiresInteractiveInput: true)));

        Assert.True(decision.Accepted);
        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, decision.SelectedBackend);
        Assert.Equal("CapabilityNotSupported", decision.SelectionReason);
    }

    [Fact]
    public async Task AuthoritativeCoordinatorExecutesExactlyOnceAfterOnePreparedRoute()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities, TimeSpan.FromMilliseconds(20));
        using var coordinator = CreateCoordinator(legacy, null, new EditorExecutionFeatureGate(false), shadowMode: false);
        var request = CreateRequest();

        var decision = coordinator.Route(request);
        var result = await coordinator.ExecuteAsync(request);

        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, decision.SelectedBackend);
        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.Equal(1, legacy.ExecutionCount);
        Assert.Equal(request.RequestId, result.RequestId);
    }

    [Fact]
    public async Task DuplicateRequestIdIsRejectedWhileFirstExecutionIsActive()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities, TimeSpan.FromMilliseconds(40));
        using var coordinator = CreateCoordinator(legacy, null, new EditorExecutionFeatureGate(false), shadowMode: false);
        var request = CreateRequest();

        var first = coordinator.ExecuteAsync(request);
        var second = await coordinator.ExecuteAsync(request);
        var result = await first;

        Assert.Equal(EditorExecutionStatus.Rejected, second.Status);
        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.Equal(1, legacy.ExecutionCount);
    }

    [Fact]
    public async Task DifferentRequestCannotReplaceTheActiveSharedSessionOwner()
    {
        var backend = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities)
        {
            Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var coordinator = CreateCoordinator(backend, null, new EditorExecutionFeatureGate(false), shadowMode: false);
        var firstRequest = CreateRequest();
        var secondRequest = CreateRequest();

        var first = coordinator.ExecuteAsync(firstRequest);
        Assert.Equal(firstRequest.RequestId, coordinator.ActiveExecution?.RequestId);

        // A backend event and a prompt-looking output cannot release ownership.
        backend.PublishCompletion(firstRequest);
        var second = await coordinator.ExecuteAsync(secondRequest);
        Assert.Equal(EditorExecutionStatus.Rejected, second.Status);
        Assert.Equal(firstRequest.RequestId, coordinator.ActiveExecution?.RequestId);
        Assert.Equal(1, backend.ExecutionCount);

        backend.Release.SetResult(true);
        Assert.Equal(EditorExecutionStatus.Completed, (await first).Status);
        Assert.Null(coordinator.ActiveExecution);

        backend.Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = coordinator.ExecuteAsync(secondRequest);
        backend.PublishCompletion(firstRequest);
        Assert.Equal(secondRequest.RequestId, coordinator.ActiveExecution?.RequestId);
        backend.Release.SetResult(true);
        Assert.Equal(EditorExecutionStatus.Completed, (await next).Status);
        Assert.Equal(2, backend.ExecutionCount);
    }

    [Fact]
    public async Task ActiveExecutionSnapshotTracksLifecycleAndSelectedBackendCapability()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities, TimeSpan.FromMilliseconds(80));
        using var coordinator = CreateCoordinator(legacy, null, new EditorExecutionFeatureGate(false), shadowMode: false);
        var request = CreateRequest();

        var execution = coordinator.ExecuteAsync(request);
        await WaitUntilAsync(() => coordinator.ActiveExecution is not null);

        var active = coordinator.ActiveExecution;
        Assert.NotNull(active);
        Assert.Equal(request.RequestId, active.RequestId);
        Assert.Equal(EditorExecutionCoordinator.LegacyBackendId, active.BackendId);
        Assert.True(active.Capabilities.SupportsTerminalInterrupt);

        await execution;
        Assert.Null(coordinator.ActiveExecution);
    }

    [Fact]
    public async Task CompletedExecutionCarriesMonotonicDurationFromCoordinator()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities, TimeSpan.FromMilliseconds(80));
        using var coordinator = CreateCoordinator(legacy, null, new EditorExecutionFeatureGate(false), shadowMode: false);

        var result = await coordinator.ExecuteAsync(CreateRequest());

        Assert.Equal(EditorExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.MonotonicDuration);
        Assert.True(result.Duration >= TimeSpan.FromMilliseconds(70));
        Assert.Equal(result.MonotonicDuration, result.Duration);
    }

    [Fact]
    public async Task StructuredActiveExecutionSnapshotAdvertisesCancellationCapability()
    {
        var legacy = new FakeBackend(EditorExecutionCoordinator.LegacyBackendId, LegacyCapabilities);
        var structured = new FakeBackend(EditorExecutionCoordinator.StructuredBackendId, StructuredCapabilities, TimeSpan.FromMilliseconds(80));
        using var coordinator = CreateCoordinator(
            legacy,
            structured,
            new EditorExecutionFeatureGate(true),
            shadowMode: false,
            classifier: new FakeClassifier(ExecutionRequestClassification.KnownNonInteractive));
        var request = CreateRequest();
        coordinator.Route(request);

        var execution = coordinator.ExecuteAsync(request);
        await WaitUntilAsync(() => coordinator.ActiveExecution is not null);

        var active = coordinator.ActiveExecution;
        Assert.NotNull(active);
        Assert.Equal(EditorExecutionCoordinator.StructuredBackendId, active.BackendId);
        Assert.False(active.Capabilities.SupportsTerminalInterrupt);
        Assert.True(active.Capabilities.SupportsCancellation);

        await execution;
        Assert.Null(coordinator.ActiveExecution);
    }

    private static EditorExecutionCoordinator CreateCoordinator(
        FakeBackend legacy,
        FakeBackend? structured,
        EditorExecutionFeatureGate gate,
        ExecutionBackendAvailability structuredAvailability = ExecutionBackendAvailability.Available,
        IEditorExecutionRequestClassifier? classifier = null,
        bool shadowMode = true)
    {
        var backends = structured is null ? new IEditorExecutionBackend[] { legacy } : new IEditorExecutionBackend[] { legacy, structured };
        return new EditorExecutionCoordinator(backends, gate, shadowMode, structuredAvailability, classifier ?? new FakeClassifier(ExecutionRequestClassification.KnownNonInteractive));
    }

    private static EditorExecutionRequest CreateRequest(ExecutionCapabilityRequirements? requirements = null)
        => new(Guid.NewGuid(), 1, EditorExecutionMode.CurrentScope, "Test.ps1", "Write-Output test", CapabilityRequirements: requirements);

    private static readonly ExecutionBackendCapabilities LegacyCapabilities = new(true, true, true, false, true, false, true, true, true, true);
    private static readonly ExecutionBackendCapabilities StructuredCapabilities = new(false, false, true, true, false, true, true, true, true, true, true);
    private static readonly ExecutionBackendCapabilities InteractiveStructuredCapabilities = new(true, true, true, true, true, false, true, true, true, true, true);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(5);
        }

        Assert.True(condition(), "Condition did not become true within the test timeout.");
    }

    private sealed class FakeClassifier : IEditorExecutionRequestClassifier
    {
        private readonly ExecutionRequestClassification _classification;
        private readonly ExecutionCapabilityRequirements _requirements;
        private readonly string? _reason;

        public FakeClassifier(ExecutionRequestClassification classification,
            ExecutionCapabilityRequirements? requirements = null, string? reason = null)
        {
            _classification = classification;
            _requirements = requirements ?? new ExecutionCapabilityRequirements();
            _reason = reason;
        }

        public ExecutionRequestClassificationResult Classify(EditorExecutionRequest request)
            => new(_classification, _requirements, _reason ?? "Test classification");
    }

    private sealed class FakeBackend : IEditorExecutionBackend
    {
        private readonly TimeSpan _delay;

        public FakeBackend(string backendId, ExecutionBackendCapabilities capabilities, TimeSpan? delay = null)
        {
            BackendId = backendId;
            Capabilities = capabilities;
            _delay = delay ?? TimeSpan.Zero;
        }

        public string BackendId { get; }
        public ExecutionBackendCapabilities Capabilities { get; }
        public ExecutionBackendAvailability Availability => ExecutionBackendAvailability.Available;
        public string? AvailabilityReason => null;
        public int ExecutionCount { get; private set; }
        public EditorExecutionRequest? LastRequest { get; private set; }
        public TaskCompletionSource<bool>? Release { get; set; }
        public event Action<EditorExecutionEvent>? EventPublished;

        public void PublishCompletion(EditorExecutionRequest request)
            => EventPublished?.Invoke(new EditorExecutionEvent(EditorExecutionEventKind.Completed, request.RequestId, request.SessionGeneration, 1, Timestamp: DateTimeOffset.UtcNow, BackendId: BackendId));

        public async Task<EditorExecutionResult> ExecuteAsync(EditorExecutionRequest request, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            LastRequest = request;
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            EventPublished?.Invoke(new EditorExecutionEvent(EditorExecutionEventKind.Completed, request.RequestId, request.SessionGeneration, 1, Timestamp: now, BackendId: BackendId));
            return new EditorExecutionResult(request.RequestId, request.SessionGeneration, EditorExecutionStatus.Completed, Array.Empty<EditorOutputRecord>(), request.WorkingDirectory, null, null, now, now, BackendId);
        }
    }
}
