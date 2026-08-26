namespace PS7ScriptDesk.Domain.Models;

public sealed class ApiProjectGenerationRequest
{
    public ApiProjectGenerationRequest(
        string sourceScriptPath,
        ApiPublishConfiguration configuration,
        string destinationDirectory,
        string? projectName = null,
        bool overwriteExistingGeneratedProject = false)
    {
        SourceScriptPath = string.IsNullOrWhiteSpace(sourceScriptPath) ? string.Empty : sourceScriptPath;
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        DestinationDirectory = string.IsNullOrWhiteSpace(destinationDirectory) ? string.Empty : destinationDirectory;
        ProjectName = string.IsNullOrWhiteSpace(projectName) ? "GeneratedPowerShellApi" : projectName;
        OverwriteExistingGeneratedProject = overwriteExistingGeneratedProject;
    }

    public string SourceScriptPath { get; }

    public ApiPublishConfiguration Configuration { get; }

    public string DestinationDirectory { get; }

    public string ProjectName { get; }

    public bool OverwriteExistingGeneratedProject { get; }
}
