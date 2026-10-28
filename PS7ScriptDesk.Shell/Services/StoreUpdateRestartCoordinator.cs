using System;
using System.Threading;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Microsoft.Windows.AppLifecycle;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Services;

internal static class StoreUpdateRestartCoordinator
{
    internal const string RestartArgumentPrefix = "--store-update-restart-from=";
    internal static readonly TimeSpan RestartNotificationDisplayDuration = TimeSpan.FromSeconds(3);
    private static int _restartRequested;

    internal static bool TryPrepareForStoreUpdate(MainWindow mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        return mainWindow.TryPrepareForStoreUpdate();
    }

    internal static StoreUpdateRestartAttempt TryRestartAfterSuccessfulInstall(string previousPackageVersion)
    {
        if (Interlocked.CompareExchange(ref _restartRequested, 1, 0) != 0)
        {
            return new StoreUpdateRestartAttempt(false, true, "A Store update restart is already pending for this process.");
        }

        try
        {
            if (!TryReadPackageIdentity(out var packageName, out var currentVersion, out var identityFailure))
            {
                Interlocked.Exchange(ref _restartRequested, 0);
                return new StoreUpdateRestartAttempt(false, false, identityFailure);
            }

            previousPackageVersion = string.IsNullOrWhiteSpace(previousPackageVersion)
                ? currentVersion
                : previousPackageVersion;
            var arguments = $"{RestartArgumentPrefix}{previousPackageVersion}";
            var failureReason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(arguments);
            if (failureReason == AppRestartFailureReason.RestartPending)
            {
                DeveloperDiagnostics.LogDecision(
                    "StoreUpdate",
                    "AutomaticRestart",
                    "Windows reported that another packaged application restart is already pending; no second restart request was issued.",
                    "RestartAlreadyPending");
                return new StoreUpdateRestartAttempt(true, true, "Another packaged application restart is already pending.");
            }

            Interlocked.Exchange(ref _restartRequested, 0);
            var failure = $"Windows App SDK could not restart the packaged application (reason={failureReason}).";
            DeveloperDiagnostics.LogDecision(
                "StoreUpdate",
                "AutomaticRestart",
                failure,
                "RestartFailed",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["packageName"] = packageName,
                    ["packageVersion"] = currentVersion,
                    ["previousPackageVersion"] = previousPackageVersion,
                    ["failureReason"] = failureReason.ToString()
                });
            return new StoreUpdateRestartAttempt(false, false, failure);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _restartRequested, 0);
            DeveloperDiagnostics.LogException("StoreUpdate", ex, "Automatic packaged application restart failed.");
            return new StoreUpdateRestartAttempt(false, false, $"Automatic restart failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static StoreUpdateRestartStartupState ReadStartupState(string[] args)
    {
        var argument = Array.Find(args, value => value.StartsWith(RestartArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        var previousVersion = argument is null ? string.Empty : argument[RestartArgumentPrefix.Length..];
        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            return new StoreUpdateRestartStartupState(false, string.Empty, string.Empty, false, "");
        }

        if (!TryReadPackageIdentity(out var packageName, out var currentVersion, out var identityFailure))
        {
            return new StoreUpdateRestartStartupState(true, packageName, currentVersion, false, identityFailure);
        }

        var versionChanged = Version.TryParse(previousVersion, out var oldVersion) &&
            Version.TryParse(currentVersion, out var newVersion) &&
            newVersion > oldVersion;
        return new StoreUpdateRestartStartupState(true, packageName, currentVersion, versionChanged,
            versionChanged ? string.Empty : $"Relaunched package version '{currentVersion}' was not newer than '{previousVersion}'.");
    }

    private static bool TryReadPackageIdentity(out string packageName, out string packageVersion, out string failure)
    {
        try
        {
            var package = Package.Current;
            packageName = package.Id.Name;
            var version = package.Id.Version;
            packageVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            packageName = string.Empty;
            packageVersion = string.Empty;
            failure = $"Package identity was unavailable; automatic restart was not attempted: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    internal sealed record StoreUpdateRestartAttempt(bool RestartPending, bool DuplicateRequest, string FailureMessage);

    internal sealed record StoreUpdateRestartStartupState(
        bool WasRestarted,
        string PackageName,
        string PackageVersion,
        bool PackageVersionAdvanced,
        string FailureMessage);
}
