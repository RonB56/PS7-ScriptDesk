using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PowerShellStudio.Application.Diagnostics;

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
    internal static class PowerShellBackgroundProcessEnvironment
    {
        private const string AppFolderName = "PowerShellStudio";
        private const string BackgroundFolderName = "BackgroundPowerShell";

        public static void Apply(ProcessStartInfo startInfo, string purpose, string? runtimePath = null)
        {
            if (startInfo is null)
            {
                return;
            }

            try
            {
                var normalizedPurpose = SanitizePathSegment(purpose);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    return;
                }

                var backgroundRoot = Path.Combine(localAppData, AppFolderName, BackgroundFolderName);
                var purposeRoot = Path.Combine(backgroundRoot, normalizedPurpose);
                var homeRoot = Path.Combine(purposeRoot, "Home");
                var documentsPowerShellRoot = Path.Combine(homeRoot, "Documents", "PowerShell");
                var moduleRoot = Path.Combine(documentsPowerShellRoot, "Modules");
                var cacheRoot = Path.Combine(purposeRoot, "Cache");
                var tempRoot = Path.Combine(purposeRoot, "Temp");

                Directory.CreateDirectory(homeRoot);
                Directory.CreateDirectory(documentsPowerShellRoot);
                Directory.CreateDirectory(moduleRoot);
                Directory.CreateDirectory(cacheRoot);
                Directory.CreateDirectory(tempRoot);

                startInfo.Environment["HOME"] = homeRoot;
                startInfo.Environment["USERPROFILE"] = homeRoot;
                startInfo.Environment["PSModuleAnalysisCachePath"] = Path.Combine(cacheRoot, "ModuleAnalysisCache");
                startInfo.Environment["POWERSHELL_UPDATECHECK"] = "Off";
                startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
                startInfo.Environment["POWERSHELL_CLI_TELEMETRY_OPTOUT"] = "1";
                startInfo.Environment["TEMP"] = tempRoot;
                startInfo.Environment["TMP"] = tempRoot;

                var safeModulePath = BuildSafeModulePath(runtimePath, moduleRoot);
                if (!string.IsNullOrWhiteSpace(safeModulePath))
                {
                    startInfo.Environment["PSModulePath"] = safeModulePath;
                }

                AppLogger.Debug(
                    "PowerShellBackgroundEnvironment",
                    $"Configured background PowerShell environment. Purpose='{normalizedPurpose}', Home='{homeRoot}', Runtime='{runtimePath ?? string.Empty}'.");
            }
            catch (Exception ex)
            {
                // Background environment isolation is a hardening/performance feature. If it fails,
                // do not prevent metadata/diagnostics from starting; log and let the caller proceed
                // with the normal process environment.
                AppLogger.Warning(
                    "PowerShellBackgroundEnvironment",
                    $"Failed to configure isolated PowerShell environment for purpose '{purpose}': {ex.Message}");
            }
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
