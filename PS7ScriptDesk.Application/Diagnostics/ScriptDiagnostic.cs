namespace PS7ScriptDesk.Application.Diagnostics;

public enum ScriptDiagnosticSeverity
{
    Error,
    Warning,
    Information,
    Hint
}

public enum ScriptDiagnosticSource
{
    Parser,
    Authoring,
    PSScriptAnalyzer
}

/// <summary>
/// Immutable, source-aware diagnostic data shared by all editor diagnostic producers.
/// </summary>
public sealed record ScriptDiagnostic(
    Guid DocumentId,
    long DocumentRevision,
    ScriptDiagnosticSource SourceId,
    string? RuleId,
    string Message,
    ScriptDiagnosticSeverity Severity,
    string? Path,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int? StartOffset = null,
    int? EndOffset = null,
    string? RequestId = null,
    IReadOnlyDictionary<string, string>? CorrectionMetadata = null);
