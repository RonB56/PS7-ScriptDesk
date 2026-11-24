using System;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class TerminalOutputMultiplexer
{
    private readonly object _syncRoot = new();
    private InteractiveTerminalSnapshot _interactiveSnapshot = new(
        0,
        InteractiveTerminalState.Unavailable,
        "Terminal state has not been initialized.",
        DateTimeOffset.UtcNow);
    private Guid? _activeEditorRequestId;
    private Guid? _activeInternalCommandRequestId;

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
            // same generation so execution admission stays aligned with
            // the authoritative session lifecycle.
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
}
