using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// Configures background PowerShell helper processes so metadata, diagnostics, and completion
    /// workers do not use the user's protected Documents\PowerShell profile/cache location.
    ///
    /// This intentionally does not apply to the visible/live console. The visible console should
    /// keep behaving like the user's real PowerShell session. Background editor helpers should be
    /// deterministic, no-profile, non-interactive, and store writable PowerShell state under the
    /// app's LocalAppData folder so Windows Controlled Folder Access does not block pwsh.exe while
    /// metadata refresh is running.
    /// </summary>
    internal sealed class PowerShellBackgroundProcessEnvironmentInfo
    {
        public PowerShellBackgroundProcessEnvironmentInfo(
            string purpose,
            string localAppData,
            string backgroundRoot,
            string homePath,
            string userProfilePath,
            string moduleRoot,
            string tempPath,
            string tmpPath,
            string psModuleAnalysisCachePath,
            string psModulePath,
            IReadOnlyList<string> modulePathEntries,
            bool modulePathContainsUserDocumentsPowerShell,
            string powerShellTelemetryOptOut,
            string powerShellCliTelemetryOptOut,
            string powerShellUpdateCheck)
        {
            Purpose = purpose;
            LocalAppData = localAppData;
            BackgroundRoot = backgroundRoot;
            HomePath = homePath;
            UserProfilePath = userProfilePath;
            ModuleRoot = moduleRoot;
            TempPath = tempPath;
            TmpPath = tmpPath;
            PsModuleAnalysisCachePath = psModuleAnalysisCachePath;
            PsModulePath = psModulePath;
            ModulePathEntries = modulePathEntries;
            ModulePathContainsUserDocumentsPowerShell = modulePathContainsUserDocumentsPowerShell;
            PowerShellTelemetryOptOut = powerShellTelemetryOptOut;
            PowerShellCliTelemetryOptOut = powerShellCliTelemetryOptOut;
            PowerShellUpdateCheck = powerShellUpdateCheck;
        }

        public string Purpose { get; }
        public string LocalAppData { get; }
        public string BackgroundRoot { get; }
        public string HomePath { get; }
        public string UserProfilePath { get; }
        public string ModuleRoot { get; }
        public string TempPath { get; }
        public string TmpPath { get; }
        public string PsModuleAnalysisCachePath { get; }
        public string PsModulePath { get; }
        public IReadOnlyList<string> ModulePathEntries { get; }
        public bool ModulePathContainsUserDocumentsPowerShell { get; }
        public string PowerShellTelemetryOptOut { get; }
        public string PowerShellCliTelemetryOptOut { get; }
        public string PowerShellUpdateCheck { get; }
    }

    internal static class PowerShellBackgroundProcessEnvironment
    {
        private const string AppFolderName = ApplicationBranding.InternalName;
        private const string BackgroundFolderName = "BackgroundPowerShell";

        public static bool Apply(ProcessStartInfo startInfo, string purpose, string? runtimePath = null)
        {
            if (startInfo is null)
            {
                return false;
            }

            if (!TryBuildEnvironmentInfo(purpose, runtimePath, createDirectories: true, out var environmentInfo, out var failureReason))
            {
                AppLogger.Warning(
                    "PowerShellBackgroundEnvironment",
                    $"Failed to configure isolated PowerShell environment for purpose '{purpose}': {failureReason}");
                return false;
            }

            startInfo.Environment["HOME"] = environmentInfo.HomePath;
            startInfo.Environment["USERPROFILE"] = environmentInfo.UserProfilePath;
            startInfo.Environment["PSModuleAnalysisCachePath"] = environmentInfo.PsModuleAnalysisCachePath;
            startInfo.Environment["POWERSHELL_UPDATECHECK"] = environmentInfo.PowerShellUpdateCheck;
            startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = environmentInfo.PowerShellTelemetryOptOut;
            startInfo.Environment["POWERSHELL_CLI_TELEMETRY_OPTOUT"] = environmentInfo.PowerShellCliTelemetryOptOut;
            startInfo.Environment["TEMP"] = environmentInfo.TempPath;
            startInfo.Environment["TMP"] = environmentInfo.TmpPath;

            if (!string.IsNullOrWhiteSpace(environmentInfo.PsModulePath))
            {
                startInfo.Environment["PSModulePath"] = environmentInfo.PsModulePath;
            }

            AppLogger.Debug(
                "PowerShellBackgroundEnvironment",
                $"Configured background PowerShell environment. Purpose='{environmentInfo.Purpose}', Home='{environmentInfo.HomePath}', UserProfile='{environmentInfo.UserProfilePath}', Temp='{environmentInfo.TempPath}', Runtime='{runtimePath ?? string.Empty}'.");
            return true;
        }

        public static bool TryBuildEnvironmentInfo(
            string purpose,
            string? runtimePath,
            bool createDirectories,
            out PowerShellBackgroundProcessEnvironmentInfo environmentInfo,
            out string failureReason)
        {
            environmentInfo = new PowerShellBackgroundProcessEnvironmentInfo(
                purpose: string.Empty,
                localAppData: string.Empty,
                backgroundRoot: string.Empty,
                homePath: string.Empty,
                userProfilePath: string.Empty,
                moduleRoot: string.Empty,
                tempPath: string.Empty,
                tmpPath: string.Empty,
                psModuleAnalysisCachePath: string.Empty,
                psModulePath: string.Empty,
                modulePathEntries: Array.Empty<string>(),
                modulePathContainsUserDocumentsPowerShell: false,
                powerShellTelemetryOptOut: string.Empty,
                powerShellCliTelemetryOptOut: string.Empty,
                powerShellUpdateCheck: string.Empty);
            failureReason = string.Empty;

            try
            {
                var normalizedPurpose = SanitizePathSegment(purpose);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    failureReason = "LocalAppData could not be resolved.";
                    return false;
                }

                var backgroundRoot = Path.Combine(localAppData, AppFolderName, BackgroundFolderName);
                var homeRoot = Path.Combine(backgroundRoot, "Home");
                var userProfileRoot = Path.Combine(backgroundRoot, "UserProfile");
                var moduleRoot = Path.Combine(backgroundRoot, "Modules");
                var cacheRoot = Path.Combine(backgroundRoot, "Cache", normalizedPurpose);
                var tempRoot = Path.Combine(backgroundRoot, "Temp", normalizedPurpose);
                var moduleAnalysisCachePath = Path.Combine(cacheRoot, "ModuleAnalysisCache");
                var safeModulePath = BuildSafeModulePath(runtimePath, moduleRoot);

                if (createDirectories)
                {
                    Directory.CreateDirectory(homeRoot);
                    Directory.CreateDirectory(userProfileRoot);
                    Directory.CreateDirectory(moduleRoot);
                    Directory.CreateDirectory(cacheRoot);
                    Directory.CreateDirectory(tempRoot);
                }

                environmentInfo = new PowerShellBackgroundProcessEnvironmentInfo(
                    purpose: normalizedPurpose,
                    localAppData: localAppData,
                    backgroundRoot: backgroundRoot,
                    homePath: homeRoot,
                    userProfilePath: userProfileRoot,
                    moduleRoot: moduleRoot,
                    tempPath: tempRoot,
                    tmpPath: tempRoot,
                    psModuleAnalysisCachePath: moduleAnalysisCachePath,
                    psModulePath: safeModulePath,
                    modulePathEntries: string.IsNullOrWhiteSpace(safeModulePath)
                        ? Array.Empty<string>()
                        : safeModulePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    modulePathContainsUserDocumentsPowerShell: ModulePathContainsUserDocumentsPowerShell(safeModulePath),
                    powerShellTelemetryOptOut: "1",
                    powerShellCliTelemetryOptOut: "1",
                    powerShellUpdateCheck: "Off");
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        public static bool ModulePathContainsUserDocumentsPowerShell(string? modulePath)
        {
            if (string.IsNullOrWhiteSpace(modulePath))
            {
                return false;
            }

            foreach (var entry in modulePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsUserDocumentsPowerShellPath(entry))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildSafeModulePath(string? runtimePath, string appLocalModuleRoot)
        {
            var entries = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddModulePath(entries, seen, appLocalModuleRoot);

            if (!string.IsNullOrWhiteSpace(runtimePath))
            {
                try
                {
                    var runtimeDirectory = Path.GetDirectoryName(runtimePath);
                    if (!string.IsNullOrWhiteSpace(runtimeDirectory))
                    {
                        AddModulePath(entries, seen, Path.Combine(runtimeDirectory, "Modules"));
                    }
                }
                catch
                {
                    // Best effort only.
                }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                AddModulePath(entries, seen, Path.Combine(programFiles, "PowerShell", "Modules"));
                AddModulePath(entries, seen, Path.Combine(programFiles, "WindowsPowerShell", "Modules"));
            }

            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                AddModulePath(entries, seen, Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "Modules"));
            }

            foreach (var originalEntry in GetOriginalModulePathEntries())
            {
                if (IsUserDocumentsPowerShellPath(originalEntry))
                {
                    AppLogger.Debug("PowerShellBackgroundEnvironment", $"Removed protected user Documents PowerShell module path from background helper PSModulePath: '{originalEntry}'.");
                    continue;
                }

                AddModulePath(entries, seen, originalEntry);
            }

            return string.Join(Path.PathSeparator.ToString(), entries);
        }

        private static IEnumerable<string> GetOriginalModulePathEntries()
        {
            var modulePath = Environment.GetEnvironmentVariable("PSModulePath");
            if (string.IsNullOrWhiteSpace(modulePath))
            {
                return Enumerable.Empty<string>();
            }

            return modulePath
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !string.IsNullOrWhiteSpace(entry));
        }

        private static void AddModulePath(ICollection<string> entries, ISet<string> seen, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            if (seen.Add(normalizedPath))
            {
                entries.Add(normalizedPath);
            }
        }

        private static bool IsUserDocumentsPowerShellPath(string path)
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentsPath))
            {
                return false;
            }

            var documentsPowerShellPath = NormalizePath(Path.Combine(documentsPath, "PowerShell"));
            var candidatePath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(documentsPowerShellPath) || string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            return IsSameOrChildPath(candidatePath, documentsPowerShellPath);
        }

        private static bool IsSameOrChildPath(string candidatePath, string parentPath)
        {
            var normalizedParent = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCandidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        private static string SanitizePathSegment(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "PowerShell" : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(text) ? "PowerShell" : text;
        }
    }
}
