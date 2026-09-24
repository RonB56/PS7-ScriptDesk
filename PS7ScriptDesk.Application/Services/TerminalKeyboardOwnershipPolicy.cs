namespace PS7ScriptDesk.Application.Services;

public enum TerminalCtrlCDisposition
{
    CopySelection,
    Interrupt,
    PassThrough
}

/// <summary>
/// Defines the terminal-side meaning of Ctrl+C without owning terminal content.
/// </summary>
public static class TerminalKeyboardOwnershipPolicy
{
    public static TerminalCtrlCDisposition ResolveCtrlC(bool selectionPresent, bool terminalSessionRunning)
    {
        if (selectionPresent)
        {
            return TerminalCtrlCDisposition.CopySelection;
        }

        return terminalSessionRunning
            ? TerminalCtrlCDisposition.Interrupt
            : TerminalCtrlCDisposition.PassThrough;
    }
}
