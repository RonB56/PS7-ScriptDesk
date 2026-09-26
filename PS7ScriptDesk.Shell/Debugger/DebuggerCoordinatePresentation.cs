namespace PS7ScriptDesk.Shell.Debug;

public static class DebuggerCoordinatePresentation
{
    public static string FormatBreakpointHit(int runtimeLine, DebugSourceMappingStatus status)
    {
        if (runtimeLine <= 0)
        {
            return "Breakpoint hit.";
        }

        return status == DebugSourceMappingStatus.RevisionMismatch
            ? $"Breakpoint hit at execution snapshot line {runtimeLine}. The editor has changed since debugging started; restart debugging to restore source navigation."
            : status is DebugSourceMappingStatus.MissingSource or
                DebugSourceMappingStatus.RuntimeGenerated or
                DebugSourceMappingStatus.Unmapped or
                DebugSourceMappingStatus.Invalidated
                ? $"Breakpoint hit at runtime line {runtimeLine}. Source navigation is unavailable for this execution location."
                : $"Breakpoint hit at line {runtimeLine}.";
    }

    public static string FormatBreakpointStatus(int runtimeLine, DebugSourceMappingStatus status)
        => status == DebugSourceMappingStatus.RevisionMismatch
            ? $"Breakpoint hit — execution snapshot line {runtimeLine}; editor changed, restart debugging to restore navigation"
            : status is DebugSourceMappingStatus.MissingSource or
                DebugSourceMappingStatus.RuntimeGenerated or
                DebugSourceMappingStatus.Unmapped or
                DebugSourceMappingStatus.Invalidated
                ? $"Breakpoint hit — runtime line {runtimeLine}; source navigation unavailable"
                : runtimeLine > 0
                    ? $"Breakpoint hit — line {runtimeLine}"
                    : "Breakpoint hit";

    public static string FormatCallStackScript(string scriptName, DebugSourceMappingStatus status)
        => status == DebugSourceMappingStatus.RevisionMismatch
            ? $"{scriptName} (execution snapshot)"
            : scriptName;

    public static string FormatCallStackLine(int runtimeLine, DebugSourceMappingStatus status)
        => runtimeLine <= 0
            ? "—"
            : status == DebugSourceMappingStatus.RevisionMismatch
                ? $"Execution line {runtimeLine} (older revision)"
                : status is DebugSourceMappingStatus.MissingSource or
                    DebugSourceMappingStatus.RuntimeGenerated or
                    DebugSourceMappingStatus.Unmapped or
                    DebugSourceMappingStatus.Invalidated
                    ? $"Runtime line {runtimeLine}"
                    : runtimeLine.ToString();
}
