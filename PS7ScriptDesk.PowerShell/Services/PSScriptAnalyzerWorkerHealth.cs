namespace PS7ScriptDesk.PowerShell.Services;

public enum PSScriptAnalyzerWorkerState
{
    NotStarted,
    Starting,
    Ready,
    Busy,
    Recovering,
    Unavailable,
    Faulted,
    Disposed
}

public sealed record PSScriptAnalyzerWorkerHealthSnapshot(
    PSScriptAnalyzerWorkerState State,
    int Generation,
    int RestartCount,
    int? ProcessId,
    string? RuntimePath,
    string? AnalyzerVersion,
    string? CurrentRequestId,
    string? LastFailureCategory,
    long? LastColdStartMilliseconds,
    long? LastAnalysisMilliseconds,
    long? LastSuccessfulAnalysisMilliseconds);
