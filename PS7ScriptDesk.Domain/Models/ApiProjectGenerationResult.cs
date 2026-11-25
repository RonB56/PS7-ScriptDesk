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
        string detailedLog,
        bool recoveryRequired,
        string recoveryBackupPath,
        bool wasCancelled)
    {
        Succeeded = succeeded;
        DestinationDirectory = destinationDirectory;
        ProjectFilePath = projectFilePath;
        SummaryMessage = summaryMessage;
        GeneratedFiles = generatedFiles;
        ValidationErrors = validationErrors;
        DetailedLog = detailedLog;
        RecoveryRequired = recoveryRequired;
        RecoveryBackupPath = recoveryBackupPath;
        WasCancelled = wasCancelled;
    }

    public bool Succeeded { get; }

    public string DestinationDirectory { get; }

    public string ProjectFilePath { get; }

    public string SummaryMessage { get; }

    public IReadOnlyList<string> GeneratedFiles { get; }

    public IReadOnlyList<ApiPublishValidationDiagnostic> ValidationErrors { get; }

    public string DetailedLog { get; }

    public bool RecoveryRequired { get; }

    public string RecoveryBackupPath { get; }

    public bool WasCancelled { get; }

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
            detailedLog,
            recoveryRequired: false,
            recoveryBackupPath: string.Empty,
            wasCancelled: false);

    public static ApiProjectGenerationResult Failure(
        string summaryMessage,
        string detailedLog,
        string? destinationDirectory = null,
        IReadOnlyList<ApiPublishValidationDiagnostic>? validationErrors = null,
        bool recoveryRequired = false,
        string? recoveryBackupPath = null,
        bool wasCancelled = false)
        => new(
            false,
            destinationDirectory ?? string.Empty,
            string.Empty,
            string.IsNullOrWhiteSpace(summaryMessage) ? "API project generation failed." : summaryMessage,
            Array.Empty<string>(),
            validationErrors ?? Array.Empty<ApiPublishValidationDiagnostic>(),
            detailedLog,
            recoveryRequired,
            recoveryBackupPath ?? string.Empty,
            wasCancelled);
}
