using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Shell;

public partial class StoreUpdateBanner : UserControl
{
    private readonly StoreUpdateService _service;
    private readonly StoreUpdateCheckResult _result;
    private bool _installInProgress;
    private bool _installRequestAccepted;

    public StoreUpdateBanner(StoreUpdateService service, StoreUpdateCheckResult result)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _result = result ?? throw new ArgumentNullException(nameof(result));
        InitializeComponent();
        Configure();
    }

    public event EventHandler? DismissRequested;

    private void Configure()
    {
        TitleTextBlock.Text = _result.HasMandatoryUpdate ? "PowerShell update required" : "PowerShell update available";
        var availableVersions = _result.Updates
            .Select(static update => update.Version)
            .Where(static version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var versionText = availableVersions.Length == 0
            ? string.Empty
            : $" Available: {string.Join(", ", availableVersions)}.";
        var currentText = string.IsNullOrWhiteSpace(_result.PackageVersion)
            ? string.Empty
            : $" Current: {_result.PackageVersion}.";
        DetailsTextBlock.Text = $"A newer PS7 ScriptDesk package is ready.{currentText}{versionText} Installing it may close PS7 ScriptDesk and automatically restart it. You can postpone this update by dismissing this notification.";
        InstallButton.IsEnabled = _result.HasConfirmedInstallableUpdate;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installInProgress)
        {
            return;
        }

        _installInProgress = true;
        InstallButton.IsEnabled = false;
        DetailsTextBlock.Text = "Installing this update may close PS7 ScriptDesk and automatically restart it. Your editor changes will be saved through the normal close workflow. You can postpone the update by dismissing this notification.";
        DeveloperDiagnostics.LogUserAction("StoreUpdate", "BannerInstallClicked", "User requested the Microsoft Store update from the inline banner.",
            new Dictionary<string, object?> { ["updateCount"] = _result.UpdateCount, ["currentVersion"] = _result.PackageVersion });

        try
        {
            var owner = Window.GetWindow(this);
            if (owner is not MainWindow mainWindow || !StoreUpdateRestartCoordinator.TryPrepareForStoreUpdate(mainWindow))
            {
                DetailsTextBlock.Text = "Update canceled so unsaved editor changes were not discarded.";
                return;
            }

            var ownerHwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var progress = new Progress<StoreUpdateInstallProgressInfo>(info =>
                DetailsTextBlock.Text = $"Store update: {info.PackageUpdateState} {info.PackageDownloadProgress} {info.Status}".Trim());
            var installResult = await _service.RequestInstallAsync(_result, ownerHwnd, progress, CancellationToken.None).ConfigureAwait(true);
            _installRequestAccepted = installResult.RequestStarted;
            DetailsTextBlock.Text = installResult.RequestStarted
                ? "Microsoft Store accepted the update request."
                : "Microsoft Store did not start the update request. Open Store for manual instructions.";
            if (!string.IsNullOrWhiteSpace(installResult.ExceptionSummary))
            {
                DetailsTextBlock.Text += $" {installResult.ExceptionSummary}";
            }

            if (installResult.IsSuccessfulInstallation)
            {
                DetailsTextBlock.Text = "Update installed successfully. PS7 ScriptDesk will restart automatically.";
                await Task.Delay(StoreUpdateRestartCoordinator.RestartNotificationDisplayDuration).ConfigureAwait(true);
                var restart = StoreUpdateRestartCoordinator.TryRestartAfterSuccessfulInstall(_result.PackageVersion);
                DetailsTextBlock.Text = restart.RestartPending
                    ? "Update installed successfully. PS7 ScriptDesk will restart automatically."
                    : $"Update installed successfully, but automatic restart is unavailable. {restart.FailureMessage} Please reopen PS7 ScriptDesk to use the updated package.";
            }
        }
        catch (Exception ex)
        {
            DetailsTextBlock.Text = "The Microsoft Store update request failed. Open Store for manual instructions.";
            AppLogger.Error("StoreUpdate", "Inline Store update banner install request failed.", ex);
            DeveloperDiagnostics.LogException("StoreUpdate", ex, "Inline Store update banner install request failed.");
        }
        finally
        {
            _installInProgress = false;
            InstallButton.IsEnabled = _result.HasConfirmedInstallableUpdate && !_installRequestAccepted;
        }
    }

    private void OpenStoreButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-windows-store://downloadsandupdates", UseShellExecute = true });
            DeveloperDiagnostics.LogUserAction("StoreUpdate", "BannerStoreClicked", "Opened Microsoft Store from the inline update banner.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StoreUpdate", "Failed to open Microsoft Store from the inline update banner.", ex);
            DeveloperDiagnostics.LogException("StoreUpdate", ex, "Failed to open Microsoft Store from the inline update banner.");
        }
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        DeveloperDiagnostics.LogUserAction("StoreUpdate", "BannerDismissed", "User dismissed the inline Store update banner.");
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
