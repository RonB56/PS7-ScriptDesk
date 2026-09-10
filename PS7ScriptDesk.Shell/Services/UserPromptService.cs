using System;
using System.IO;
using System.Windows;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Shell.Services
{
    public class UserPromptService : IUserPromptService, IGitSetupPromptService
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

        public bool ShowConfirmation(string title, string message, string primaryText, string secondaryText)
        {
            var dialog = new IdeMessageDialog(
                System.Windows.Application.Current?.MainWindow,
                title,
                message,
                primaryText,
                secondaryText);
            var confirmed = dialog.ShowDialog() == true && dialog.PrimaryAccepted;
            DeveloperDiagnostics.LogDecision(
                "UI",
                "ConfirmationPrompt",
                "Confirmation prompt completed.",
                confirmed ? "Confirmed" : "Canceled",
                new Dictionary<string, object?> { ["titleLength"] = title?.Length ?? 0 });
            return confirmed;
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

        public GitCloneRequest? ShowCloneDialog()
        {
            var sourceBox = new System.Windows.Controls.TextBox { Text = "https://", MinWidth = 420 };
            var parentBox = new System.Windows.Controls.TextBox { MinWidth = 360, IsReadOnly = true };
            var nameBox = new System.Windows.Controls.TextBox { MinWidth = 420 };
            var browse = new System.Windows.Controls.Button { Content = "Browse", Margin = new Thickness(6, 0, 0, 0) };
            var clone = new System.Windows.Controls.Button { Content = "Clone", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(6, 0, 0, 0) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Repository" }); panel.Children.Add(sourceBox);
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Destination parent", Margin = new Thickness(0, 10, 0, 0) });
            var parentRow = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; parentRow.Children.Add(parentBox); parentRow.Children.Add(browse); panel.Children.Add(parentRow);
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Repository folder name", Margin = new Thickness(0, 10, 0, 0) }); panel.Children.Add(nameBox);
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) }; buttons.Children.Add(cancel); buttons.Children.Add(clone); panel.Children.Add(buttons);
            var dialogWindow = new Window { Title = "Clone Repository", Owner = System.Windows.Application.Current?.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, Content = panel };
            browse.Click += (_, _) => { var folder = new Microsoft.Win32.OpenFolderDialog { Title = "Choose clone destination parent" }; if (folder.ShowDialog(dialogWindow) == true) parentBox.Text = folder.FolderName; };
            sourceBox.TextChanged += (_, _) => { var trimmed = sourceBox.Text.TrimEnd('/', '\\'); var proposed = Path.GetFileName(trimmed); if (proposed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) proposed = proposed[..^4]; if (string.IsNullOrWhiteSpace(nameBox.Text) || nameBox.Tag is null) { nameBox.Text = proposed; nameBox.Tag = proposed; } };
            clone.Click += (_, _) => dialogWindow.DialogResult = true;
            if (dialogWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(sourceBox.Text) || string.IsNullOrWhiteSpace(parentBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text)) return null;
            return new GitCloneRequest(sourceBox.Text.Trim(), parentBox.Text, nameBox.Text.Trim());
        }

        public string? ShowInitializeDialog(string folderPath)
        {
            var selected = folderPath;
            if (string.IsNullOrWhiteSpace(selected))
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folder to initialize as a Git repository" };
                if (dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return null;
                selected = dialog.FolderName;
            }
            return ShowConfirmation("Initialize Git Repository", $"Initialize Git in:\n{selected}\n\nThis creates .git metadata in this folder. No files will be staged or committed.", "Initialize", "Cancel") ? selected : null;
        }

    }
}
