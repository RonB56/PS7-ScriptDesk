using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IUserPromptService
    {
        UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName);
        ExternalFileConflictDecision ShowExternalFileConflictPrompt(string filePath, string conflictReason);
        DocumentRecoveryAction ShowDocumentRecoveryPrompt(DocumentRecoveryCandidate recoveryCandidate);
        string? ShowSaveFileDialog(string suggestedFileName);
        string? ShowSaveExecutableDialog(string suggestedFileName);
        string? ShowOpenFolderDialog();
        string? ShowOpenPowerShellExecutableDialog();
        void ShowWarningMessage(string title, string message);
        bool ShowConfirmation(string title, string message, string primaryText, string secondaryText, bool destructive = false);
    }
}
