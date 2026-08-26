namespace PS7ScriptDesk.Domain.Models;

public enum ApiLocalTestHostState
{
    NotRunning,
    Generating,
    Preparing,
    Starting,
    Running,
    Stopping,
    Failed,
    Exited
}

public sealed class ApiLocalTestHostRequest
{
    public ApiLocalTestHostRequest(
        string sourceScriptPath,
        ApiPublishConfiguration configuration,
        string? projectDirectory = null,
        string? projectName = null,
        int? port = null,
        TimeSpan? readinessTimeout = null,
        bool overwriteExistingGeneratedProject = true,
        string? hostExecutablePath = null)
    {
        SourceScriptPath = string.IsNullOrWhiteSpace(sourceScriptPath) ? string.Empty : sourceScriptPath;
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ProjectDirectory = projectDirectory;
        ProjectName = projectName;
        Port = port;
        ReadinessTimeout = readinessTimeout;
        OverwriteExistingGeneratedProject = overwriteExistingGeneratedProject;
        HostExecutablePath = hostExecutablePath;
    }

    public string SourceScriptPath { get; }

    public ApiPublishConfiguration Configuration { get; }

    public string? ProjectDirectory { get; }

    public string? ProjectName { get; }

    public int? Port { get; }

    public TimeSpan? ReadinessTimeout { get; }

    public bool OverwriteExistingGeneratedProject { get; }

    public string? HostExecutablePath { get; }
}

public sealed class ApiLocalTestHostStatus
{
    public ApiLocalTestHostState State { get; init; } = ApiLocalTestHostState.NotRunning;

    public string StatusMessage { get; init; } = "Local API test host is not running.";

    public Uri? BaseUrl { get; init; }

    public Uri? OpenApiUrl { get; init; }

    public Uri? SwaggerUrl { get; init; }

    public string ProjectDirectory { get; init; } = string.Empty;

    public int? ProcessId { get; init; }

    public int? ExitCode { get; init; }

    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();

    public bool IsRunning => State == ApiLocalTestHostState.Running;
}

public sealed class ApiLocalTestHostStartResult
{
    private ApiLocalTestHostStartResult(bool succeeded, string summaryMessage, string detailedLog, ApiLocalTestHostStatus status)
    {
        Succeeded = succeeded;
        SummaryMessage = summaryMessage;
        DetailedLog = detailedLog;
        Status = status;
    }

    public bool Succeeded { get; }

    public string SummaryMessage { get; }

    public string DetailedLog { get; }

    public ApiLocalTestHostStatus Status { get; }

    public static ApiLocalTestHostStartResult Success(ApiLocalTestHostStatus status, string detailedLog)
        => new(true, "Local API test host started.", detailedLog, status);

    public static ApiLocalTestHostStartResult Failure(string summaryMessage, string detailedLog, ApiLocalTestHostStatus status)
        => new(false, string.IsNullOrWhiteSpace(summaryMessage) ? "Local API test host failed." : summaryMessage, detailedLog, status);
}
