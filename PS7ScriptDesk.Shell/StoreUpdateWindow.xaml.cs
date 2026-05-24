using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Shell
{
    public partial class StoreUpdateWindow : Window
    {
        private readonly StoreUpdateService _storeUpdateService;
        private readonly StoreUpdateCheckResult _checkResult;
        private readonly bool _isMandatory;
        private bool _installInProgress;

        public StoreUpdateWindow(StoreUpdateService storeUpdateService, StoreUpdateCheckResult checkResult, bool isMandatory)
        {
            _storeUpdateService = storeUpdateService ?? throw new ArgumentNullException(nameof(storeUpdateService));
            _checkResult = checkResult ?? throw new ArgumentNullException(nameof(checkResult));
            _isMandatory = isMandatory;

            InitializeComponent();
            ConfigureWindow();
        }

        private void ConfigureWindow()
        {
            Title = BuildWindowTitle();
            TitleTextBlock.Text = BuildTitleText();

            MessageTextBlock.Text = BuildMessageText();
            StatusMessageTextBlock.Text = BuildInitialStatusText();
            CloseOrExitButton.Content = _isMandatory ? "Exit" : "Close";
            InstallNowButton.IsEnabled = _checkResult.HasConfirmedInstallableUpdate;

            var items = new List<string>();
            if (_checkResult.HasConfirmedInstallableUpdate)
            {
                foreach (var update in _checkResult.Updates)
                {
                    items.Add($"{update.PackageFamilyName} | Mandatory: {update.IsMandatory}");
                }
            }
            else
            {
                switch (_checkResult.AvailabilityState)
                {
                    case StoreUpdateAvailabilityState.NoUpdateAvailable:
                        items.Add("No installable Microsoft Store update packages were returned.");
                        break;
                    case StoreUpdateAvailabilityState.ManualCheckRequired:
                    case StoreUpdateAvailabilityState.UpdateCheckUnavailable:
                        items.Add(_checkResult.StatusMessage);
                        break;
                    default:
                        items.Add("No installable Microsoft Store update packages were returned.");
                        break;
                }
            }

            UpdatesItemsControl.ItemsSource = items;
        }

        private string BuildWindowTitle()
        {
            return _checkResult.AvailabilityState switch
            {
                StoreUpdateAvailabilityState.ConfirmedUpdateAvailable => $"{ApplicationBranding.PublicName} - Update Available",
                StoreUpdateAvailabilityState.ManualCheckRequired when _checkResult.PackagingKind == StoreUpdatePackagingKind.PackagedSideloadedOrTest => $"{ApplicationBranding.PublicName} - Test Package Update Check",
                StoreUpdateAvailabilityState.UpdateCheckUnavailable => $"{ApplicationBranding.PublicName} - Update Check",
                StoreUpdateAvailabilityState.ManualCheckRequired => $"{ApplicationBranding.PublicName} - Update Check",
                StoreUpdateAvailabilityState.NoUpdateAvailable => $"{ApplicationBranding.PublicName} - Update Check",
                _ => $"{ApplicationBranding.PublicName} - Update Check",
            };
        }

        private string BuildTitleText()
        {
            if (_checkResult.AvailabilityState == StoreUpdateAvailabilityState.ConfirmedUpdateAvailable)
            {
                return _isMandatory
                    ? "A Microsoft Store update is required before you can continue."
                    : "A Microsoft Store update is available.";
            }

            if (_checkResult.PackagingKind == StoreUpdatePackagingKind.PackagedSideloadedOrTest)
            {
                return "This appears to be a sideloaded or test package.";
            }

            return "Microsoft Store update check";
        }

        private string BuildMessageText()
        {
            if (_checkResult.PackagingKind == StoreUpdatePackagingKind.UnpackagedLocalBuild)
            {
                return "This is an unpackaged or local build.\n\nMicrosoft Store update checks are not available for this build.";
            }

            if (_checkResult.PackagingKind == StoreUpdatePackagingKind.PackagedSideloadedOrTest)
            {
                return "This appears to be a sideloaded or test package. Microsoft Store update checks may not be available for this build.\n\n" +
                       "If you expected an update, install a newer test package manually or use the source that provided this package.";
            }

            if (_checkResult.AvailabilityState is StoreUpdateAvailabilityState.ManualCheckRequired or StoreUpdateAvailabilityState.UpdateCheckUnavailable)
            {
                return "PS7 ScriptDesk could not confirm an installable Microsoft Store update package for this build.\n\n" +
                       "Open Microsoft Store and use:\nMicrosoft Store -> Library -> Get updates.";
            }

            if (_checkResult.AvailabilityState == StoreUpdateAvailabilityState.NoUpdateAvailable)
            {
                return "No Microsoft Store updates are currently available for this PS7 ScriptDesk installation.";
            }

            if (_isMandatory)
            {
                return "A mandatory Microsoft Store update was detected for this PS7 ScriptDesk installation.\n\n" +
                       "Install the update now, or open Microsoft Store and update the app manually before launching it again.";
            }

            return "An optional Microsoft Store update is available for this PS7 ScriptDesk installation.\n\n" +
                   "You can keep working and install it later, or start the update now.";
        }

        private string BuildInitialStatusText()
        {
            if (!string.IsNullOrWhiteSpace(_checkResult.ExceptionSummary))
            {
                return $"{_checkResult.StatusMessage}{Environment.NewLine}{_checkResult.ExceptionSummary}";
            }

            return string.IsNullOrWhiteSpace(_checkResult.StatusMessage)
                ? _checkResult.ManualInstructions
                : _checkResult.StatusMessage;
        }

        private async void InstallNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_installInProgress)
            {
                return;
            }

            _installInProgress = true;
            InstallNowButton.IsEnabled = false;
            StatusMessageTextBlock.Text = "Starting Microsoft Store update install request...";

            DeveloperDiagnostics.LogUserAction(
                "StoreUpdate",
                "InstallNowClicked",
                "User requested Microsoft Store update install.",
                new Dictionary<string, object?>
                {
                    ["isMandatory"] = _isMandatory,
                    ["updateCount"] = _checkResult.UpdateCount
                });

            var progress = new Progress<StoreUpdateInstallProgressInfo>(info =>
            {
                var progressText = string.IsNullOrWhiteSpace(info.PackageFamilyName)
                    ? $"Store update progress: {info.PackageUpdateState} {info.PackageDownloadProgress} {info.Status}".Trim()
                    : $"Store update progress for {info.PackageFamilyName}: {info.PackageUpdateState} {info.PackageDownloadProgress} {info.Status}".Trim();
                StatusMessageTextBlock.Text = progressText;
            });

            try
            {
                var installResult = await _storeUpdateService.RequestInstallAsync(_checkResult, progress, CancellationToken.None).ConfigureAwait(true);
                var statusLines = new List<string>();
                if (!string.IsNullOrWhiteSpace(installResult.OverallState))
                {
                    statusLines.Add($"Overall state: {installResult.OverallState}");
                }

                foreach (var packageStatus in installResult.PackageStatuses)
                {
                    statusLines.Add(
                        $"{packageStatus.PackageFamilyName}: State={packageStatus.PackageUpdateState}, Progress={packageStatus.PackageDownloadProgress}, " +
                        $"Status={packageStatus.Status}, ErrorCode={packageStatus.ErrorCode}, Message={packageStatus.StatusMessage}");
                }

                if (!string.IsNullOrWhiteSpace(installResult.ExceptionSummary))
                {
                    statusLines.Add(installResult.ExceptionSummary);
                }

                StatusMessageTextBlock.Text = statusLines.Count == 0
                    ? "Microsoft Store update request completed."
                    : string.Join(Environment.NewLine, statusLines);
            }
            finally
            {
                _installInProgress = false;
                InstallNowButton.IsEnabled = _checkResult.HasConfirmedInstallableUpdate;
            }
        }

        private void OpenStoreButton_Click(object sender, RoutedEventArgs e)
        {
            const string storeUri = "ms-windows-store://downloadsandupdates";
            var instructionText = "Open Microsoft Store and use: Library -> Get updates.";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = storeUri,
                    UseShellExecute = true
                });

                StatusMessageTextBlock.Text = $"Opened Microsoft Store. {instructionText}";
                AppLogger.Info("StoreUpdate", $"Opened Microsoft Store updates URI: {storeUri}");
                DeveloperDiagnostics.LogUserAction("StoreUpdate", "OpenStore", "Opened Microsoft Store update page.", new Dictionary<string, object?> { ["uri"] = storeUri });
            }
            catch (Exception ex)
            {
                StatusMessageTextBlock.Text = $"{instructionText}{Environment.NewLine}{Environment.NewLine}Store launch failed: {ex.Message}";
                AppLogger.Error("StoreUpdate", "Failed to open Microsoft Store update page.", ex);
                DeveloperDiagnostics.LogException("StoreUpdate", ex, "Failed to open Microsoft Store update page.");
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
                AppLogger.Info("StoreUpdate", $"Opened logs folder from Store update dialog: {logFolder}");
            }
            catch (Exception ex)
            {
                StatusMessageTextBlock.Text = $"Could not open the logs folder. Path: {logFolder}{Environment.NewLine}{ex.Message}";
                AppLogger.Error("StoreUpdate", $"Failed to open logs folder from Store update dialog: {logFolder}", ex);
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
                AppLogger.Info("StoreUpdate", $"Copied logs folder path from Store update dialog: {logFolder}");
            }
            catch (Exception ex)
            {
                StatusMessageTextBlock.Text = $"Could not copy the logs folder path. Path: {logFolder}{Environment.NewLine}{ex.Message}";
                AppLogger.Error("StoreUpdate", $"Failed to copy logs folder path from Store update dialog: {logFolder}", ex);
            }
        }

        private void CloseOrExitButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isMandatory && System.Windows.Application.Current?.MainWindow is not null && System.Windows.Application.Current.MainWindow != this)
            {
                DeveloperDiagnostics.LogDecision("StoreUpdate", "MandatoryDialogClosing", "Mandatory Store update dialog closed; the app will exit without opening the main shell.", "Exit");
            }

            base.OnClosing(e);
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
    }
}
