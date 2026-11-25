using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Dialogs;

internal static class ApiBuildPublishPresentation
{
    internal static string Format(ApiBuildPublishResult result, string actionName)
    {
        ArgumentNullException.ThrowIfNull(result);
        var isProjectGeneration = string.Equals(actionName, "Generate Project", StringComparison.Ordinal);
        var text = new StringBuilder();
        text.AppendLine(result.Succeeded
            ? isProjectGeneration
                ? "Project generation completed successfully. No runtime artifact was built or published."
                : "Complete success. All requested architecture targets completed."
            : result.RecoveryRequired
                ? "Recovery required. The operation did not complete, and restoration could not be confirmed."
                : result.WasCancelled
                    ? isProjectGeneration ? "Project generation was cancelled." : "Operation cancelled. Some requested targets may not have been attempted."
                    : isProjectGeneration ? "Project generation failed." : "Operation incomplete. One or more requested targets did not complete.");
        text.AppendLine(DeveloperDiagnostics.SanitizePreview(result.SummaryMessage, 4096));

        foreach (var artifact in result.CompletedArtifacts)
        {
            text.AppendLine($"Committed artifact ({artifact.RuntimeIdentifier}): {artifact.OutputDirectory}");
            text.AppendLine(artifact.RuntimeValidated
                ? "Runtime execution was verified for this artifact."
                : "Structural validation passed; runtime execution was not verified.");
        }

        foreach (var outcome in result.ArtifactOutcomes.Where(item => item.Status != ApiArtifactOutcomeStatus.Committed))
        {
            text.AppendLine($"{outcome.RuntimeIdentifier}: {outcome.Status} — {DeveloperDiagnostics.SanitizePreview(outcome.Message, 2048)}");
            if (!string.IsNullOrWhiteSpace(outcome.OutputDirectory))
                text.AppendLine($"Output location: {outcome.OutputDirectory}");
        }

        if (result.RecoveryRequired)
        {
            var recoveryDetails = result.DetailedLog ?? string.Empty;
            var couldNotInspect = recoveryDetails.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase) ||
                                  recoveryDetails.Contains("availability is unknown", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(result.RecoveryBackupPath))
            {
                text.AppendLine($"Recovery material path reported: {result.RecoveryBackupPath}");
                if (couldNotInspect)
                {
                    text.AppendLine("Recovery material could not be inspected; its availability is unknown.");
                }
                else if (recoveryDetails.Contains("(backup unavailable)", StringComparison.OrdinalIgnoreCase) ||
                         recoveryDetails.Contains("recovery material was unavailable", StringComparison.OrdinalIgnoreCase) ||
                         recoveryDetails.Contains("backup is unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    text.AppendLine("Recovery material is reported unavailable at the stated location.");
                }
                else
                {
                    text.AppendLine("Its availability, integrity, and restoration status have not been verified by this presentation.");
                }
            }
            else if (couldNotInspect)
            {
                text.AppendLine("Recovery material could not be inspected; its availability is unknown, and no recovery path was reported.");
            }
            else
            {
                text.AppendLine("No recovery material path was reported; its availability could not be confirmed.");
            }

            text.AppendLine("Do not assume the previous output was restored; inspect the destination and any reported recovery location before retrying.");
        }

        if (!string.IsNullOrWhiteSpace(result.DetailedLog))
        {
            text.AppendLine("Details:");
            text.AppendLine(DeveloperDiagnostics.SanitizePreview(result.DetailedLog, 4096));
        }

        return text.ToString().TrimEnd();
    }
}
