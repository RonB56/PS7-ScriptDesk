namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Coordinates visual prompt restoration after the request's private status
/// boundary. A prompt-looking output fragment before that boundary is never
/// evidence that the foreground invocation has returned to the REPL.
/// </summary>
internal sealed class PromptRestorationSignal(int expectedGeneration)
{
    private readonly object _syncRoot = new();
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _statusFileObserved;
    private bool _promptBoundaryObserved;
    private bool _postScriptPromptObserved;
    private bool _cursorVisible;

    public PromptRestorationSnapshot Snapshot()
    {
        lock (_syncRoot)
        {
            return new PromptRestorationSnapshot(
                _statusFileObserved,
                _promptBoundaryObserved,
                _postScriptPromptObserved,
                _cursorVisible,
                _completion.Task.IsCompleted);
        }
    }

    public Task<bool> Completion => _completion.Task;

    public bool ObserveStatusFileVisible()
    {
        lock (_syncRoot)
        {
            _statusFileObserved = true;
            return _promptBoundaryObserved;
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

internal sealed record PromptRestorationSnapshot(
    bool StatusFileObserved,
    bool PromptBoundaryObserved,
    bool PostScriptPromptObserved,
    bool CursorVisible,
    bool Completed);
