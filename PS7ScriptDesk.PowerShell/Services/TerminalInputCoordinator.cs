using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Coordinates ownership of input immediately before the generation-safe terminal writer.
/// It deliberately tracks only conservative safety facts; it does not emulate PSReadLine.
/// </summary>
internal sealed class TerminalInputCoordinator : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private readonly TerminalInputRouter _router;
    private int _sessionGeneration;
    private bool _sessionActive;
    private bool _promptReady;
    private bool _userEditBufferMayContainText;
    private bool _internalSubmissionActive;

    internal (bool InternalSubmissionActive, bool UserEditOwnershipActive, bool PromptReady) GetForensicState()
    {
        lock (_syncRoot)
        {
            return (_internalSubmissionActive, _userEditBufferMayContainText, _promptReady);
        }
    }

    public TerminalInputCoordinator(TerminalInputRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public void BeginSession(int generation)
    {
        lock (_syncRoot)
        {
            _sessionGeneration = generation;
            _sessionActive = true;
            // Direct service consumers do not receive MainWindowViewModel's
            // prompt-ready event. Start with an empty-input provisional state;
            // the application-level admission policy still requires its prompt
            // observation before enabling editor Run, and any user input below
            // immediately revokes this provisional state.
            _promptReady = true;
            _userEditBufferMayContainText = false;
            _internalSubmissionActive = false;
        }
    }

    public void EndSession(int generation)
    {
        lock (_syncRoot)
        {
            if (_sessionGeneration != generation)
            {
                return;
            }

            _sessionActive = false;
            _promptReady = false;
            _userEditBufferMayContainText = false;
            _internalSubmissionActive = false;
        }
    }

    public void ObservePromptReady(int generation)
    {
        lock (_syncRoot)
        {
            if (!_sessionActive || _sessionGeneration != generation || _userEditBufferMayContainText)
            {
                return;
            }

            _promptReady = true;
        }
    }

    public void ObserveUserInput(string data, int generation)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_sessionActive || _sessionGeneration != generation)
            {
                return;
            }

            if (data.Contains('\x03'))
            {
                var previousEdit = _userEditBufferMayContainText;
                _userEditBufferMayContainText = false;
                _promptReady = false;
                AdmissionForensicLog.Write("USER_EDIT_STATE", new Dictionary<string, object?>
                {
                    ["previous"] = previousEdit,
                    ["new"] = false,
                    ["reason"] = "Ctrl+C",
                    ["generation"] = generation
                });
                return;
            }

            var inputClass = TerminalInputClassifier.Classify(data);
            if (!TerminalInputClassifier.EstablishesUserEditOwnership(inputClass))
            {
                AdmissionForensicLog.Write("USER_EDIT_STATE_IGNORED_PROTOCOL", new Dictionary<string, object?>
                {
                    ["inputClass"] = inputClass,
                    ["generation"] = generation,
                    ["reason"] = "Terminal frontend protocol does not establish an editable command line."
                });
                return;
            }

            // CR/LF submits the current line, but the shell is not considered safe
            // again until a subsequent prompt-ready observation arrives. Any other
            // input is conservatively treated as user-owned editable state. This
            // covers printable input, paste, arrows, deletion, and multiline edits
            // without attempting to reproduce PSReadLine's buffer implementation.
            if (data.Contains('\r') || data.Contains('\n'))
            {
                var previousEdit = _userEditBufferMayContainText;
                _userEditBufferMayContainText = false;
                _promptReady = false;
                AdmissionForensicLog.Write("USER_EDIT_STATE", new Dictionary<string, object?>
                {
                    ["previous"] = previousEdit,
                    ["new"] = false,
                    ["reason"] = "Enter",
                    ["generation"] = generation
                });
                return;
            }

            var previous = _userEditBufferMayContainText;
            _userEditBufferMayContainText = true;
            _promptReady = false;
            AdmissionForensicLog.Write("USER_EDIT_STATE", new Dictionary<string, object?>
            {
                ["previous"] = previous,
                ["new"] = true,
                ["reason"] = "PrintableOrHistoryEditingInput",
                ["generation"] = generation
            });
        }
    }

    public bool CanAcceptInternalDispatch(int generation, out string reason)
    {
        lock (_syncRoot)
        {
            var accepted = CanAcceptInternalDispatchNoLock(generation, out reason);
            AdmissionForensicLog.Write(accepted ? "INTERNAL_DISPATCH_ACCEPTED" : "INTERNAL_DISPATCH_REJECTED", new Dictionary<string, object?>
            {
                ["generation"] = generation,
                ["coordinatorGeneration"] = _sessionGeneration,
                ["isActive"] = _sessionActive,
                ["promptReady"] = _promptReady,
                ["possibleUserEditOwned"] = _userEditBufferMayContainText,
                ["internalSubmissionActive"] = _internalSubmissionActive,
                ["reason"] = reason
            });
            return accepted;
        }
    }

    public async Task WriteAsync(
        int generation,
        string payload,
        TerminalInputOrigin origin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AdmissionForensicLog.Write("ROUTER_WRITE_BEGIN", new Dictionary<string, object?>
        {
            ["origin"] = origin,
            ["generation"] = generation,
            ["payloadLength"] = payload.Length
        });
        var internalSubmission = false;
        try
        {
            if (origin == TerminalInputOrigin.InternalDispatch)
            {
                lock (_syncRoot)
                {
                    AdmissionForensicLog.Write("INTERNAL_DISPATCH_REQUEST", new Dictionary<string, object?>
                    {
                        ["generation"] = generation,
                        ["coordinatorGeneration"] = _sessionGeneration,
                        ["isActive"] = _sessionActive,
                        ["promptReady"] = _promptReady,
                        ["possibleUserEditOwned"] = _userEditBufferMayContainText,
                        ["internalSubmissionActive"] = _internalSubmissionActive,
                        ["origin"] = origin
                    });
                    if (!CanAcceptInternalDispatchNoLock(generation, out var reason))
                    {
                        AdmissionForensicLog.Write("INTERNAL_DISPATCH_REJECTED", new Dictionary<string, object?>
                        {
                            ["generation"] = generation,
                            ["coordinatorGeneration"] = _sessionGeneration,
                            ["reason"] = reason
                        });
                        throw new InvalidOperationException(reason);
                    }

                    _internalSubmissionActive = true;
                    _promptReady = false;
                    internalSubmission = true;
                    AdmissionForensicLog.Write("INTERNAL_DISPATCH_ACCEPTED", new Dictionary<string, object?>
                    {
                        ["generation"] = generation,
                        ["coordinatorGeneration"] = _sessionGeneration,
                        ["origin"] = origin
                    });
                }
            }

            await _router.WriteAsync(generation, payload, cancellationToken).ConfigureAwait(false);
            AdmissionForensicLog.Write("ROUTER_WRITE_END", new Dictionary<string, object?>
            {
                ["origin"] = origin,
                ["generation"] = generation,
                ["payloadLength"] = payload.Length
            });
        }
        finally
        {
            if (internalSubmission)
            {
                lock (_syncRoot)
                {
                    _internalSubmissionActive = false;
                }
            }

            _submissionGate.Release();
        }
    }

    private bool CanAcceptInternalDispatchNoLock(int generation, out string reason)
    {
        if (!_sessionActive || _sessionGeneration != generation)
        {
            reason = "The terminal session is not active for internal dispatch.";
            return false;
        }

        if (_internalSubmissionActive)
        {
            reason = "Another internal terminal submission is in progress.";
            return false;
        }

        if (_userEditBufferMayContainText)
        {
            reason = "The user may have an unfinished interactive input line.";
            return false;
        }

        if (!_promptReady)
        {
            reason = "The interactive PowerShell prompt is not confirmed ready.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void Dispose()
    {
        _submissionGate.Dispose();
    }
}
