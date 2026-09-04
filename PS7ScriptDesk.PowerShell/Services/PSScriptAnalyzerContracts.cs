namespace PS7ScriptDesk.PowerShell.Services;

public sealed record PSScriptAnalyzerRequest(
    string RequestId,
    string DocumentId,
    long Revision,
    string? Path,
    string ScriptText,
    string SeverityFilter = "All",
    string Profile = "DefaultBundled",
    bool EnableProgress = false);

public enum PSScriptAnalyzerProgressState
{
    AnalysisStarted,
    PreparingAnalyzer,
    RuleStarted,
    RuleCompleted,
    AnalysisCompleted,
    AnalysisCancelled,
    AnalysisFailed,
    WorkerHealthFailure
}

public sealed record PSScriptAnalyzerProgress(
    string RequestId,
    string DocumentId,
    long DocumentRevision,
    PSScriptAnalyzerProgressState State,
    int CurrentRuleIndex,
    int TotalRules,
    string? RuleName,
    long ElapsedMilliseconds,
    long RuleElapsedMilliseconds,
    int FindingsSoFar,
    string? FailureClassification = null);

public sealed record PSScriptAnalyzerFinding(
    string? RuleId,
    string Message,
    string Severity,
    int Line,
    int Column,
    int? EndLine = null,
    int? EndColumn = null,
    string? ScriptName = null,
    string? Correction = null);

public sealed record PSScriptAnalyzerResult(
    string RequestId,
    IReadOnlyList<PSScriptAnalyzerFinding> Findings,
    string? Error = null);

public interface IPSScriptAnalyzerService
{
    string? BundledAnalyzerVersion { get; }
    Task<PSScriptAnalyzerResult> AnalyzeAsync(PSScriptAnalyzerRequest request, CancellationToken cancellationToken = default);
}
