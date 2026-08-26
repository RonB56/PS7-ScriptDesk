namespace PS7ScriptDesk.Domain.Models;

public sealed class ApiProjectGenerationResult
{
    private ApiProjectGenerationResult(
        bool succeeded,
        string destinationDirectory,
        string projectFilePath,
        string summaryMessage,
        IReadOnlyList<string> generatedFiles,
        IReadOnlyList<ApiPublishValidationDiagnostic> validationErrors,
        string detailedLog)
    {
        Succeeded = succeeded;
        DestinationDirectory = destinationDirectory;
        ProjectFilePath = projectFilePath;
        SummaryMessage = summaryMessage;
        GeneratedFiles = generatedFiles;
        ValidationErrors = validationErrors;
        DetailedLog = detailedLog;
    }

    public bool Succeeded { get; }

    public string DestinationDirectory { get; }

    public string ProjectFilePath { get; }

    public string SummaryMessage { get; }

    public IReadOnlyList<string> GeneratedFiles { get; }

    public IReadOnlyList<ApiPublishValidationDiagnostic> ValidationErrors { get; }

    public string DetailedLog { get; }

    public static ApiProjectGenerationResult Success(
        string destinationDirectory,
        string projectFilePath,
        IReadOnlyList<string> generatedFiles,
        string detailedLog)
        => new(
            true,
            destinationDirectory,
            projectFilePath,
            "API project generated successfully.",
            generatedFiles,
            Array.Empty<ApiPublishValidationDiagnostic>(),
            detailedLog);

    public static ApiProjectGenerationResult Failure(
        string summaryMessage,
        string detailedLog,
        string? destinationDirectory = null,
        IReadOnlyList<ApiPublishValidationDiagnostic>? validationErrors = null)
        => new(
            false,
            destinationDirectory ?? string.Empty,
            string.Empty,
            string.IsNullOrWhiteSpace(summaryMessage) ? "API project generation failed." : summaryMessage,
            Array.Empty<string>(),
            validationErrors ?? Array.Empty<ApiPublishValidationDiagnostic>(),
            detailedLog);
}
