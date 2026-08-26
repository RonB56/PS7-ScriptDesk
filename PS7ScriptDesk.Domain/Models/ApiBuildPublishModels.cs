namespace PS7ScriptDesk.Domain.Models;

public enum ApiPublishTargetArchitecture
{
    WinX64,
    WinArm64,
    Both
}

public sealed class ApiBuildPublishRequest
{
    public ApiBuildPublishRequest(
        string sourceScriptPath,
        ApiPublishConfiguration configuration,
        string projectDirectory,
        string? projectName = null,
        ApiPublishTargetArchitecture targetArchitecture = ApiPublishTargetArchitecture.WinX64,
        string? publishDirectory = null,
        bool overwriteExistingGeneratedProject = true)
    {
        SourceScriptPath = string.IsNullOrWhiteSpace(sourceScriptPath) ? string.Empty : sourceScriptPath;
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ProjectDirectory = string.IsNullOrWhiteSpace(projectDirectory) ? string.Empty : projectDirectory;
        ProjectName = string.IsNullOrWhiteSpace(projectName) ? "GeneratedPowerShellApi" : projectName;
        TargetArchitecture = targetArchitecture;
        PublishDirectory = publishDirectory;
        OverwriteExistingGeneratedProject = overwriteExistingGeneratedProject;
    }

    public string SourceScriptPath { get; }
    public ApiPublishConfiguration Configuration { get; }
    public string ProjectDirectory { get; }
    public string ProjectName { get; }
    public ApiPublishTargetArchitecture TargetArchitecture { get; }
    public string? PublishDirectory { get; }
    public bool OverwriteExistingGeneratedProject { get; }
}

public sealed record ApiBuildPublishProgressUpdate(
    string Stage,
    string StatusMessage,
    bool IsIndeterminate = true);

public sealed record ApiBuildPublishArtifact(
    ApiPublishTargetArchitecture Architecture,
    string RuntimeIdentifier,
    string OutputDirectory,
    string ExecutablePath,
    long ExecutableLength,
    bool RuntimeValidated);

public sealed class ApiBuildPublishResult
{
    private ApiBuildPublishResult(
        bool succeeded,
        string summaryMessage,
        string detailedLog,
        string projectDirectory,
        string projectFilePath,
        IReadOnlyList<ApiBuildPublishArtifact> artifacts,
        bool wasCancelled)
    {
        Succeeded = succeeded;
        SummaryMessage = summaryMessage;
        DetailedLog = detailedLog;
        ProjectDirectory = projectDirectory;
        ProjectFilePath = projectFilePath;
        Artifacts = artifacts;
        WasCancelled = wasCancelled;
    }

    public bool Succeeded { get; }
    public string SummaryMessage { get; }
    public string DetailedLog { get; }
    public string ProjectDirectory { get; }
    public string ProjectFilePath { get; }
    public IReadOnlyList<ApiBuildPublishArtifact> Artifacts { get; }
    public bool WasCancelled { get; }
    public string OutputDirectory => Artifacts.LastOrDefault()?.OutputDirectory ?? string.Empty;

    public static ApiBuildPublishResult Success(
        string summaryMessage,
        string detailedLog,
        string projectDirectory,
        string projectFilePath,
        IReadOnlyList<ApiBuildPublishArtifact> artifacts)
        => new(
            true,
            string.IsNullOrWhiteSpace(summaryMessage) ? "REST API operation completed successfully." : summaryMessage,
            detailedLog,
            projectDirectory,
            projectFilePath,
            artifacts,
            wasCancelled: false);

    public static ApiBuildPublishResult Failure(
        string summaryMessage,
        string detailedLog,
        string? projectDirectory = null,
        string? projectFilePath = null,
        bool wasCancelled = false)
        => new(
            false,
            string.IsNullOrWhiteSpace(summaryMessage) ? "REST API operation failed." : summaryMessage,
            detailedLog,
            projectDirectory ?? string.Empty,
            projectFilePath ?? string.Empty,
            Array.Empty<ApiBuildPublishArtifact>(),
            wasCancelled);
}
