namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IUserPromptService
    {
        UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName);
        string? ShowSaveFileDialog(string suggestedFileName);
        string? ShowSaveExecutableDialog(string suggestedFileName);
        string? ShowOpenFolderDialog();
        void ShowWarningMessage(string title, string message);
    }
}