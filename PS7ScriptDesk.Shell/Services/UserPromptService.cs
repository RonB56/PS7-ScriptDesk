using System;
using System.Windows;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Shell.Services
{
    public class UserPromptService : IUserPromptService
    {
        public UnsavedChangesDecision ShowUnsavedChangesPrompt(string documentName)
        {
            var dialog = new IdeMessageDialog(
                System.Windows.Application.Current?.MainWindow,
                "Unsaved Changes",
                $"Do you want to save changes to {documentName}?",
                "Save",
                "Discard");
            var result = dialog.ShowDialog();
            var decision = ResolveUnsavedChangesDecision(result, dialog.PrimaryAccepted, dialog.SecondaryAccepted);
            DeveloperDiagnostics.LogDecision(
                "UI",
                "UnsavedChangesPrompt",
                "Unsaved-changes prompt completed.",
                decision.ToString(),
                new Dictionary<string, object?> { ["documentNameLength"] = documentName?.Length ?? 0 });
            return decision;
        }

        internal static UnsavedChangesDecision ResolveUnsavedChangesDecision(bool? dialogResult, bool primaryAccepted, bool secondaryAccepted)
        {
            if (dialogResult == true && primaryAccepted)
            {
                return UnsavedChangesDecision.Save;
            }

            return dialogResult == false && secondaryAccepted
                ? UnsavedChangesDecision.Discard
                : UnsavedChangesDecision.Cancel;
        }

        public ExternalFileConflictDecision ShowExternalFileConflictPrompt(string filePath, string conflictReason)
        {
            var dialog = new ExternalFileConflictDialog(filePath, conflictReason)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            _ = dialog.ShowDialog();
            return dialog.Decision;
        }

        public DocumentRecoveryAction ShowDocumentRecoveryPrompt(DocumentRecoveryCandidate recoveryCandidate)
        {
            ArgumentNullException.ThrowIfNull(recoveryCandidate);

            var dialog = new DocumentRecoveryDialog(
                recoveryCandidate,
                System.Windows.Application.Current?.MainWindow);
            _ = dialog.ShowDialog();
            return dialog.Decision;
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
            _ = new IdeMessageDialog(
                System.Windows.Application.Current?.MainWindow,
                title,
                message).ShowDialog();
        }

        public string? ShowOpenPowerShellExecutableDialog()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PowerShell 7 pwsh.exe",
                Filter = "PowerShell 7 executable (pwsh.exe)|pwsh.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                FileName = "pwsh.exe"
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? ShowOpenFolderDialog()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a workspace folder",
                Multiselect = false
            };

            return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true
                ? dialog.FolderName
                : null;
        }

    }
}
