using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Dialogs;
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

            var decision = DocumentRecoveryAction.KeepForLater;
            var originalPathText = string.IsNullOrWhiteSpace(recoveryCandidate.OriginalFilePath)
                ? "Untitled document"
                : recoveryCandidate.OriginalFilePath;

            var window = new Window
            {
                Title = "Recover Unsaved Script",
                Owner = System.Windows.Application.Current?.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Width = 560,
                SizeToContent = SizeToContent.Height,
                MinHeight = 260
            };

            var root = new StackPanel
            {
                Margin = new Thickness(18),
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            root.Children.Add(new TextBlock
            {
                Text = "PS7 ScriptDesk found unsaved editor content from an earlier session.",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            root.Children.Add(new TextBlock
            {
                Text =
                    $"Document: {recoveryCandidate.DisplayName}{Environment.NewLine}" +
                    $"Original: {originalPathText}{Environment.NewLine}" +
                    $"Recovered: {recoveryCandidate.LastRecoveryWriteUtc.ToLocalTime():G}{Environment.NewLine}" +
                    $"Disk status: {recoveryCandidate.StatusDescription}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            root.Children.Add(new TextBlock
            {
                Text = "Recovered content is temporary. Restoring it opens a dirty editor tab; saving is still explicit and will not overwrite the original file automatically.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 18)
            });

            var buttons = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            AddRecoveryButton(buttons, "Restore", "Open recovered content in an editor tab.", DocumentRecoveryAction.Restore);
            AddRecoveryButton(buttons, "Discard", "Delete this recovery snapshot and leave the original file untouched.", DocumentRecoveryAction.Discard);
            AddRecoveryButton(buttons, "Save As...", "Restore the content and choose a new file path.", DocumentRecoveryAction.SaveAs);
            AddRecoveryButton(buttons, "Later", "Keep this recovery snapshot for the next startup.", DocumentRecoveryAction.KeepForLater);

            root.Children.Add(buttons);
            window.Content = root;

            foreach (System.Windows.Controls.Button button in buttons.Children)
            {
                button.Click += (_, _) =>
                {
                    if (button.Tag is DocumentRecoveryAction action)
                    {
                        decision = action;
                    }

                    window.DialogResult = true;
                    window.Close();
                };
            }

            _ = window.ShowDialog();
            return decision;
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
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select a workspace folder"
            };

            return dialog.ShowDialog() == Forms.DialogResult.OK
                ? dialog.SelectedPath
                : null;
        }

        private static void AddRecoveryButton(
            System.Windows.Controls.Panel buttons,
            string text,
            string toolTip,
            DocumentRecoveryAction action)
        {
            buttons.Children.Add(new System.Windows.Controls.Button
            {
                Content = text,
                Tag = action,
                MinWidth = 86,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 5, 10, 5),
                ToolTip = toolTip
            });
        }
    }
}
