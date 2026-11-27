using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Shell.Services;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace PS7ScriptDesk.Shell.Presentation
{
    internal sealed class StoreUpdatePresenter
    {
        private readonly Window _owner;
        private readonly ContentControl _bannerHost;
        private readonly Action<string> _updateStatus;

        internal StoreUpdatePresenter(Window owner, ContentControl bannerHost, Action<string> updateStatus)
        {
            _owner = owner;
            _bannerHost = bannerHost;
            _updateStatus = updateStatus;
        }

        internal void ShowStoreUpdateBanner(StoreUpdateService service, StoreUpdateCheckResult result)
        {
            if (!_owner.Dispatcher.CheckAccess())
            {
                _owner.Dispatcher.BeginInvoke(() => ShowStoreUpdateBanner(service, result));
                return;
            }

            if (_bannerHost.Content is StoreUpdateBanner)
            {
                return;
            }

            var banner = new StoreUpdateBanner(service, result);
            banner.DismissRequested += StoreUpdateBanner_DismissRequested;
            _bannerHost.Content = banner;
            _bannerHost.Visibility = Visibility.Visible;
            DeveloperDiagnostics.LogDecision("StoreUpdate", "InlineBannerPresentation", "Displayed the optional Store update as a dedicated shell layout region.", "Shown",
                new Dictionary<string, object?> { ["currentVersion"] = result.PackageVersion, ["updateCount"] = result.UpdateCount });
        }

        private void StoreUpdateBanner_DismissRequested(object? sender, EventArgs e)
        {
            if (sender is StoreUpdateBanner banner)
            {
                banner.DismissRequested -= StoreUpdateBanner_DismissRequested;
            }

            _bannerHost.Content = null;
            _bannerHost.Visibility = Visibility.Collapsed;
            DeveloperDiagnostics.LogDecision("StoreUpdate", "InlineBannerPresentation", "Collapsed the optional Store update banner after dismissal.", "Hidden");
        }

        internal async Task CheckForUpdatesAsync(object sender)
        {
            DeveloperDiagnostics.LogUserAction(
                "StoreUpdate",
                "ManualCheckRequested",
                "User requested a fresh Microsoft Store update check.");

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            if (!StoreUpdateStartupState.TryStartManualCheck(cancellation.Token, out var checkTask))
            {
                _updateStatus("A Store update check is already in progress.");
                return;
            }

            var closed = false;
            void CancelOnClose(object? _, EventArgs __)
            {
                closed = true;
                cancellation.Cancel();
            }

            _owner.Closed += CancelOnClose;
            if (sender is WpfMenuItem menuItem)
            {
                menuItem.IsEnabled = false;
            }

            _updateStatus("Checking Microsoft Store for updates...");

            if (_bannerHost.Content is StoreUpdateBanner banner)
            {
                banner.DismissRequested -= StoreUpdateBanner_DismissRequested;
                _bannerHost.Content = null;
                _bannerHost.Visibility = Visibility.Collapsed;
            }

            try
            {
                var completed = await checkTask!;
                if (closed)
                {
                    return;
                }

                DeveloperDiagnostics.LogDecision(
                    "StoreUpdate",
                    "ManualCheckCompleted",
                    "Fresh Microsoft Store update check completed.",
                    ClassifyStoreUpdateResult(completed.Result),
                    BuildStoreUpdateDiagnosticsProperties(completed.Result, "HelpMenu"));
                ShowManualStoreUpdateWindow(completed.Service, completed.Result);
            }
            catch (OperationCanceledException) when (closed)
            {
                // The owning window is gone; no result window should be shown.
            }
            catch (Exception ex)
            {
                AppLogger.Error("StoreUpdate", "Manual Microsoft Store update check failed.", ex);
                DeveloperDiagnostics.LogException("StoreUpdate", ex, "Manual Microsoft Store update check failed.");
                if (!closed)
                {
                    var failedResult = new StoreUpdateCheckResult
                    {
                        AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable,
                        StatusMessage = cancellation.IsCancellationRequested
                            ? "Microsoft Store update check timed out."
                            : "Microsoft Store update check failed.",
                        ExceptionSummary = $"{ex.GetType().Name}: {ex.Message}",
                        ShouldShowManualInstructions = true,
                        ManualInstructions = "Microsoft Store -> Library -> Get updates."
                    };
                    ShowManualStoreUpdateWindow(StoreUpdateStartupState.Read().Service ?? new StoreUpdateService(), failedResult);
                }
            }
            finally
            {
                _owner.Closed -= CancelOnClose;
                if (!closed && sender is WpfMenuItem enabledMenuItem)
                {
                    enabledMenuItem.IsEnabled = true;
                }
            }
        }

        private void ShowManualStoreUpdateWindow(StoreUpdateService storeUpdateService, StoreUpdateCheckResult checkResult)
        {
            var updateWindow = new StoreUpdateWindow(storeUpdateService, checkResult, isMandatory: checkResult.HasMandatoryUpdate)
            {
                Owner = _owner
            };

            var resultKind = ClassifyStoreUpdateResult(checkResult);
            DeveloperDiagnostics.LogDecision(
                "StoreUpdate",
                "ManualUpdateWindowDisplay",
                "Manual Store update result window displayed.",
                checkResult.HasMandatoryUpdate ? "ShownMandatoryDialog" : "ShownModelessWindow",
                BuildStoreUpdateDiagnosticsProperties(checkResult, "HelpMenu"));

            _updateStatus(resultKind switch
            {
                "MandatoryUpdate" => "Mandatory update detected.",
                "UpdateAvailable" => "Update available.",
                "NoUpdateAvailable" => "No updates available.",
                "ManualInstructions" => "Manual Store update instructions shown.",
                "LocalBuildUnavailable" => "Store updates are not available for this local build.",
                "CheckUnavailable" => "Update check unavailable.",
                "Failure" => "Update check failed.",
                _ => "Update check complete."
            });

            if (checkResult.HasMandatoryUpdate)
            {
                updateWindow.ShowDialog();
                System.Windows.Application.Current?.Shutdown(0);
                return;
            }

            updateWindow.Show();
            updateWindow.Activate();
        }

        private static IReadOnlyDictionary<string, object?> BuildStoreUpdateDiagnosticsProperties(StoreUpdateCheckResult? checkResult, string invocation)
        {
            var properties = new Dictionary<string, object?>
            {
                ["invocation"] = invocation,
                ["cancellationSupported"] = false
            };

            if (checkResult is null)
            {
                properties["resultAvailable"] = false;
                return properties;
            }

            properties["resultAvailable"] = true;
            properties["packagingKind"] = checkResult.PackagingKind.ToString();
            properties["availabilityState"] = checkResult.AvailabilityState.ToString();
            properties["storeUpdateCheckAvailable"] = checkResult.StoreUpdateCheckAvailable;
            properties["storeContextAttempted"] = checkResult.StoreContextAttempted;
            properties["storeContextAvailable"] = checkResult.StoreContextAvailable;
            properties["isDevelopmentMode"] = checkResult.IsDevelopmentMode;
            properties["packageName"] = checkResult.PackageName;
            properties["packageFamilyName"] = checkResult.PackageFamilyName;
            properties["packagePublisherId"] = checkResult.PackagePublisherId;
            properties["packageVersion"] = checkResult.PackageVersion;
            properties["packageSignatureKind"] = checkResult.PackageSignatureKind;
            properties["packageIdentityApi"] = checkResult.PackageIdentityApi;
            properties["packageTypeAvailable"] = checkResult.PackageTypeAvailable;
            properties["packageCurrentAvailable"] = checkResult.PackageCurrentAvailable;
            properties["packageIdentityReadSucceeded"] = checkResult.PackageIdentityReadSucceeded;
            properties["packageIdentityReadFailure"] = checkResult.PackageIdentityReadFailure;
            properties["packageIdentityFallbackSource"] = checkResult.PackageIdentityFallbackSource;
            properties["perPackageUpdateListReturned"] = checkResult.PerPackageUpdateListReturned;
            properties["updateCount"] = checkResult.UpdateCount;
            properties["hasMandatoryUpdate"] = checkResult.HasMandatoryUpdate;
            properties["hasConfirmedInstallableUpdate"] = checkResult.HasConfirmedInstallableUpdate;
            properties["shouldShowManualInstructions"] = checkResult.ShouldShowManualInstructions;
            properties["resultKind"] = ClassifyStoreUpdateResult(checkResult);
            properties["exceptionSummary"] = string.IsNullOrWhiteSpace(checkResult.ExceptionSummary) ? null : checkResult.ExceptionSummary;
            return properties;
        }

        private static string ClassifyStoreUpdateResult(StoreUpdateCheckResult checkResult)
        {
            if (!string.IsNullOrWhiteSpace(checkResult.ExceptionSummary))
            {
                return "Failure";
            }

            if (checkResult.HasMandatoryUpdate)
            {
                return "MandatoryUpdate";
            }

            if (checkResult.HasConfirmedInstallableUpdate)
            {
                return "UpdateAvailable";
            }

            if (checkResult.PackagingKind == StoreUpdatePackagingKind.UnpackagedLocalBuild)
            {
                return "LocalBuildUnavailable";
            }

            return checkResult.AvailabilityState switch
            {
                StoreUpdateAvailabilityState.NoUpdateAvailable => "NoUpdateAvailable",
                StoreUpdateAvailabilityState.ManualCheckRequired => "ManualInstructions",
                StoreUpdateAvailabilityState.UpdateCheckUnavailable => "CheckUnavailable",
                StoreUpdateAvailabilityState.ConfirmedUpdateAvailable => "UpdateAvailable",
                _ => "Unknown"
            };
        }
    }
}
