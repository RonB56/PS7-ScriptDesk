using System;
using System.Collections.Generic;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class TerminalOutputMultiplexer
{
    private readonly object _syncRoot = new();
    private readonly List<TerminalOutputEnvelope> _published = [];
    private readonly Queue<TerminalOutputEnvelope> _pendingNotifications = new();
    private long _sequence;
    private InteractiveTerminalSnapshot _interactiveSnapshot = new(
        0,
        InteractiveTerminalState.Unavailable,
        "Terminal state has not been initialized.",
        DateTimeOffset.UtcNow);
    private Guid? _activeEditorRequestId;
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

    public IReadOnlyList<TerminalOutputEnvelope> Published
    {
        get
        {
            lock (_syncRoot)
            {
                return _published.ToArray();
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

            _interactiveSnapshot = new InteractiveTerminalSnapshot(
                generation,
                state,
                reason,
                DateTimeOffset.UtcNow);
            return true;
        }
    }

    public bool TryBeginEditorExecution(Guid requestId, out string rejectionReason)
    {
        lock (_syncRoot)
        {
            if (!EditorExecutionAdmissionPolicy.CanStart(_interactiveSnapshot.State))
            {
                rejectionReason = EditorExecutionAdmissionPolicy.ExplainRejection(_interactiveSnapshot.State);
                return false;
            }

            if (_activeEditorRequestId.HasValue)
            {
                rejectionReason = "Another editor execution is already active.";
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
            _published.Add(envelope);
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
