namespace PS7ScriptDesk.Domain.Models;

public enum ExeExportDependencyKind
{
    Module,
    Assembly,
    File,
    Executable,
    ScriptRelativePath,
    Unknown
}

public enum ExeExportDependencyClassification
{
    EmbeddedCandidate,
    ExternalDependency,
    SystemDependency,
    PotentialPortabilityProblem,
    CannotDetermine
}

public sealed record ExeExportDependency(
    ExeExportDependencyKind Kind,
    ExeExportDependencyClassification Classification,
    string Value,
    string Message,
    int? LineNumber = null);
