using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models;

public sealed record ExeExportWizardRequest(
    string SuggestedApplicationName,
    string SourceScriptPath,
    string ScriptContent,
    IReadOnlyList<ExeExportDependency> Dependencies);
