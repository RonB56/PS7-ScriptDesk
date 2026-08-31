using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace PS7ScriptDesk.Shell.Services
{
    public sealed class StoreUpdateService
    {
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
        private readonly IStorePackageEnvironmentProvider _packageEnvironmentProvider;
        private readonly IStoreUpdateQuery _storeUpdateQuery;

        public StoreUpdateService()
            : this(new WindowsStorePackageEnvironmentProvider(), new DirectStoreUpdateQuery())
        {
        }

        internal StoreUpdateService(IStorePackageEnvironmentProvider packageEnvironmentProvider, IStoreUpdateQuery storeUpdateQuery)
        {
            _packageEnvironmentProvider = packageEnvironmentProvider ?? throw new ArgumentNullException(nameof(packageEnvironmentProvider));
            _storeUpdateQuery = storeUpdateQuery ?? throw new ArgumentNullException(nameof(storeUpdateQuery));
        }

        public async Task<StoreUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            var operationId = $"StoreUpdateCheck-{Guid.NewGuid():N}";
            using var scope = DeveloperDiagnostics.BeginTimedOperation(
                "StoreUpdate",
                "CheckForUpdates",
                "Store update detection started.",
                operationId: operationId);

            var stopwatch = Stopwatch.StartNew();
            var result = new StoreUpdateCheckResult
            {
                ManualInstructions = "Microsoft Store -> Library -> Get updates."
            };

            try
            {
                ApplyPackageEnvironment(result, _packageEnvironmentProvider.ReadPackageEnvironment());
                LogCheckStep(
                    "Store packaging state resolved.",
                    new Dictionary<string, object?>
                    {
                        ["isPackaged"] = result.IsPackaged,
                        ["isStoreManaged"] = result.IsStoreManaged,
                        ["isDevelopmentMode"] = result.IsDevelopmentMode,
                        ["packageName"] = result.PackageName,
                        ["packageFamilyName"] = result.PackageFamilyName,
                        ["packageFullName"] = result.PackageFullName,
                        ["packagePublisherId"] = result.PackagePublisherId,
                        ["packageVersion"] = result.PackageVersion,
                        ["signatureKind"] = result.PackageSignatureKind,
                        ["packageIdentityApi"] = result.PackageIdentityApi,
                        ["packageTypeAvailable"] = result.PackageTypeAvailable,
                        ["packageCurrentAvailable"] = result.PackageCurrentAvailable,
                        ["packageIdentityReadSucceeded"] = result.PackageIdentityReadSucceeded,
                        ["packageIdentityReadFailure"] = result.PackageIdentityReadFailure,
                        ["fallbackSource"] = result.PackageIdentityFallbackSource
                    });

                if (!result.IsPackaged)
                {
                    result.PackagingKind = StoreUpdatePackagingKind.UnpackagedLocalBuild;
                    result.AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable;
                    result.StoreUpdateCheckAvailable = false;
                    result.StatusMessage = "This is an unpackaged or local build. Microsoft Store update checks are not available.";
                    LogCheckStep("Store update checking skipped because the app is unpackaged/local.");
                    return result;
                }

                if (!result.IsStoreManaged)
                {
                    result.PackagingKind = ClassifyPackagedNonStoreInstall(result);
                    result.AvailabilityState = StoreUpdateAvailabilityState.ManualCheckRequired;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = BuildNonStorePackageStatusMessage(result.PackagingKind);
                    LogCheckStep(
                        "The app appears packaged, but no Store-managed update path was confirmed for this build.",
                        new Dictionary<string, object?>
                        {
                            ["signatureKind"] = result.PackageSignatureKind,
                            ["isDevelopmentMode"] = result.IsDevelopmentMode,
                            ["packageFamilyName"] = result.PackageFamilyName,
                            ["packageIdentityApi"] = result.PackageIdentityApi,
                            ["packageIdentityReadSucceeded"] = result.PackageIdentityReadSucceeded,
                            ["packageIdentityReadFailure"] = result.PackageIdentityReadFailure,
                            ["fallbackSource"] = result.PackageIdentityFallbackSource
                        });
                    return result;
                }

                result.PackagingKind = StoreUpdatePackagingKind.StoreInstalledManaged;

                var queryResult = await _storeUpdateQuery.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                result.StoreContextAttempted = queryResult.StoreContextAttempted;
                result.StoreContextAvailable = queryResult.StoreContextAvailable;
                result.RawStoreContext = queryResult.RawStoreContext;
                result.RawUpdatesCollection = queryResult.RawUpdatesCollection;
                result.PerPackageUpdateListReturned = queryResult.PerPackageUpdateListReturned;

                if (queryResult.AvailabilityState != StoreUpdateAvailabilityState.ConfirmedUpdateAvailable)
                {
                    result.AvailabilityState = queryResult.AvailabilityState;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = queryResult.StatusMessage;
                    LogCheckStep(
                        queryResult.LogMessage,
                        new Dictionary<string, object?>
                        {
                            ["packageFamilyName"] = result.PackageFamilyName,
                            ["signatureKind"] = result.PackageSignatureKind,
                            ["storeContextAttempted"] = result.StoreContextAttempted,
                            ["storeContextAvailable"] = result.StoreContextAvailable
                        });
                    return result;
                }

                result.StoreUpdateCheckAvailable = true;
                result.Updates = queryResult.Updates;
                result.UpdateCount = queryResult.Updates.Count;
                result.HasMandatoryUpdate = queryResult.Updates.Any(update => update.IsMandatory);

                LogCheckStep(
                    "Store update query completed.",
                    new Dictionary<string, object?>
                    {
                        ["storeContextAttempted"] = result.StoreContextAttempted,
                        ["storeContextAvailable"] = result.StoreContextAvailable,
                        ["perPackageUpdateListReturned"] = result.PerPackageUpdateListReturned,
                        ["updateCount"] = result.UpdateCount,
                        ["packageFamilyNames"] = string.Join(", ", result.Updates.Select(update => update.PackageFamilyName)),
                        ["mandatoryUpdatePresent"] = result.HasMandatoryUpdate
                    });

                foreach (var update in result.Updates)
                {
                    LogCheckStep(
                        "Store update candidate found.",
                        new Dictionary<string, object?>
                        {
                            ["packageFamilyName"] = update.PackageFamilyName,
                            ["mandatory"] = update.IsMandatory
                        });
                }

                if (result.UpdateCount == 0)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.NoUpdateAvailable;
                    result.StatusMessage = "No Microsoft Store updates were available.";
                }
                else if (result.HasMandatoryUpdate)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.ConfirmedUpdateAvailable;
                    result.StatusMessage = "A mandatory Microsoft Store update is required before using PS7 ScriptDesk.";
                }
                else
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.ConfirmedUpdateAvailable;
                    result.StatusMessage = "An optional Microsoft Store update is available for PS7 ScriptDesk.";
                }

                return result;
            }
            catch (Exception ex)
            {
                if (result.PackagingKind == StoreUpdatePackagingKind.None && result.IsPackaged)
                {
                    result.PackagingKind = result.IsStoreManaged
                        ? StoreUpdatePackagingKind.StoreInstalledManaged
                        : ClassifyPackagedNonStoreInstall(result);
                }

                result.AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable;
                result.StoreUpdateCheckAvailable = false;
                result.ShouldShowManualInstructions = result.IsStoreManaged;
                result.ExceptionSummary = BuildExceptionSummary(ex);
                result.StatusMessage = "Microsoft Store update detection failed.";
                LogCheckException("Store update detection failed.", ex);
                return result;
            }
            finally
            {
                DeveloperDiagnostics.LogOperationStop(
                    "StoreUpdate",
                    "CheckForUpdates",
                    "Store update detection finished.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["isPackaged"] = result.IsPackaged,
                        ["packagingKind"] = result.PackagingKind.ToString(),
                        ["availabilityState"] = result.AvailabilityState.ToString(),
                        ["isStoreManaged"] = result.IsStoreManaged,
                        ["isDevelopmentMode"] = result.IsDevelopmentMode,
                        ["packageIdentityApi"] = result.PackageIdentityApi,
                        ["packageIdentityReadSucceeded"] = result.PackageIdentityReadSucceeded,
                        ["packageIdentityReadFailure"] = result.PackageIdentityReadFailure,
                        ["fallbackSource"] = result.PackageIdentityFallbackSource,
                        ["storeContextAttempted"] = result.StoreContextAttempted,
                        ["storeContextAvailable"] = result.StoreContextAvailable,
                        ["perPackageUpdateListReturned"] = result.PerPackageUpdateListReturned,
                        ["updateCount"] = result.UpdateCount,
                        ["hasMandatoryUpdate"] = result.HasMandatoryUpdate,
                        ["shouldShowManualInstructions"] = result.ShouldShowManualInstructions,
                        ["exceptionSummary"] = result.ExceptionSummary
                    });
            }
        }

        public async Task<StoreUpdateInstallResult> RequestInstallAsync(
            StoreUpdateCheckResult checkResult,
            IProgress<StoreUpdateInstallProgressInfo>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(checkResult);

            var operationId = $"StoreUpdateInstall-{Guid.NewGuid():N}";
            using var scope = DeveloperDiagnostics.BeginTimedOperation(
                "StoreUpdate",
                "InstallStoreUpdates",
                "Store update install request started.",
                operationId: operationId);

            var stopwatch = Stopwatch.StartNew();
            var result = new StoreUpdateInstallResult();

            try
            {
                if (checkResult.RawStoreContext is not StoreContext storeContext ||
                    checkResult.RawUpdatesCollection is not IReadOnlyList<StorePackageUpdate> rawUpdates)
                {
                    result.ExceptionSummary = "Store update install could not start because no direct Store update context was available.";
                    LogCheckStep(result.ExceptionSummary);
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();

                result.RequestStarted = true;
                LogCheckStep(
                    "Calling direct RequestDownloadAndInstallStorePackageUpdatesAsync.",
                    new Dictionary<string, object?>
                    {
                        ["updateCount"] = checkResult.UpdateCount,
                        ["packageFamilyNames"] = string.Join(", ", checkResult.Updates.Select(update => update.PackageFamilyName))
                    });

                // Microsoft requires this API to be initiated from the UI thread.
                // StoreUpdateWindow invokes this method from its WPF click handler.
                var operation = storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(rawUpdates);
                operation.Progress = (_, status) =>
                {
                    try
                    {
                        progress?.Report(new StoreUpdateInstallProgressInfo(
                            status.PackageFamilyName,
                            status.PackageUpdateState.ToString(),
                            status.PackageDownloadProgress.ToString("P0"),
                            $"Total={status.TotalDownloadProgress:P0}",
                            string.Empty,
                            "Started"));
                    }
                    catch (Exception progressException)
                    {
                        LogCheckException("Store update progress reporting failed.", progressException);
                    }
                };

                var installResult = await operation;
                result.OverallState = installResult.OverallState.ToString();
                result.PackageStatuses = installResult.StorePackageUpdateStatuses
                    .Select(status => new StoreUpdateInstallStatusInfo(
                        status.PackageFamilyName,
                        status.PackageUpdateState.ToString(),
                        status.PackageDownloadProgress.ToString("P0"),
                        $"Total={status.TotalDownloadProgress:P0}",
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty))
                    .ToList();

                LogCheckStep(
                    "Direct Store update install request completed.",
                    new Dictionary<string, object?> { ["overallState"] = result.OverallState });

                foreach (var status in result.PackageStatuses)
                {
                    LogCheckStep(
                        "Per-package Store update install status.",
                        new Dictionary<string, object?>
                        {
                            ["packageFamilyName"] = status.PackageFamilyName,
                            ["status"] = status.Status,
                            ["packageUpdateState"] = status.PackageUpdateState,
                            ["downloadProgress"] = status.PackageDownloadProgress
                        });
                }

                return result;
            }
            catch (OperationCanceledException ex)
            {
                result.ExceptionSummary = BuildExceptionSummary(ex);
                LogCheckException("Store update install request was canceled.", ex);
                return result;
            }
            catch (Exception ex)
            {
                result.ExceptionSummary = BuildExceptionSummary(ex);
                LogCheckException("Store update install request failed.", ex);
                return result;
            }
            finally
            {
                DeveloperDiagnostics.LogOperationStop(
                    "StoreUpdate",
                    "InstallStoreUpdates",
                    "Store update install request finished.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["requestStarted"] = result.RequestStarted,
                        ["overallState"] = result.OverallState,
                        ["packageStatusCount"] = result.PackageStatuses.Count,
                        ["exceptionSummary"] = result.ExceptionSummary
                    });
            }
        }

        internal static void ApplyPackageEnvironment(StoreUpdateCheckResult result, StorePackageEnvironmentInfo packageEnvironment)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(packageEnvironment);

            var inferredPackagedFallback = !packageEnvironment.HasPackageIdentity && packageEnvironment.IsInferredPackagedFallback;
            result.IsPackaged = packageEnvironment.HasPackageIdentity || inferredPackagedFallback;
            result.IsDevelopmentMode = packageEnvironment.IsDevelopmentMode;
            result.PackageSignatureKind = NormalizePackageSignatureKind(packageEnvironment.SignatureKind);
            result.PackageName = packageEnvironment.PackageName;
            result.PackageFullName = packageEnvironment.PackageFullName;
            result.PackageFamilyName = packageEnvironment.PackageFamilyName;
            result.PackagePublisherId = packageEnvironment.PackagePublisherId;
            result.PackageVersion = packageEnvironment.PackageVersion;
            result.PackageIdentityApi = packageEnvironment.PackageIdentityApi;
            result.PackageTypeAvailable = packageEnvironment.PackageTypeAvailable;
            result.PackageCurrentAvailable = packageEnvironment.PackageCurrentAvailable;
            result.PackageIdentityReadSucceeded = packageEnvironment.PackageIdentityReadSucceeded;
            result.PackageIdentityReadFailure = packageEnvironment.PackageIdentityReadFailure;
            result.PackageIdentityFallbackSource = packageEnvironment.PackageIdentityFallbackSource;
            result.IsFrameworkPackage = packageEnvironment.IsFramework;
            result.IsResourcePackage = packageEnvironment.IsResourcePackage;
            result.IsStoreManaged = result.IsPackaged &&
                                    !inferredPackagedFallback &&
                                    !result.IsDevelopmentMode &&
                                    IsStorePackageSignature(result.PackageSignatureKind);

            if (inferredPackagedFallback)
            {
                result.PackageSignatureKind = string.IsNullOrWhiteSpace(result.PackageSignatureKind) ? "UnknownPackagedFallback" : result.PackageSignatureKind;
                LogCheckStep(
                    "Packaged fallback detection inferred MSIX packaging from the startup environment.",
                    new Dictionary<string, object?>
                    {
                        ["processPath"] = packageEnvironment.ProcessPath,
                        ["baseDirectory"] = packageEnvironment.BaseDirectory,
                        ["packageFamilyName"] = result.PackageFamilyName,
                        ["signatureKind"] = result.PackageSignatureKind,
                        ["fallbackSource"] = result.PackageIdentityFallbackSource,
                        ["packageIdentityApi"] = result.PackageIdentityApi,
                        ["packageIdentityReadSucceeded"] = result.PackageIdentityReadSucceeded,
                        ["packageIdentityReadFailure"] = result.PackageIdentityReadFailure
                    });
            }
        }

        private static StoreUpdatePackagingKind ClassifyPackagedNonStoreInstall(StoreUpdateCheckResult result)
        {
            if (result.IsDevelopmentMode ||
                string.Equals(result.PackageSignatureKind, "Developer", StringComparison.OrdinalIgnoreCase))
            {
                return StoreUpdatePackagingKind.PackagedDeveloperOrTest;
            }

            if (IsUnknownPackageSignature(result.PackageSignatureKind))
            {
                return StoreUpdatePackagingKind.PackagedUnknownSource;
            }

            return StoreUpdatePackagingKind.PackagedSideloaded;
        }

        private static string BuildNonStorePackageStatusMessage(StoreUpdatePackagingKind packagingKind)
        {
            if (packagingKind == StoreUpdatePackagingKind.PackagedUnknownSource)
            {
                return "ScriptDesk could not determine how this package was installed. Microsoft Store automatic update checks are not available until the package source is confirmed.";
            }

            return packagingKind == StoreUpdatePackagingKind.PackagedDeveloperOrTest
                ? "This appears to be a developer or test package. Microsoft Store automatic update checks are not available for this build."
                : "This appears to be a sideloaded package. Microsoft Store automatic update checks are not available for this build.";
        }

        private static bool IsStorePackageSignature(string signatureKind)
        {
            return string.Equals(signatureKind, "Store", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnknownPackageSignature(string signatureKind)
        {
            return string.IsNullOrWhiteSpace(signatureKind) ||
                   string.Equals(signatureKind, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(signatureKind, "UnknownPackagedFallback", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePackageSignatureKind(object? signatureKind)
        {
            var text = signatureKind?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (string.Equals(text, "3", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".Store", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Store", StringComparison.OrdinalIgnoreCase))
            {
                return "Store";
            }

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".Developer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Developer", StringComparison.OrdinalIgnoreCase))
            {
                return "Developer";
            }

            if (string.Equals(text, "2", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".Enterprise", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Enterprise", StringComparison.OrdinalIgnoreCase))
            {
                return "Enterprise";
            }

            if (string.Equals(text, "4", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".System", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "System", StringComparison.OrdinalIgnoreCase))
            {
                return "System";
            }

            return text;
        }

        private static class NativePackageIdentity
        {
            private const int ErrorInsufficientBuffer = 122;
            private const int AppModelErrorNoPackage = 15700;

            public static string TryGetCurrentPackageFullName(out string status)
            {
                return TryReadCurrentPackageString(GetCurrentPackageFullName, out status);
            }

            public static string TryGetCurrentPackageFamilyName(out string status)
            {
                return TryReadCurrentPackageString(GetCurrentPackageFamilyName, out status);
            }

            public static string TryGetStagedPackageOrigin(string packageFullName, out string status)
            {
                try
                {
                    var error = GetStagedPackageOrigin(packageFullName, out var origin);
                    status = FormatNativeStatus(error);
                    return error == 0 ? origin.ToString() : string.Empty;
                }
                catch (EntryPointNotFoundException ex)
                {
                    status = BuildExceptionSummary(ex);
                    return string.Empty;
                }
                catch (DllNotFoundException ex)
                {
                    status = BuildExceptionSummary(ex);
                    return string.Empty;
                }
            }

            private static string TryReadCurrentPackageString(CurrentPackageStringReader reader, out string status)
            {
                var length = 0;
                var initialBuffer = new StringBuilder(0);
                var initialError = reader(ref length, initialBuffer);
                if (initialError == AppModelErrorNoPackage)
                {
                    status = FormatNativeStatus(initialError);
                    return string.Empty;
                }

                if (initialError != ErrorInsufficientBuffer || length <= 0)
                {
                    status = FormatNativeStatus(initialError);
                    return string.Empty;
                }

                var buffer = new StringBuilder(length);
                var error = reader(ref length, buffer);
                status = FormatNativeStatus(error);
                return error == 0 ? buffer.ToString().TrimEnd('\0') : string.Empty;
            }

            private static string FormatNativeStatus(int error)
            {
                return error switch
                {
                    0 => "Success",
                    ErrorInsufficientBuffer => "ERROR_INSUFFICIENT_BUFFER",
                    AppModelErrorNoPackage => "APPMODEL_ERROR_NO_PACKAGE",
                    _ => $"Win32Error={error}"
                };
            }

            private delegate int CurrentPackageStringReader(ref int packageNameLength, StringBuilder packageName);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder packageFullName);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder packageFamilyName);

            [DllImport("api-ms-win-appmodel-runtime-l1-1-1.dll", CharSet = CharSet.Unicode)]
            private static extern int GetStagedPackageOrigin(string packageFullName, out NativePackageOrigin origin);
        }

        private enum NativePackageOrigin
        {
            Unknown = 0,
            Unsigned = 1,
            Inbox = 2,
            Store = 3,
            DeveloperUnsigned = 4,
            DeveloperSigned = 5,
            LineOfBusiness = 6
        }

        private sealed class WindowsStorePackageEnvironmentProvider : IStorePackageEnvironmentProvider
        {
            public StorePackageEnvironmentInfo ReadPackageEnvironment()
            {
                var packageEnvironment = new StorePackageEnvironmentInfo
                {
                    ProcessPath = Environment.ProcessPath ?? string.Empty,
                    BaseDirectory = AppContext.BaseDirectory ?? string.Empty,
                    PackageTypeAvailable = true
                };

                try
                {
                    var currentPackage = Package.Current;
                    packageEnvironment.PackageCurrentAvailable = true;
                    packageEnvironment.HasPackageIdentity = true;
                    packageEnvironment.PackageIdentityReadSucceeded = true;
                    packageEnvironment.PackageIdentityApi = "Package.Current";
                    packageEnvironment.SignatureKind = NormalizePackageSignatureKind(currentPackage.SignatureKind);
                    packageEnvironment.IsDevelopmentMode = currentPackage.IsDevelopmentMode;
                    packageEnvironment.IsFramework = currentPackage.IsFramework;
                    packageEnvironment.IsResourcePackage = currentPackage.IsResourcePackage;
                    packageEnvironment.PackageName = currentPackage.Id.Name;
                    packageEnvironment.PackageFullName = currentPackage.Id.FullName;
                    packageEnvironment.PackageFamilyName = currentPackage.Id.FamilyName;
                    packageEnvironment.PackagePublisherId = currentPackage.Id.PublisherId;
                    packageEnvironment.PackageVersion = FormatPackageVersion(currentPackage.Id.Version);
                    return packageEnvironment;
                }
                catch (Exception ex)
                {
                    packageEnvironment.PackageCurrentAvailable = false;
                    packageEnvironment.PackageIdentityReadFailure = BuildExceptionSummary(ex);
                    LogCheckException("Direct Package.Current identity lookup failed.", ex);
                }

                TryReadNativePackageEnvironment(packageEnvironment);

                var packageFamilyName = Environment.GetEnvironmentVariable("APPX_PACKAGE_FAMILY_NAME") ?? string.Empty;
                packageEnvironment.IsInferredPackagedFallback = !packageEnvironment.HasPackageIdentity &&
                    (packageEnvironment.ProcessPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                     packageEnvironment.BaseDirectory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                     !string.IsNullOrWhiteSpace(packageFamilyName));

                if (!packageEnvironment.HasPackageIdentity && packageEnvironment.IsInferredPackagedFallback)
                {
                    packageEnvironment.PackageFamilyName = packageFamilyName;
                    packageEnvironment.PackageIdentityFallbackSource = string.IsNullOrWhiteSpace(packageEnvironment.PackageIdentityFallbackSource)
                        ? "WindowsAppsPathOrAppxEnvironment"
                        : packageEnvironment.PackageIdentityFallbackSource;
                    packageEnvironment.SignatureKind = "UnknownPackagedFallback";
                }

                return packageEnvironment;
            }

            private static void TryReadNativePackageEnvironment(StorePackageEnvironmentInfo packageEnvironment)
            {
                try
                {
                    var packageFullName = NativePackageIdentity.TryGetCurrentPackageFullName(out var fullNameStatus);
                    var packageFamilyName = NativePackageIdentity.TryGetCurrentPackageFamilyName(out var familyNameStatus);
                    packageEnvironment.NativePackageFullNameStatus = fullNameStatus;
                    packageEnvironment.NativePackageFamilyNameStatus = familyNameStatus;

                    if (string.IsNullOrWhiteSpace(packageFullName))
                    {
                        return;
                    }

                    packageEnvironment.HasPackageIdentity = true;
                    packageEnvironment.PackageIdentityReadSucceeded = true;
                    packageEnvironment.PackageIdentityApi = "NativeAppModel";
                    packageEnvironment.PackageFullName = packageFullName;
                    packageEnvironment.PackageFamilyName = packageFamilyName;
                    packageEnvironment.PackageIdentityFallbackSource = "NativeAppModel";

                    var origin = NativePackageIdentity.TryGetStagedPackageOrigin(packageFullName, out var originStatus);
                    packageEnvironment.NativePackageOriginStatus = originStatus;
                    packageEnvironment.PackageOrigin = origin;
                    packageEnvironment.SignatureKind = MapPackageOriginToSignatureKind(origin);

                    PopulatePackageIdentityFromFullName(packageEnvironment, packageFullName);
                }
                catch (Exception ex)
                {
                    packageEnvironment.PackageIdentityReadFailure = BuildExceptionSummary(ex);
                    LogCheckException("Native AppModel package identity lookup failed.", ex);
                }
            }

            private static void PopulatePackageIdentityFromFullName(StorePackageEnvironmentInfo packageEnvironment, string packageFullName)
            {
                var parts = packageFullName.Split('_');
                if (parts.Length < 5)
                {
                    return;
                }

                packageEnvironment.PackageName = parts[0];
                packageEnvironment.PackageVersion = parts[1];
                packageEnvironment.PackagePublisherId = parts[^1];
                if (string.IsNullOrWhiteSpace(packageEnvironment.PackageFamilyName))
                {
                    packageEnvironment.PackageFamilyName = $"{parts[0]}_{parts[^1]}";
                }
            }

            private static string MapPackageOriginToSignatureKind(string origin)
            {
                return origin switch
                {
                    "Store" => "Store",
                    "DeveloperUnsigned" or "DeveloperSigned" => "Developer",
                    "LineOfBusiness" or "Unsigned" => "Enterprise",
                    _ => string.Empty
                };
            }
        }

        private sealed class DirectStoreUpdateQuery : IStoreUpdateQuery
        {
            public async Task<StoreUpdateQueryResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StoreContext? storeContext = null;
                try
                {
                    storeContext = StoreContext.GetDefault();
                    LogCheckStep(
                        "Direct StoreContext availability evaluated.",
                        new Dictionary<string, object?>
                        {
                            ["storeContextAttempted"] = true,
                            ["storeContextAvailable"] = storeContext is not null
                        });

                    if (storeContext is null)
                    {
                        return StoreUpdateQueryResult.Unavailable(
                            StoreUpdateAvailabilityState.UpdateCheckUnavailable,
                            "Microsoft Store update status could not be queried at startup.",
                            "StoreContext.GetDefault() returned null.",
                            storeContextAttempted: true);
                    }

                    LogCheckStep("Calling direct GetAppAndOptionalStorePackageUpdatesAsync.");
                    var queryTask = QueryUpdatesAsync(storeContext);
                    var updates = await queryTask.WaitAsync(CheckTimeout, cancellationToken).ConfigureAwait(false);

                    return StoreUpdateQueryResult.UpdatesReturned(
                        storeContext,
                        updates,
                        updates.Select(ToPackageInfo).ToList());
                }
                catch (TimeoutException ex)
                {
                    LogCheckException("Direct Microsoft Store update query timed out.", ex);
                    return StoreUpdateQueryResult.Unavailable(
                        StoreUpdateAvailabilityState.UpdateCheckUnavailable,
                        "Microsoft Store update status could not be queried at startup.",
                        "Direct Store update query timed out.",
                        storeContext,
                        storeContextAttempted: true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogCheckException("Direct Microsoft Store update query failed.", ex);
                    return StoreUpdateQueryResult.Unavailable(
                        StoreUpdateAvailabilityState.UpdateCheckUnavailable,
                        "Microsoft Store update status could not be queried at startup.",
                        $"Direct Store update query failed: {BuildExceptionSummary(ex)}",
                        storeContext,
                        storeContextAttempted: true);
                }
            }

            private static async Task<IReadOnlyList<StorePackageUpdate>> QueryUpdatesAsync(StoreContext storeContext)
            {
                var updates = await storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
                return updates;
            }

            private static StoreUpdatePackageInfo ToPackageInfo(StorePackageUpdate update)
            {
                var packageId = update.Package?.Id;
                return new StoreUpdatePackageInfo(
                    packageId?.FamilyName ?? string.Empty,
                    update.Mandatory,
                    packageId is null ? string.Empty : FormatPackageVersion(packageId.Version));
            }
        }

        private static string FormatPackageVersion(PackageVersion version)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        private static string ReadPackageVersion(object currentPackage)
        {
            try
            {
                var idObject = currentPackage.GetType().GetProperty("Id")?.GetValue(currentPackage);
                var versionObject = idObject?.GetType().GetProperty("Version")?.GetValue(idObject);
                if (versionObject is null)
                {
                    return string.Empty;
                }

                var major = ReadUnsignedIntegerProperty(versionObject, "Major");
                var minor = ReadUnsignedIntegerProperty(versionObject, "Minor");
                var build = ReadUnsignedIntegerProperty(versionObject, "Build");
                var revision = ReadUnsignedIntegerProperty(versionObject, "Revision");
                return $"{major}.{minor}.{build}.{revision}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static uint ReadUnsignedIntegerProperty(object source, string propertyName)
        {
            try
            {
                var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
                return value switch
                {
                    byte byteValue => byteValue,
                    ushort ushortValue => ushortValue,
                    uint uintValue => uintValue,
                    int intValue when intValue >= 0 => (uint)intValue,
                    _ => 0
                };
            }
            catch
            {
                return 0;
            }
        }

        private static List<StoreUpdatePackageInfo> ExtractUpdates(object? updatesObject)
        {
            var updates = new List<StoreUpdatePackageInfo>();
            if (updatesObject is not IEnumerable enumerable)
            {
                return updates;
            }

            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                var packageFamilyName = ReadStringProperty(item, "PackageFamilyName");
                var mandatory = ReadBooleanProperty(item, "Mandatory") ?? ReadBooleanProperty(item, "IsMandatory") ?? false;
                updates.Add(new StoreUpdatePackageInfo(packageFamilyName, mandatory));
            }

            return updates;
        }

        private static List<StoreUpdateInstallStatusInfo> ExtractInstallStatuses(object? installResultObject)
        {
            var statuses = new List<StoreUpdateInstallStatusInfo>();
            var collection = ReadPropertyValue(installResultObject, "StorePackageUpdateStatuses");
            if (collection is not IEnumerable enumerable)
            {
                return statuses;
            }

            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                statuses.Add(new StoreUpdateInstallStatusInfo(
                    ReadStringProperty(item, "PackageFamilyName"),
                    ReadStringProperty(item, "PackageUpdateState"),
                    ReadStringProperty(item, "PackageDownloadProgress"),
                    ReadStringProperty(item, "Status"),
                    ReadStringProperty(item, "ErrorCode"),
                    ReadStringProperty(item, "StatusKind"),
                    ReadStringProperty(item, "StatusCode"),
                    ReadStringProperty(item, "StatusMessage")));
            }

            return statuses;
        }

        private static async Task<object?> AwaitWinRtOperationAsync(object? operation, TimeSpan timeout, string operationName, CancellationToken cancellationToken)
        {
            if (operation is null)
            {
                throw new InvalidOperationException($"{operationName} returned no operation object.");
            }

            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var statusValue = ReadPropertyValue(operation, "Status");
                var statusText = statusValue?.ToString() ?? string.Empty;
                if (!string.Equals(statusText, "Started", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(statusText, "Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        LogCheckStep($"{operationName} completed.", new Dictionary<string, object?> { ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds });
                        return operation.GetType().GetMethod("GetResults", BindingFlags.Public | BindingFlags.Instance)?.Invoke(operation, null);
                    }

                    var errorCode = ReadPropertyValue(operation, "ErrorCode")?.ToString() ?? string.Empty;
                    throw new InvalidOperationException($"{operationName} finished with status '{statusText}'. ErrorCode='{errorCode}'.");
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new TimeoutException($"{operationName} timed out after {timeout.TotalSeconds:0} seconds.");
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        private static void TryRegisterProgressCallback(object? operation, IProgress<StoreUpdateInstallProgressInfo>? progress)
        {
            if (operation is null || progress is null)
            {
                return;
            }

            try
            {
                var progressProperty = operation.GetType().GetProperty("Progress", BindingFlags.Public | BindingFlags.Instance);
                var delegateType = progressProperty?.PropertyType;
                if (progressProperty is null || delegateType is null)
                {
                    LogCheckStep("Store update progress callback was not available on the install operation.");
                    return;
                }

                var callback = BuildProgressDelegate(delegateType, progress);
                progressProperty.SetValue(operation, callback);
                LogCheckStep("Store update progress callback registered.");
            }
            catch (Exception ex)
            {
                LogCheckException("Store update progress callback registration failed.", ex);
            }
        }

        private static Delegate BuildProgressDelegate(Type delegateType, IProgress<StoreUpdateInstallProgressInfo> progress)
        {
            var invokeMethod = delegateType.GetMethod("Invoke") ?? throw new InvalidOperationException("Progress delegate type did not expose an Invoke method.");
            var parameters = invokeMethod.GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                .ToArray();

            var reportMethod = typeof(StoreUpdateService).GetMethod(nameof(ReportInstallProgress), BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Progress reporting method was not found.");

            var body = Expression.Call(
                reportMethod,
                Expression.Constant(progress),
                parameters.Length > 0 ? Expression.Convert(parameters[0], typeof(object)) : Expression.Constant(null, typeof(object)),
                parameters.Length > 1 ? Expression.Convert(parameters[1], typeof(object)) : Expression.Constant(null, typeof(object)));

            return Expression.Lambda(delegateType, body, parameters).Compile();
        }

        private static void ReportInstallProgress(IProgress<StoreUpdateInstallProgressInfo> progress, object? operation, object? progressInfo)
        {
            var info = new StoreUpdateInstallProgressInfo(
                ReadStringProperty(progressInfo, "PackageFamilyName"),
                ReadStringProperty(progressInfo, "PackageUpdateState"),
                ReadStringProperty(progressInfo, "PackageDownloadProgress"),
                ReadStringProperty(progressInfo, "Status"),
                ReadStringProperty(progressInfo, "ErrorCode"),
                ReadStringProperty(operation, "Status"));

            progress.Report(info);
            LogCheckStep(
                "Store update install progress reported.",
                new Dictionary<string, object?>
                {
                    ["packageFamilyName"] = info.PackageFamilyName,
                    ["packageUpdateState"] = info.PackageUpdateState,
                    ["packageDownloadProgress"] = info.PackageDownloadProgress,
                    ["status"] = info.Status,
                    ["errorCode"] = info.ErrorCode,
                    ["operationStatus"] = info.OperationStatus
                });
        }

        private static string ReadStringProperty(object? source, string propertyName)
        {
            return ReadPropertyValue(source, propertyName)?.ToString() ?? string.Empty;
        }

        private static string ReadStringProperty(object? source, string parentPropertyName, string nestedPropertyName)
        {
            var nested = ReadPropertyValue(ReadPropertyValue(source, parentPropertyName), nestedPropertyName);
            return nested?.ToString() ?? string.Empty;
        }

        private static bool? ReadBooleanProperty(object? source, string propertyName)
        {
            try
            {
                var value = ReadPropertyValue(source, propertyName);
                return value switch
                {
                    bool boolValue => boolValue,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static object? ReadPropertyValue(object? source, string propertyName)
        {
            try
            {
                return source?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildExceptionSummary(Exception ex)
        {
            var hresult = ex.HResult.ToString("X8");
            var builder = new StringBuilder();
            builder.Append(ex.GetType().Name)
                .Append(": ")
                .Append(ex.Message)
                .Append(" (HRESULT=0x")
                .Append(hresult)
                .Append(')');
            return builder.ToString();
        }

        private static void LogCheckStep(string message, IReadOnlyDictionary<string, object?>? additionalProperties = null)
        {
            AppLogger.Info("StoreUpdate", message);
            DeveloperDiagnostics.LogInfo("StoreUpdate", message, additionalProperties);
        }

        private static void LogCheckException(string message, Exception ex)
        {
            AppLogger.Error("StoreUpdate", $"{message} {BuildExceptionSummary(ex)}", ex);
            DeveloperDiagnostics.LogException(
                "StoreUpdate",
                ex,
                message,
                new Dictionary<string, object?>
                {
                    ["hresult"] = $"0x{ex.HResult:X8}",
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    internal interface IStorePackageEnvironmentProvider
    {
        StorePackageEnvironmentInfo ReadPackageEnvironment();
    }

    internal interface IStoreUpdateQuery
    {
        Task<StoreUpdateQueryResult> CheckForUpdatesAsync(CancellationToken cancellationToken);
    }

    internal sealed class StorePackageEnvironmentInfo
    {
        public bool HasPackageIdentity { get; set; }

        public bool IsInferredPackagedFallback { get; set; }

        public bool IsDevelopmentMode { get; set; }

        public bool IsFramework { get; set; }

        public bool IsResourcePackage { get; set; }

        public bool PackageTypeAvailable { get; set; }

        public bool PackageCurrentAvailable { get; set; }

        public bool PackageIdentityReadSucceeded { get; set; }

        public string SignatureKind { get; set; } = string.Empty;

        public string PackageName { get; set; } = string.Empty;

        public string PackageFullName { get; set; } = string.Empty;

        public string PackageFamilyName { get; set; } = string.Empty;

        public string PackagePublisherId { get; set; } = string.Empty;

        public string PackageVersion { get; set; } = string.Empty;

        public string PackageIdentityApi { get; set; } = string.Empty;

        public string PackageIdentityReadFailure { get; set; } = string.Empty;

        public string PackageIdentityFallbackSource { get; set; } = string.Empty;

        public string PackageOrigin { get; set; } = string.Empty;

        public string NativePackageFullNameStatus { get; set; } = string.Empty;

        public string NativePackageFamilyNameStatus { get; set; } = string.Empty;

        public string NativePackageOriginStatus { get; set; } = string.Empty;

        public string ProcessPath { get; set; } = string.Empty;

        public string BaseDirectory { get; set; } = string.Empty;
    }

    internal sealed class StoreUpdateQueryResult
    {
        private StoreUpdateQueryResult()
        {
        }

        public StoreUpdateAvailabilityState AvailabilityState { get; private set; }

        public string StatusMessage { get; private set; } = string.Empty;

        public string LogMessage { get; private set; } = string.Empty;

        public bool StoreContextAvailable { get; private set; }

        public bool PerPackageUpdateListReturned { get; private set; }

        public bool StoreContextAttempted { get; private set; }

        public object? RawStoreContext { get; private set; }

        public object? RawUpdatesCollection { get; private set; }

        public List<StoreUpdatePackageInfo> Updates { get; private set; } = new();

        public static StoreUpdateQueryResult UpdatesReturned(object storeContext, object updatesCollection, List<StoreUpdatePackageInfo> updates)
        {
            return new StoreUpdateQueryResult
            {
                AvailabilityState = StoreUpdateAvailabilityState.ConfirmedUpdateAvailable,
                StatusMessage = "Microsoft Store update query completed.",
                LogMessage = "Store update query returned a per-package update list.",
                StoreContextAvailable = true,
                PerPackageUpdateListReturned = true,
                StoreContextAttempted = true,
                RawStoreContext = storeContext,
                RawUpdatesCollection = updatesCollection,
                Updates = updates ?? new List<StoreUpdatePackageInfo>()
            };
        }

        public static StoreUpdateQueryResult Unavailable(
            StoreUpdateAvailabilityState availabilityState,
            string statusMessage,
            string logMessage,
            object? storeContext = null,
            object? updatesCollection = null,
            bool storeContextAttempted = false)
        {
            return new StoreUpdateQueryResult
            {
                AvailabilityState = availabilityState,
                StatusMessage = statusMessage ?? string.Empty,
                LogMessage = logMessage ?? string.Empty,
                StoreContextAvailable = storeContext is not null,
                PerPackageUpdateListReturned = false,
                StoreContextAttempted = storeContextAttempted,
                RawStoreContext = storeContext,
                RawUpdatesCollection = updatesCollection
            };
        }
    }

    public sealed class StoreUpdateCheckResult
    {
        public StoreUpdatePackagingKind PackagingKind { get; set; }

        public StoreUpdateAvailabilityState AvailabilityState { get; set; }

        public bool IsPackaged { get; set; }

        public bool IsStoreManaged { get; set; }

        public bool IsDevelopmentMode { get; set; }

        public bool IsFrameworkPackage { get; set; }

        public bool IsResourcePackage { get; set; }

        public bool PackageTypeAvailable { get; set; }

        public bool PackageCurrentAvailable { get; set; }

        public bool PackageIdentityReadSucceeded { get; set; }

        public string PackageIdentityApi { get; set; } = string.Empty;

        public string PackageIdentityReadFailure { get; set; } = string.Empty;

        public string PackageIdentityFallbackSource { get; set; } = string.Empty;

        public string PackageName { get; set; } = string.Empty;

        public string PackageFamilyName { get; set; } = string.Empty;

        public string PackageFullName { get; set; } = string.Empty;

        public string PackagePublisherId { get; set; } = string.Empty;

        public string PackageVersion { get; set; } = string.Empty;

        public string PackageSignatureKind { get; set; } = string.Empty;

        public bool StoreContextAvailable { get; set; }

        public bool StoreContextAttempted { get; set; }

        public bool StoreUpdateCheckAvailable { get; set; }

        public bool PerPackageUpdateListReturned { get; set; }

        public int UpdateCount { get; set; }

        public bool HasMandatoryUpdate { get; set; }

        public bool ShouldShowManualInstructions { get; set; }

        public string ManualInstructions { get; set; } = string.Empty;

        public string StatusMessage { get; set; } = string.Empty;

        public string ExceptionSummary { get; set; } = string.Empty;

        public List<StoreUpdatePackageInfo> Updates { get; set; } = new();

        public object? RawStoreContext { get; set; }

        public object? RawUpdatesCollection { get; set; }

        public bool HasConfirmedInstallableUpdate =>
            AvailabilityState == StoreUpdateAvailabilityState.ConfirmedUpdateAvailable &&
            UpdateCount > 0 &&
            RawStoreContext is not null &&
            RawUpdatesCollection is not null;

        public bool ShouldShowAutomaticNotification => HasMandatoryUpdate || HasConfirmedInstallableUpdate;
    }

    public enum StoreUpdatePackagingKind
    {
        None = 0,
        UnpackagedLocalBuild = 1,
        PackagedSideloadedOrTest = 2,
        StoreInstalledManaged = 3,
        PackagedDeveloperOrTest = 4,
        PackagedSideloaded = 5,
        PackagedUnknownSource = 6,
    }

    public enum StoreUpdateAvailabilityState
    {
        None = 0,
        ConfirmedUpdateAvailable = 1,
        UpdateCheckUnavailable = 2,
        ManualCheckRequired = 3,
        NoUpdateAvailable = 4,
    }

    public sealed class StoreUpdatePackageInfo
    {
        public StoreUpdatePackageInfo(string packageFamilyName, bool isMandatory, string version = "")
        {
            PackageFamilyName = packageFamilyName ?? string.Empty;
            IsMandatory = isMandatory;
            Version = version ?? string.Empty;
        }

        public string PackageFamilyName { get; }

        public bool IsMandatory { get; }

        public string Version { get; }
    }

    public sealed class StoreUpdateInstallResult
    {
        public bool RequestStarted { get; set; }

        public string OverallState { get; set; } = string.Empty;

        public string ExceptionSummary { get; set; } = string.Empty;

        public List<StoreUpdateInstallStatusInfo> PackageStatuses { get; set; } = new();
    }

    public sealed class StoreUpdateInstallStatusInfo
    {
        public StoreUpdateInstallStatusInfo(
            string packageFamilyName,
            string packageUpdateState,
            string packageDownloadProgress,
            string status,
            string errorCode,
            string statusKind,
            string statusCode,
            string statusMessage)
        {
            PackageFamilyName = packageFamilyName ?? string.Empty;
            PackageUpdateState = packageUpdateState ?? string.Empty;
            PackageDownloadProgress = packageDownloadProgress ?? string.Empty;
            Status = status ?? string.Empty;
            ErrorCode = errorCode ?? string.Empty;
            StatusKind = statusKind ?? string.Empty;
            StatusCode = statusCode ?? string.Empty;
            StatusMessage = statusMessage ?? string.Empty;
        }

        public string PackageFamilyName { get; }

        public string PackageUpdateState { get; }

        public string PackageDownloadProgress { get; }

        public string Status { get; }

        public string ErrorCode { get; }

        public string StatusKind { get; }

        public string StatusCode { get; }

        public string StatusMessage { get; }
    }

    public sealed class StoreUpdateInstallProgressInfo
    {
        public StoreUpdateInstallProgressInfo(
            string packageFamilyName,
            string packageUpdateState,
            string packageDownloadProgress,
            string status,
            string errorCode,
            string operationStatus)
        {
            PackageFamilyName = packageFamilyName ?? string.Empty;
            PackageUpdateState = packageUpdateState ?? string.Empty;
            PackageDownloadProgress = packageDownloadProgress ?? string.Empty;
            Status = status ?? string.Empty;
            ErrorCode = errorCode ?? string.Empty;
            OperationStatus = operationStatus ?? string.Empty;
        }

        public string PackageFamilyName { get; }

        public string PackageUpdateState { get; }

        public string PackageDownloadProgress { get; }

        public string Status { get; }

        public string ErrorCode { get; }

        public string OperationStatus { get; }
    }
}
