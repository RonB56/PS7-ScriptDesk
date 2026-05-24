using PS7ScriptDesk.Application.Interfaces;
using Forms = System.Windows.Forms;

namespace PS7ScriptDesk.Shell.Services
{
    public class UserPromptService : IUserPromptService
    {
        public UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName)
        {
            var result = System.Windows.MessageBox.Show(
                $"Do you want to save changes to {documentName}?",
                "Unsaved Changes",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Warning);

            return result switch
            {
                System.Windows.MessageBoxResult.Yes => UnsavedChangesDecision.Save,
                System.Windows.MessageBoxResult.No => UnsavedChangesDecision.Discard,
                _ => UnsavedChangesDecision.Cancel
            };
        }

        public string? ShowSaveFileDialog(string suggestedFileName)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Script File",
                Filter = "PowerShell Files (*.ps1)|*.ps1|All Files (*.*)|*.*",
                DefaultExt = ".ps1",
                AddExtension = true,
                OverwritePrompt = true,
                CheckFileExists = false,
                CheckPathExists = true,
                CreatePrompt = false,
                CreateTestFile = false,
                ValidateNames = true,
                FileName = suggestedFileName
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? ShowSaveExecutableDialog(string suggestedFileName)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Script as Windows Executable",
                Filter = "Executable Files (*.exe)|*.exe",
                DefaultExt = ".exe",
                AddExtension = true,
                FileName = suggestedFileName
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }


        public void ShowWarningMessage(string title, string message)
        {
            System.Windows.MessageBox.Show(
                message,
                title,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        public string? ShowOpenFolderDialog()
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select a workspace folder"
            };

            return dialog.ShowDialog() == Forms.DialogResult.OK
                ? dialog.SelectedPath
                : null;
        }
    }
}
