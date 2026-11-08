using System;
using System.Collections.Generic;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class TerminalOutputMultiplexer
{
    private readonly object _syncRoot = new();
    private readonly Queue<TerminalOutputEnvelope> _pendingNotifications = new();
    private long _sequence;
    private InteractiveTerminalSnapshot _interactiveSnapshot = new(
        0,
        InteractiveTerminalState.Unavailable,
        "Terminal state has not been initialized.",
        DateTimeOffset.UtcNow);
    private Guid? _activeEditorRequestId;
    private Guid? _activeInternalCommandRequestId;
    private bool _isPublishingNotifications;

    public event Action<TerminalOutputEnvelope>? OutputPublished;

    public InteractiveTerminalSnapshot InteractiveSnapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _interactiveSnapshot;
            }
        }
    }

    public InteractiveTerminalState InteractiveState
    {
        get
        {
            lock (_syncRoot)
            {
                return _interactiveSnapshot.State;
            }
        }
    }

    public void SetInteractiveState(InteractiveTerminalState state, string? reason = null)
    {
        lock (_syncRoot)
        {
            _interactiveSnapshot = _interactiveSnapshot with
            {
                State = state,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    public bool TryReplaceInteractiveGeneration(int generation, InteractiveTerminalState state, string? reason = null)
    {
        lock (_syncRoot)
        {
            if (generation < _interactiveSnapshot.Generation)
            {
                return false;
            }

            // The PSES output reader may observe the prompt before the
            // session-start lifecycle event. Preserve the ready state for the
            // same generation so the presentation model stays aligned with
            // the execution-admission coordinator.
            if (generation == _interactiveSnapshot.Generation &&
                _interactiveSnapshot.State == InteractiveTerminalState.InteractiveIdleAtPrompt &&
                state == InteractiveTerminalState.Starting)
            {
                return true;
            }

            var generationChanged = generation != _interactiveSnapshot.Generation;
            _interactiveSnapshot = new InteractiveTerminalSnapshot(
                generation,
                state,
                reason,
                DateTimeOffset.UtcNow);
            if (generationChanged)
            {
                _activeInternalCommandRequestId = null;
            }
            return true;
        }
    }

    public bool TryBeginEditorExecution(
        Guid requestId,
        EditorExecutionAdmissionFacts admissionFacts,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(admissionFacts);
        lock (_syncRoot)
        {
            if (!EditorExecutionAdmissionPolicy.CanStart(admissionFacts.InteractiveState, admissionFacts))
            {
                rejectionReason = EditorExecutionAdmissionPolicy.ExplainRejection(admissionFacts.InteractiveState);
                return false;
            }

            if (_activeEditorRequestId.HasValue || _activeInternalCommandRequestId.HasValue)
            {
                rejectionReason = "Another editor or internal terminal operation is already active.";
                return false;
            }

            _activeEditorRequestId = requestId;
            rejectionReason = string.Empty;
            return true;
        }
    }

    public void EndEditorExecution(Guid requestId)
    {
        lock (_syncRoot)
        {
            if (_activeEditorRequestId == requestId)
            {
                _activeEditorRequestId = null;
            }
        }
    }

    public bool TryBeginInternalCommand(
        Guid requestId,
        int generation,
        EditorExecutionAdmissionFacts admissionFacts,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(admissionFacts);
        lock (_syncRoot)
        {
            if (generation != _interactiveSnapshot.Generation ||
                !EditorExecutionAdmissionPolicy.CanStart(admissionFacts.InteractiveState, admissionFacts))
            {
                rejectionReason = generation != _interactiveSnapshot.Generation
                    ? "The terminal generation changed before internal command admission."
                    : EditorExecutionAdmissionPolicy.ExplainRejection(admissionFacts.InteractiveState);
                return false;
            }

            if (_activeEditorRequestId.HasValue || _activeInternalCommandRequestId.HasValue)
            {
                rejectionReason = "Another editor or internal terminal operation already owns admission.";
                return false;
            }

            _activeInternalCommandRequestId = requestId;
            _interactiveSnapshot = _interactiveSnapshot with
            {
                State = InteractiveTerminalState.InteractiveCommandRunning,
                Reason = "An internal terminal command owns interactive admission.",
                Timestamp = DateTimeOffset.UtcNow
            };
            rejectionReason = string.Empty;
            return true;
        }
    }

    public bool EndInternalCommand(Guid requestId, int generation, InteractiveTerminalState nextState, string? reason = null)
    {
        lock (_syncRoot)
        {
            if (_activeInternalCommandRequestId != requestId || _interactiveSnapshot.Generation != generation)
            {
                return false;
            }

            _activeInternalCommandRequestId = null;
            _interactiveSnapshot = _interactiveSnapshot with
            {
                State = nextState,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public TerminalOutputEnvelope PublishInteractive(EditorOutputStreamKind streamKind, string payload)
    {
        return PublishInteractive(_interactiveSnapshot.Generation, streamKind, payload);
    }

    public TerminalOutputEnvelope PublishInteractive(int interactiveTerminalSessionGeneration, EditorOutputStreamKind streamKind, string payload)
    {
        return Publish(
            TerminalOutputSource.InteractiveTerminal,
            requestId: null,
            brokerSessionGeneration: 0,
            interactiveTerminalSessionGeneration,
            rendererGeneration: interactiveTerminalSessionGeneration,
            sourceSequence: 0,
            streamKind,
            payload);
    }

    public TerminalOutputEnvelope PublishEditor(EditorOutputRecord output)
    {
        if (ContainsPrivateProtocol(output.Payload))
        {
            throw new InvalidOperationException("Structured editor output contains private ScriptDesk terminal protocol.");
        }

        return Publish(
            TerminalOutputSource.StructuredEditor,
            output.RequestId,
            output.SessionGeneration,
            _interactiveSnapshot.Generation,
            _interactiveSnapshot.Generation,
            output.Sequence,
            output.StreamKind,
            output.Payload);
    }

    public static bool ContainsPrivateProtocol(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        return payload.Contains("EXEC_START", StringComparison.Ordinal) ||
               payload.Contains("EXEC_DONE", StringComparison.Ordinal) ||
               payload.Contains("LOCATION_", StringComparison.Ordinal) ||
               payload.Contains("DISPATCH_DIAG", StringComparison.Ordinal) ||
               payload.Contains("hidden-helper", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("PSSTUDIO", StringComparison.Ordinal);
    }

    private TerminalOutputEnvelope Publish(
        TerminalOutputSource source,
        Guid? requestId,
        int brokerSessionGeneration,
        int interactiveTerminalSessionGeneration,
        int rendererGeneration,
        long sourceSequence,
        EditorOutputStreamKind streamKind,
        string payload)
    {
        payload ??= string.Empty;
        TerminalCriticalTrace.LogStage(
            "TerminalOutputMultiplexer.Publish.Begin",
            new Dictionary<string, object?>
            {
                ["source"] = source.ToString(),
                ["brokerSessionGeneration"] = brokerSessionGeneration,
                ["terminalSessionGeneration"] = interactiveTerminalSessionGeneration,
                ["rendererGeneration"] = rendererGeneration,
                ["sourceSequence"] = sourceSequence,
                ["streamKind"] = streamKind.ToString(),
                ["outputCharacterLength"] = payload?.Length ?? 0,
                ["contentOmitted"] = true
            });
        TerminalOutputEnvelope envelope;
        var shouldDrainNotifications = false;
        lock (_syncRoot)
        {
            envelope = new TerminalOutputEnvelope(
                Interlocked.Increment(ref _sequence),
                source,
                requestId,
                brokerSessionGeneration,
                interactiveTerminalSessionGeneration,
                rendererGeneration,
                sourceSequence,
                streamKind,
                payload!,
                DateTimeOffset.UtcNow);
            _pendingNotifications.Enqueue(envelope);
            if (!_isPublishingNotifications)
            {
                _isPublishingNotifications = true;
                shouldDrainNotifications = true;
            }
        }
        TerminalCriticalTrace.LogStage(
            "TerminalOutputMultiplexer.Publish.EnvelopeCreated",
            new Dictionary<string, object?>
            {
                ["source"] = envelope.Source.ToString(),
                ["sequence"] = envelope.Sequence,
                ["brokerSessionGeneration"] = envelope.BrokerSessionGeneration,
                ["terminalSessionGeneration"] = envelope.InteractiveTerminalSessionGeneration,
                ["rendererGeneration"] = envelope.RendererGeneration,
                ["sourceSequence"] = envelope.SourceSequence,
                ["streamKind"] = envelope.StreamKind.ToString(),
                ["outputCharacterLength"] = envelope.Payload?.Length ?? 0,
                ["contentOmitted"] = true
            });

        if (shouldDrainNotifications)
        {
            DrainNotifications();
        }

        return envelope;
    }

    private void DrainNotifications()
    {
        while (true)
        {
            TerminalOutputEnvelope envelope;
            lock (_syncRoot)
            {
                if (!_pendingNotifications.TryDequeue(out envelope!))
                {
                    _isPublishingNotifications = false;
                    return;
                }
            }

            var handlers = OutputPublished;
            if (handlers is null)
            {
                continue;
            }

            foreach (Action<TerminalOutputEnvelope> handler in handlers.GetInvocationList())
            {
                var metadata = TerminalCriticalTrace.CreateDelegateMetadata(handler);
                metadata["source"] = envelope.Source.ToString();
                metadata["sequence"] = envelope.Sequence;
                metadata["brokerSessionGeneration"] = envelope.BrokerSessionGeneration;
                metadata["terminalSessionGeneration"] = envelope.InteractiveTerminalSessionGeneration;
                metadata["rendererGeneration"] = envelope.RendererGeneration;
                metadata["sourceSequence"] = envelope.SourceSequence;
                metadata["streamKind"] = envelope.StreamKind.ToString();
                metadata["outputCharacterLength"] = envelope.Payload?.Length ?? 0;
                metadata["contentOmitted"] = true;
                try
                {
                    TerminalCriticalTrace.LogStage("TerminalOutputMultiplexer.OutputPublishedSubscriber.Begin", metadata);
                    handler(envelope);
                    TerminalCriticalTrace.LogStage("TerminalOutputMultiplexer.OutputPublishedSubscriber.End", metadata);
                }
                catch (Exception ex)
                {
                    TerminalCriticalTrace.LogException(
                        "TerminalOutputMultiplexer.OutputPublishedSubscriber.Exception",
                        ex,
                        metadata);
                    AppLogger.Warning("TerminalOutputMultiplexer", $"Renderer output subscriber failed. Source={envelope.Source}, Sequence={envelope.Sequence}, ErrorType={ex.GetType().Name}.");
                }
            }
        }
    }
}
