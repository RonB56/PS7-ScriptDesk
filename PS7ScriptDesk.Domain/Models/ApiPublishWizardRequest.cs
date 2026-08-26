namespace PS7ScriptDesk.Domain.Models;

public sealed record ApiPublishWizardRequest(
    string SuggestedApiName,
    string SourceScriptPath,
    string ScriptContent);
