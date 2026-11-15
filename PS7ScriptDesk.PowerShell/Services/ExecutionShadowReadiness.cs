using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Diagnostic-only mirror of the post-status visual predicates, without the echo guard.</summary>
internal sealed class ExecutionShadowReadiness(
    ExecutionCorrelationTelemetry telemetry,
    Guid requestId,
    int terminalGeneration,
    int? processId,
    long? processStartUtcTicks)
{
    private readonly object _sync = new();
    private bool _statusValidated;
    private bool _postStatusPrompt;
    private bool _cursorReady;

    public void ObserveStatus(bool validated, bool actualReady, bool echoObserved, bool identityValid)
    {
        lock (_sync)
        {
            _statusValidated = validated;
            Record("ShadowStatusEvaluated", actualReady, echoObserved, identityValid);
        }
    }

    public void ObservePrompt(int generation, bool cursorVisible, bool actualReady, bool echoObserved, bool identityValid)
    {
        lock (_sync)
        {
            if (generation == terminalGeneration && _statusValidated && identityValid)
            {
                _postStatusPrompt = true;
                _cursorReady = cursorVisible;
            }
            Record("ShadowPromptEvaluated", actualReady, echoObserved, identityValid);
        }
    }

    public void ObserveCursor(int generation, bool visible, bool actualReady, bool echoObserved, bool identityValid)
    {
        lock (_sync)
        {
            if (generation == terminalGeneration && _postStatusPrompt && identityValid)
                _cursorReady = visible;
            Record("ShadowCursorEvaluated", actualReady, echoObserved, identityValid);
        }
    }

    private void Record(string code, bool actualReady, bool echoObserved, bool identityValid)
    {
        var shadowReady = _statusValidated && _postStatusPrompt && _cursorReady && identityValid;
        var reason = !_statusValidated ? "status-unvalidated" : !_postStatusPrompt ? "post-status-prompt-missing"
            : !_cursorReady ? "cursor-not-ready" : !identityValid ? "identity-stale" :
            !actualReady && !echoObserved ? "echo-missing" : !actualReady ? "production-signal-incomplete" : null;
        telemetry.Record(new ExecutionCorrelationEvent(code, RequestId: requestId,
            RequestCorrelation: "authoritative", TerminalGeneration: terminalGeneration,
            ProcessId: processId, ProcessStartUtcTicks: processStartUtcTicks,
            Reason: reason, StatusValidated: _statusValidated, PostStatusPrompt: _postStatusPrompt,
            CursorReady: _cursorReady, IdentityValid: identityValid, EchoObserved: echoObserved,
            ShadowReady: shadowReady, ActualReady: actualReady));
        if (shadowReady && !actualReady)
            telemetry.Record(new ExecutionCorrelationEvent("ShadowDivergence", RequestId: requestId,
                RequestCorrelation: "authoritative", TerminalGeneration: terminalGeneration,
                ProcessId: processId, ProcessStartUtcTicks: processStartUtcTicks,
                Reason: reason, StatusValidated: true, PostStatusPrompt: true, CursorReady: true,
                IdentityValid: true, EchoObserved: echoObserved, ShadowReady: true, ActualReady: false));
    }
}
