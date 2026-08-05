namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IUserPromptService
    {
        UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName);
        ExternalFileConflictDecision ShowExternalFileConflictPrompt(string filePath, string conflictReason);
        string? ShowSaveFileDialog(string suggestedFileName);
        string? ShowSaveExecutableDialog(string suggestedFileName);
        string? ShowOpenFolderDialog();
        string? ShowOpenPowerShellExecutableDialog();
        void ShowWarningMessage(string title, string message);
    }
}
