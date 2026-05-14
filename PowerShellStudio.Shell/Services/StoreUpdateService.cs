using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;

namespace PowerShellStudio.Shell.Services
{
    public sealed class StoreUpdateService
    {
        private const string PackageTypeName = "Windows.ApplicationModel.Package, Windows, ContentType=WindowsRuntime";
        private const string StoreContextTypeName = "Windows.Services.Store.StoreContext, Windows, ContentType=WindowsRuntime";
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

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
                PopulatePackageInfo(result);
                LogCheckStep(
                    "Store packaging state resolved.",
                    new Dictionary<string, object?>
                    {
                        ["isPackaged"] = result.IsPackaged,
                        ["isStoreManaged"] = result.IsStoreManaged,
                        ["packageFamilyName"] = result.PackageFamilyName,
                        ["packageFullName"] = result.PackageFullName,
                        ["packageVersion"] = result.PackageVersion,
                        ["signatureKind"] = result.PackageSignatureKind
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
                    result.PackagingKind = StoreUpdatePackagingKind.PackagedSideloadedOrTest;
                    result.AvailabilityState = StoreUpdateAvailabilityState.ManualCheckRequired;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = "This appears to be a sideloaded or test package. Microsoft Store update checks may not be available for this build.";
                    LogCheckStep(
                        "The app appears packaged, but no Store-managed update path was confirmed for this build.",
                        new Dictionary<string, object?>
                        {
                            ["signatureKind"] = result.PackageSignatureKind,
                            ["packageFamilyName"] = result.PackageFamilyName
                        });
                    return result;
                }

                result.PackagingKind = StoreUpdatePackagingKind.StoreInstalledManaged;

                var storeContextType = Type.GetType(StoreContextTypeName, throwOnError: false);
                if (storeContextType is null)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = "Store update APIs were not available at runtime.";
                    LogCheckStep("StoreContext type was not available.");
                    return result;
                }

                var getDefaultMethod = storeContextType.GetMethod("GetDefault", BindingFlags.Public | BindingFlags.Static);
                if (getDefaultMethod is null)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = "StoreContext.GetDefault was not available at runtime.";
                    LogCheckStep("StoreContext.GetDefault was not available.");
                    return result;
                }

                var storeContext = getDefaultMethod.Invoke(null, null);
                result.StoreContextAvailable = storeContext is not null;
                LogCheckStep(
                    "StoreContext availability evaluated.",
                    new Dictionary<string, object?> { ["storeContextAvailable"] = result.StoreContextAvailable });

