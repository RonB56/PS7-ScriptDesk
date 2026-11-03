namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Coordinates the prompt and cursor observations used to verify completion of
/// one foreground invocation. This intentionally mirrors the event predicates
/// used by PsesOwnedInteractiveSessionService.
/// </summary>
internal sealed class PromptRestorationSignal(int expectedGeneration)
{
    private readonly object _syncRoot = new();
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _statusFileObserved;
    private bool _promptBoundaryObserved;
    private bool _postScriptPromptObserved;
    private bool _cursorVisible;

    public Task<bool> Completion => _completion.Task;

    public bool ObserveStatusFileVisible()
    {
        lock (_syncRoot)
        {
            var promptWasBuffered = !_statusFileObserved && _promptBoundaryObserved;
            _statusFileObserved = true;
            if (!_promptBoundaryObserved) return false;

            _postScriptPromptObserved = true;
            if (_cursorVisible) _completion.TrySetResult(true);
            return promptWasBuffered;
        }
    }

    public bool ObservePrompt(int generation, bool statusFileExists, bool invocationEchoRemoved, bool cursorVisible)
    {
        if (generation != expectedGeneration || !invocationEchoRemoved) return false;

        lock (_syncRoot)
        {
            var firstPromptBoundary = !_promptBoundaryObserved;
            _promptBoundaryObserved = true;
            _cursorVisible = cursorVisible;
            if (statusFileExists) _statusFileObserved = true;
            if (!_statusFileObserved) return firstPromptBoundary;

            _postScriptPromptObserved = true;
            if (_cursorVisible) _completion.TrySetResult(true);
            return firstPromptBoundary;
        }
    }

    public bool ObserveCursor(int generation, bool visible)
    {
        if (generation != expectedGeneration || !visible) return false;

        lock (_syncRoot)
        {
            if (!_postScriptPromptObserved) return false;
            _completion.TrySetResult(true);
            return true;
        }
    }
}
