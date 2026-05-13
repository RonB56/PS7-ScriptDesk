using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Domain.Models;
using PowerShellStudio.PowerShell.Services;

namespace PowerShellStudio.Shell
{
    public partial class RuntimeResolverWindow : Window
    {
        private readonly RuntimeService _runtimeService;

        public RuntimeResolverWindow(RuntimeService runtimeService)
        {
            _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
            InitializeComponent();
            Title = $"{ApplicationBranding.PublicName} - PowerShell 7 Required";
            StatusMessageTextBlock.Text = "PowerShell 7 was not found yet. Browse to pwsh.exe to continue.";
        }

        public PowerShellRuntimeInfo? SelectedRuntime { get; private set; }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            DeveloperDiagnostics.LogUserAction("Startup", "StartupRuntimeBrowseRequested", "User requested runtime browse from the startup resolver dialog.");

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select pwsh.exe",
                FileName = "pwsh.exe",
                CheckFileExists = true,
                CheckPathExists = true,
                Filter = "PowerShell 7 executable (pwsh.exe)|pwsh.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                DeveloperDiagnostics.LogDecision("Startup", "StartupRuntimeBrowseRequested", "User canceled the browse dialog from the startup resolver.", "BrowseCanceled");
                return;
            }

            SelectedPathTextBox.Text = dialog.FileName;
            ValidateAndAccept(dialog.FileName);
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            DeveloperDiagnostics.LogDecision("Startup", "StartupRuntimeResolverClosed", "User chose to exit from the startup runtime resolver dialog.", "ExitRequested");
            DialogResult = false;
        }

        private void RetryDiscoveryButton_Click(object sender, RoutedEventArgs e)
        {
            DeveloperDiagnostics.LogUserAction("Startup", "StartupRuntimeRetryDiscoveryRequested", "User requested another automatic runtime discovery pass from the startup resolver dialog.");
            StatusMessageTextBlock.Text = "Searching for PowerShell 7 again...";

            try
            {
                var discoveryResult = _runtimeService.DiscoverRuntimes();
                if (discoveryResult.PreferredRuntime is not null)
                {
                    SelectedRuntime = discoveryResult.PreferredRuntime;
                    SelectedPathTextBox.Text = SelectedRuntime.ExecutablePath;
                    StatusMessageTextBlock.Text = $"Found and accepted {SelectedRuntime.DisplayName} at {SelectedRuntime.ExecutablePath}.";
                    DeveloperDiagnostics.LogDecision(
                        "Startup",
                        "StartupRuntimeRetryDiscoveryRequested",
                        "Retry discovery found a valid PowerShell 7 runtime from the startup resolver dialog.",
                        "Accepted",
                        new Dictionary<string, object?>
                        {
                            ["runtimePath"] = SelectedRuntime.ExecutablePath,
                            ["runtimeVersion"] = SelectedRuntime.VersionText,
                            ["candidateCount"] = discoveryResult.CandidateResults.Count
                        });
                    DialogResult = true;
                    return;
                }

                StatusMessageTextBlock.Text =
                    $"PowerShell 7 still was not found. Checked {discoveryResult.CandidateResults.Count} candidate(s). " +
                    "Click Open Logs Folder and send the startup/runtime logs to the developer, or browse directly to pwsh.exe.";
                DeveloperDiagnostics.LogDecision(
                    "Startup",
                    "StartupRuntimeRetryDiscoveryRequested",
                    "Retry discovery did not find a valid PowerShell 7 runtime from the startup resolver dialog.",
                    "StillNotFound",
                    new Dictionary<string, object?>
                    {
                        ["candidateCount"] = discoveryResult.CandidateResults.Count
                    });
            }
            catch (Exception ex)
            {
                StatusMessageTextBlock.Text = $"Automatic search failed: {ex.Message}";
                AppLogger.Error("StartupRuntimeResolver", "Retry discovery failed from startup resolver.", ex);
                DeveloperDiagnostics.LogException("Startup", ex, "Retry discovery failed from startup resolver.");
            }
        }

        private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var logFolder = ResolveLogsFolder();
            try
            {
                Directory.CreateDirectory(logFolder);
                TrySetClipboardText(logFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logFolder,
                    UseShellExecute = true
                });

                StatusMessageTextBlock.Text = $"Opened the logs folder and copied its path to the clipboard: {logFolder}";
                AppLogger.Info("StartupRuntimeResolver", $"Opened logs folder from startup resolver: {logFolder}");
            }
            catch (Exception ex)
            {
                StatusMessageTextBlock.Text = $"Could not open the logs folder. Path: {logFolder}{Environment.NewLine}{ex.Message}";
                AppLogger.Error("StartupRuntimeResolver", $"Failed to open logs folder from startup resolver: {logFolder}", ex);
            }
        }

        private void CopyLogsPathButton_Click(object sender, RoutedEventArgs e)
        {
            var logFolder = ResolveLogsFolder();
            try
            {
                Directory.CreateDirectory(logFolder);
                TrySetClipboardText(logFolder);
                StatusMessageTextBlock.Text = $"Logs folder path copied to clipboard: {logFolder}";
                AppLogger.Info("StartupRuntimeResolver", $"Copied logs folder path from startup resolver: {logFolder}");
            }
            catch (Exception ex)
            {
                StatusMessageTextBlock.Text = $"Could not copy the logs folder path. Path: {logFolder}{Environment.NewLine}{ex.Message}";
                AppLogger.Error("StartupRuntimeResolver", $"Failed to copy logs folder path from startup resolver: {logFolder}", ex);
            }
        }

        private void CopyHelpTextButton_Click(object sender, RoutedEventArgs e)
        {
            var helpText =
                "PS7 ScriptDesk requires PowerShell 7.0 or newer. " +
                "Install PowerShell 7 from Microsoft, then restart PS7 ScriptDesk. " +
                "If PowerShell 7 is already installed, browse to pwsh.exe. Common path: " +
                @"C:\Program Files\PowerShell\7\pwsh.exe" +
                ". Microsoft Store alias path: " +
                @"%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe" +
                ". Do not select powershell.exe; that is Windows PowerShell 5.1.";

            if (TrySetClipboardText(helpText))
            {
                StatusMessageTextBlock.Text = "PowerShell 7 help text copied to clipboard.";
                AppLogger.Info("StartupRuntimeResolver", "Copied PowerShell 7 help text from startup resolver.");
                return;
            }

            StatusMessageTextBlock.Text = helpText;
        }

        private static string ResolveLogsFolder()
        {
            return string.IsNullOrWhiteSpace(AppLogger.CurrentLogDirectory)
                ? Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs")
                : AppLogger.CurrentLogDirectory;
        }

        private static bool TrySetClipboardText(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ValidateAndAccept(string candidatePath)
        {
            var validationResult = _runtimeService.ValidateRuntimePath(candidatePath, "Startup runtime resolver");
            var properties = new Dictionary<string, object?>
            {
                ["candidatePath"] = candidatePath,
                ["candidateExists"] = validationResult.CandidateInfo.Exists,
                ["validationSucceeded"] = validationResult.IsValid,
                ["failureReason"] = validationResult.FailureReason
            };

            if (validationResult.IsValid)
            {
                SelectedRuntime = validationResult.RuntimeInfo;
                StatusMessageTextBlock.Text = $"Accepted {SelectedRuntime!.DisplayName} at {SelectedRuntime.ExecutablePath}.";
                DeveloperDiagnostics.LogDecision("Startup", "StartupRuntimeSelected", "User selected a valid PowerShell 7 runtime from the startup resolver dialog.", "Accepted", properties);
                DialogResult = true;
                return;
            }

            StatusMessageTextBlock.Text = BuildFailureMessage(candidatePath, validationResult);
            DeveloperDiagnostics.LogDecision("Startup", "StartupRuntimeSelected", "User selected an invalid runtime from the startup resolver dialog.", "Rejected", properties);
        }

        private static string BuildFailureMessage(string candidatePath, RuntimeValidationResult validationResult)
        {
            var failureReason = string.IsNullOrWhiteSpace(validationResult.FailureReason)
                ? "The selected executable could not be used."
                : validationResult.FailureReason;

            if (string.Equals(Path.GetFileName(candidatePath), "powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows PowerShell 5.1 is not supported. Select pwsh.exe from a PowerShell 7.x installation.";
            }

            return $"{failureReason}{Environment.NewLine}{Environment.NewLine}PS7 ScriptDesk requires pwsh.exe from PowerShell 7.0 or newer.";
        }
    }
}