                if (storeContext is null)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = "StoreContext was unavailable for this packaged build.";
                    return result;
                }

                var checkMethod = storeContextType.GetMethod("GetAppAndOptionalStorePackageUpdatesAsync", BindingFlags.Public | BindingFlags.Instance);
                if (checkMethod is null)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.UpdateCheckUnavailable;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = "GetAppAndOptionalStorePackageUpdatesAsync was unavailable at runtime.";
                    LogCheckStep("GetAppAndOptionalStorePackageUpdatesAsync was unavailable.");
                    return result;
                }

                LogCheckStep("Calling GetAppAndOptionalStorePackageUpdatesAsync.");
                var operation = checkMethod.Invoke(storeContext, null);
                var updatesObject = await AwaitWinRtOperationAsync(operation, CheckTimeout, "GetAppAndOptionalStorePackageUpdatesAsync", cancellationToken).ConfigureAwait(false);

                result.RawStoreContext = storeContext;
                result.RawUpdatesCollection = updatesObject;
                result.PerPackageUpdateListReturned = updatesObject is IEnumerable;

                if (!result.PerPackageUpdateListReturned)
                {
                    result.AvailabilityState = StoreUpdateAvailabilityState.ManualCheckRequired;
                    result.StoreUpdateCheckAvailable = false;
                    result.ShouldShowManualInstructions = true;
                    result.StatusMessage = "No per-package Microsoft Store update list was returned for this build. Use Microsoft Store -> Library -> Get updates.";
                    LogCheckStep(
                        "Store update query completed without a per-package update list. Treating the result as manual-check-required.",
                        new Dictionary<string, object?>
                        {
                            ["packageFamilyName"] = result.PackageFamilyName,
                            ["signatureKind"] = result.PackageSignatureKind
                        });
                    return result;
                }

                result.StoreUpdateCheckAvailable = true;

                var updates = ExtractUpdates(updatesObject);
                result.Updates = updates;
                result.UpdateCount = updates.Count;
                result.HasMandatoryUpdate = updates.Any(update => update.IsMandatory);

                LogCheckStep(
                    "Store update query completed.",
                    new Dictionary<string, object?>
                    {
                        ["updateCount"] = result.UpdateCount,
                        ["packageFamilyNames"] = string.Join(", ", updates.Select(update => update.PackageFamilyName)),
                        ["mandatoryUpdatePresent"] = result.HasMandatoryUpdate
                    });

                foreach (var update in updates)
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
                        : StoreUpdatePackagingKind.PackagedSideloadedOrTest;
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
                        ["storeContextAvailable"] = result.StoreContextAvailable,
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
                if (checkResult.RawStoreContext is null || checkResult.RawUpdatesCollection is null)
                {
                    result.ExceptionSummary = "Store update install could not start because no raw Store update context was available.";
                    LogCheckStep(result.ExceptionSummary);
                    return result;
                }

                var storeContextType = checkResult.RawStoreContext.GetType();
                var installMethod = storeContextType.GetMethod("RequestDownloadAndInstallStorePackageUpdatesAsync", BindingFlags.Public | BindingFlags.Instance);
                if (installMethod is null)
                {
                    result.ExceptionSummary = "RequestDownloadAndInstallStorePackageUpdatesAsync was unavailable at runtime.";
                    LogCheckStep(result.ExceptionSummary);
                    return result;
                }

                result.RequestStarted = true;
                LogCheckStep(
                    "Calling RequestDownloadAndInstallStorePackageUpdatesAsync.",
                    new Dictionary<string, object?>
                    {
                        ["updateCount"] = checkResult.UpdateCount,
                        ["packageFamilyNames"] = string.Join(", ", checkResult.Updates.Select(update => update.PackageFamilyName))
                    });

                var operation = installMethod.Invoke(checkResult.RawStoreContext, new[] { checkResult.RawUpdatesCollection });
                TryRegisterProgressCallback(operation, progress);
                var installResultObject = await AwaitWinRtOperationAsync(operation, InstallTimeout, "RequestDownloadAndInstallStorePackageUpdatesAsync", cancellationToken).ConfigureAwait(false);
                result.OverallState = ReadStringProperty(installResultObject, "OverallState");
                result.PackageStatuses = ExtractInstallStatuses(installResultObject);

                LogCheckStep(
                    "Store update install request completed.",
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
                            ["downloadProgress"] = status.PackageDownloadProgress,
                            ["errorCode"] = status.ErrorCode,
                            ["statusKind"] = status.StatusKind,
                            ["statusCode"] = status.StatusCode,
                            ["statusMessage"] = status.StatusMessage
                        });
                }

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

        private static void PopulatePackageInfo(StoreUpdateCheckResult result)
        {
            var packageType = Type.GetType(PackageTypeName, throwOnError: false);
            if (packageType is not null)
            {
                try
                {
                    var currentPackage = packageType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (currentPackage is not null)
                    {
                        result.IsPackaged = true;
                        result.PackageSignatureKind = ReadStringProperty(currentPackage, "SignatureKind");
                        result.IsStoreManaged = string.Equals(result.PackageSignatureKind, "Store", StringComparison.OrdinalIgnoreCase);
                        result.PackageFullName = ReadStringProperty(currentPackage, "Id", "FullName");
                        result.PackageFamilyName = ReadStringProperty(currentPackage, "Id", "FamilyName");
                        result.PackageVersion = ReadPackageVersion(currentPackage);
                    }
                }
                catch (TargetInvocationException ex)
                {
                    LogCheckException("Package identity lookup failed.", ex.InnerException ?? ex);
                }
                catch (Exception ex)
                {
                    LogCheckException("Package identity lookup failed.", ex);
                }
            }

            var processPath = Environment.ProcessPath ?? string.Empty;
            var baseDirectory = AppContext.BaseDirectory ?? string.Empty;
            var packageFamilyName = Environment.GetEnvironmentVariable("APPX_PACKAGE_FAMILY_NAME") ?? string.Empty;
            var inferredPackaged = processPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                                   baseDirectory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                                   !string.IsNullOrWhiteSpace(packageFamilyName);

            if (!result.IsPackaged && inferredPackaged)
            {
                result.IsPackaged = true;
                result.IsStoreManaged = false;
                result.PackageFamilyName = string.IsNullOrWhiteSpace(result.PackageFamilyName) ? packageFamilyName : result.PackageFamilyName;
                result.PackageSignatureKind = string.IsNullOrWhiteSpace(result.PackageSignatureKind) ? "UnknownPackagedFallback" : result.PackageSignatureKind;
                LogCheckStep(
                    "Packaged fallback detection inferred MSIX packaging from the startup environment.",
                    new Dictionary<string, object?>
                    {
                        ["processPath"] = processPath,
                        ["baseDirectory"] = baseDirectory,
                        ["packageFamilyName"] = result.PackageFamilyName,
                        ["signatureKind"] = result.PackageSignatureKind
                    });
            }
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

    public sealed class StoreUpdateCheckResult
    {
        public StoreUpdatePackagingKind PackagingKind { get; set; }

        public StoreUpdateAvailabilityState AvailabilityState { get; set; }

        public bool IsPackaged { get; set; }

        public bool IsStoreManaged { get; set; }

        public string PackageFamilyName { get; set; } = string.Empty;

        public string PackageFullName { get; set; } = string.Empty;

        public string PackageVersion { get; set; } = string.Empty;

        public string PackageSignatureKind { get; set; } = string.Empty;

        public bool StoreContextAvailable { get; set; }

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
        public StoreUpdatePackageInfo(string packageFamilyName, bool isMandatory)
        {
            PackageFamilyName = packageFamilyName ?? string.Empty;
            IsMandatory = isMandatory;
        }

        public string PackageFamilyName { get; }

        public bool IsMandatory { get; }
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
