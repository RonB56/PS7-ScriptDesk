using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Security;
using System.Runtime.Versioning;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.PowerShell.Services
{
    public class RuntimeService : IRuntimeService
    {
        private const int ProbeTimeoutMilliseconds = 8000;
        private const int AliasProbeTimeoutMilliseconds = 1500;
        private static readonly string[] RegistryRootsToInspect =
        {
            @"SOFTWARE\Microsoft\PowerShellCore\InstalledVersions",
            @"SOFTWARE\WOW6432Node\Microsoft\PowerShellCore\InstalledVersions"
        };

        public RuntimeDiscoveryResult DiscoverRuntimes()
        {
            var discoveryStopwatch = Stopwatch.StartNew();
            StartupTimingLogger.Log("RuntimeService", "DiscoverRuntimes started.");
            var detectedRuntimes = new List<PowerShellRuntimeInfo>();

            foreach (var candidate in EnumerateCandidateExecutablePaths())
            {
                var runtime = TryResolveRuntimeCandidate(
                    candidate.Path,
                    candidate.Source,
                    allowProcessProbe: ShouldProbeRuntimeCandidate(candidate.Path));

                if (runtime is null)
                {
                    continue;
                }

                if (runtime.IsWindowsPowerShell && runtime.Version.Major != 5)
                {
                    StartupTimingLogger.Log("RuntimeService", $"Ignored suspicious Windows PowerShell runtime '{runtime.DisplayName}' from {runtime.ExecutablePath}.");
                    continue;
                }

                detectedRuntimes.Add(runtime);
            }

            var consolidatedRuntimes = ConsolidateDuplicateRuntimes(detectedRuntimes);

            var orderedRuntimes = consolidatedRuntimes
                .OrderByDescending(GetRuntimePriority)
                .ThenByDescending(runtime => runtime.Version)
                .ThenBy(runtime => runtime.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preferredRuntime = orderedRuntimes.FirstOrDefault();

            var finalizedRuntimes = orderedRuntimes
                .Select(runtime => new PowerShellRuntimeInfo(
                    runtime.DisplayName,
                    runtime.Edition,
                    runtime.VersionText,
                    runtime.Version,
                    runtime.Architecture,
                    runtime.ExecutablePath,
                    runtime.DiscoverySource,
                    runtime.IsPowerShell7OrLater,
                    runtime.IsWindowsPowerShell,
                    isPreferred: preferredRuntime is not null &&
                                 string.Equals(runtime.ExecutablePath, preferredRuntime.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var finalizedPreferredRuntime = finalizedRuntimes.FirstOrDefault(runtime => runtime.IsPreferred);
            var summaryText = BuildSummaryText(finalizedRuntimes, finalizedPreferredRuntime);

            StartupTimingLogger.Log("RuntimeService", $"DiscoverRuntimes completed in {discoveryStopwatch.ElapsedMilliseconds} ms with {finalizedRuntimes.Count} finalized runtime(s).");
            return new RuntimeDiscoveryResult(finalizedRuntimes, finalizedPreferredRuntime, summaryText);
        }

        public PowerShellRuntimeInfo? TryResolveRuntimeIdentity(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            var normalizedRuntimePath = NormalizeExecutablePath(executablePath);
            var stopwatch = Stopwatch.StartNew();
            var runtime = TryResolveRuntimeCandidate(
                normalizedRuntimePath,
                "Persisted runtime selection",
                allowProcessProbe: ShouldProbeRuntimeCandidate(normalizedRuntimePath));
            stopwatch.Stop();

            if (runtime is null)
            {
                StartupTimingLogger.Log("RuntimeService", $"Persisted runtime identity could not be resolved for '{normalizedRuntimePath}' after {stopwatch.ElapsedMilliseconds} ms.");
                return null;
            }

            StartupTimingLogger.Log("RuntimeService", $"Persisted runtime identity resolved in {stopwatch.ElapsedMilliseconds} ms: {runtime.DisplayName} ({normalizedRuntimePath})");
            return new PowerShellRuntimeInfo(
                runtime.DisplayName,
                runtime.Edition,
                runtime.VersionText,
                runtime.Version,
                runtime.Architecture,
                runtime.ExecutablePath,
                runtime.DiscoverySource,
                runtime.IsPowerShell7OrLater,
                runtime.IsWindowsPowerShell,
                isPreferred: true);
        }

        private static IReadOnlyList<PowerShellRuntimeInfo> ConsolidateDuplicateRuntimes(IReadOnlyList<PowerShellRuntimeInfo> detectedRuntimes)
        {
            var consolidated = new Dictionary<string, PowerShellRuntimeInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var runtime in detectedRuntimes)
            {
                var key = runtime.IsWindowsPowerShell
                    ? $"winps|{runtime.Edition}|{runtime.VersionText}"
                    : runtime.ExecutablePath;

                if (!consolidated.TryGetValue(key, out var existingRuntime))
                {
                    consolidated[key] = runtime;
                    continue;
                }

                consolidated[key] = ChoosePreferredDuplicate(existingRuntime, runtime);
            }

            return consolidated.Values.ToList();
        }

        private static PowerShellRuntimeInfo ChoosePreferredDuplicate(PowerShellRuntimeInfo first, PowerShellRuntimeInfo second)
        {
            if (first.IsWindowsPowerShell && second.IsWindowsPowerShell)
            {
                var firstScore = GetWindowsPowerShellPathPreference(first.ExecutablePath);
                var secondScore = GetWindowsPowerShellPathPreference(second.ExecutablePath);
                return secondScore > firstScore ? second : first;
            }

            return second.Version > first.Version ? second : first;
        }

        private static int GetWindowsPowerShellPathPreference(string path)
        {
            if (path.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (path.Contains(@"\SysWOW64\", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 0;
        }

        private static IEnumerable<(string Path, string Source)> EnumerateCandidateExecutablePaths()
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in GetKnownPowerShellCoreCandidates())
            {
                if (seenPaths.Add(candidate.Path))
                {
                    yield return candidate;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var candidate in GetRegistryPowerShellCoreCandidates())
                {
                    if (seenPaths.Add(candidate.Path))
                    {
                        yield return candidate;
                    }
                }
            }

            foreach (var candidate in GetPathEnvironmentCandidates())
            {
                if (seenPaths.Add(candidate.Path))
                {
                    yield return candidate;
                }
            }

            if (seenPaths.Count == 0)
            {
                foreach (var candidate in GetWhereCommandCandidates())
                {
                    if (seenPaths.Add(candidate.Path))
                    {
                        yield return candidate;
                    }
                }
            }

            foreach (var candidate in GetWindowsPowerShellCandidates())
            {
                if (seenPaths.Add(candidate.Path))
                {
                    yield return candidate;
                }
            }
        }

        private static IEnumerable<(string Path, string Source)> GetKnownPowerShellCoreCandidates()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddIfDirectoryExists(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell"));
            AddIfDirectoryExists(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell"));

            var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrWhiteSpace(programW6432))
            {
                AddIfDirectoryExists(roots, Path.Combine(programW6432, "PowerShell"));
            }

            foreach (var root in roots)
            {
                IEnumerable<string> subDirectories;
                try
                {
                    subDirectories = Directory.EnumerateDirectories(root)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch
                {
                    subDirectories = Array.Empty<string>();
                }

                foreach (var directory in subDirectories)
                {
                    var candidatePath = Path.Combine(directory, "pwsh.exe");
                    if (File.Exists(candidatePath))
                    {
                        yield return (candidatePath, "Program Files PowerShell");
                    }
                }
            }

            var localWindowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "pwsh.exe");

            if (File.Exists(localWindowsApps))
            {
                yield return (localWindowsApps, "WindowsApps alias");
            }
        }

        [SupportedOSPlatform("windows")]
        private static IEnumerable<(string Path, string Source)> GetRegistryPowerShellCoreCandidates()
        {
            var candidates = new List<(string Path, string Source)>();

            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var rootPath in RegistryRootsToInspect)
                {
                    RegistryView[] views = hive == RegistryHive.LocalMachine
                        ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                        : new[] { RegistryView.Default };

                    foreach (var view in views)
                    {
                        RegistryKey? baseKey = null;
                        RegistryKey? rootKey = null;

                        try
                        {
                            baseKey = RegistryKey.OpenBaseKey(hive, view);
                            rootKey = baseKey.OpenSubKey(rootPath);
                            if (rootKey is null)
                            {
                                continue;
                            }

                            foreach (var subKeyName in rootKey.GetSubKeyNames())
                            {
                                using var subKey = rootKey.OpenSubKey(subKeyName);
                                var installPath = subKey?.GetValue("InstallPath") as string
                                    ?? subKey?.GetValue("ApplicationBase") as string;

                                if (string.IsNullOrWhiteSpace(installPath))
                                {
                                    continue;
                                }

                                var candidatePath = Path.Combine(installPath, "pwsh.exe");
                                if (File.Exists(candidatePath))
                                {
                                    candidates.Add((candidatePath, $"Registry ({hive}, {view})"));
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
                        {
                            StartupTimingLogger.Log("RuntimeService", $"Registry probe skipped for {hive}/{view}/{rootPath}: {ex.Message}");
                        }
                        finally
                        {
                            rootKey?.Dispose();
                            baseKey?.Dispose();
                        }
                    }
                }
            }

            return candidates;
        }

        private static IEnumerable<(string Path, string Source)> GetPathEnvironmentCandidates()
        {
            var processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathEntries = processPath
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizePathEntry)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in pathEntries)
            {
                if (!TryDirectoryExists(directory!))
                {
                    continue;
                }

                var pwshPath = Path.Combine(directory!, "pwsh.exe");
                if (File.Exists(pwshPath))
                {
                    yield return (pwshPath, "PATH");
                }

                var powershellPath = Path.Combine(directory!, "powershell.exe");
                if (File.Exists(powershellPath))
                {
                    yield return (powershellPath, "PATH");
                }
            }
        }

        private static IEnumerable<(string Path, string Source)> GetWhereCommandCandidates()
        {
            foreach (var executableName in new[] { "pwsh.exe", "powershell.exe" })
            {
                foreach (var path in RunWhereCommand(executableName))
                {
                    yield return (path, $"where.exe {executableName}");
                }
            }
        }

        private static IEnumerable<string> RunWhereCommand(string executableName)
        {
            var results = new List<string>();

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = executableName,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                if (!process.WaitForExit(2000))
                {
                    TryKillProcess(process);
                    return results;
                }

                var output = process.StandardOutput.ReadToEnd();
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = line.Trim();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        results.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("RuntimeService", $"where.exe lookup failed for {executableName}: {ex.Message}");
            }

            return results;
        }

        private static string? NormalizePathEntry(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            var normalized = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return normalized;
        }

        private static bool TryDirectoryExists(string directory)
        {
            try
            {
                return Directory.Exists(directory);
            }
            catch (UnauthorizedAccessException)
            {
                StartupTimingLogger.Log("RuntimeService", $"Skipped inaccessible PATH directory during runtime discovery: {directory}");
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static IEnumerable<(string Path, string Source)> GetWindowsPowerShellCandidates()
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                var system32Path = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(system32Path))
                {
                    yield return (system32Path, "Windows system path");
                }

                var sysWow64Path = Path.Combine(windowsDirectory, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(sysWow64Path))
                {
                    yield return (sysWow64Path, "Windows system path");
                }
            }
        }

        private static void AddIfDirectoryExists(ISet<string> roots, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                roots.Add(path);
            }
        }

        private static PowerShellRuntimeInfo? TryResolveRuntimeCandidate(string executablePath, string discoverySource, bool allowProcessProbe)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            var normalizedRuntimePath = NormalizeExecutablePath(executablePath);
            var metadataStopwatch = Stopwatch.StartNew();
            var metadataRuntime = TryBuildRuntimeFromFileMetadata(normalizedRuntimePath, discoverySource, "startup file metadata");
            metadataStopwatch.Stop();
            if (metadataRuntime is not null)
            {
                StartupTimingLogger.Log("RuntimeService", $"Resolved runtime from file metadata in {metadataStopwatch.ElapsedMilliseconds} ms: {metadataRuntime.DisplayName} ({normalizedRuntimePath})");
                return metadataRuntime;
            }

            if (!allowProcessProbe)
            {
                StartupTimingLogger.Log("RuntimeService", $"Skipping process probe for candidate without usable metadata: {normalizedRuntimePath}");
                return null;
            }

            return TryProbeRuntime(
                normalizedRuntimePath,
                discoverySource,
                normalizedRuntimePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
                    ? AliasProbeTimeoutMilliseconds
                    : ProbeTimeoutMilliseconds);
        }

        private static bool ShouldProbeRuntimeCandidate(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            return executablePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                   (string.Equals(Path.GetFileName(executablePath), "powershell.exe", StringComparison.OrdinalIgnoreCase) &&
                    executablePath.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase));
        }

        private static PowerShellRuntimeInfo? TryProbeRuntime(string executablePath, string discoverySource, int timeoutMilliseconds)
        {
            if (!File.Exists(executablePath))
            {
                StartupTimingLogger.Log("RuntimeService", $"Skipped missing candidate: {executablePath}");
                return null;
            }

            var probeStopwatch = Stopwatch.StartNew();

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"$v = $PSVersionTable.PSVersion; $edition = $PSVersionTable.PSEdition; $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture; [Console]::Out.WriteLine($edition + '|' + $v.ToString() + '|' + $v.Major + '|' + $v.Minor + '|' + $v.Build + '|' + $v.Revision + '|' + $arch.ToString())\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe timed out after {probeStopwatch.ElapsedMilliseconds} ms: {executablePath}");
                    TryKillProcess(process);
                    return TryBuildRuntimeFromFileMetadata(executablePath, discoverySource, "probe timeout fallback");
                }

                var standardOutput = process.StandardOutput.ReadToEnd().Trim();
                var standardError = process.StandardError.ReadToEnd().Trim();

                if (!string.IsNullOrWhiteSpace(standardOutput) && process.ExitCode == 0)
                {
                    var parsedRuntime = TryParseRuntimeProbe(executablePath, discoverySource, standardOutput);
                    if (parsedRuntime is not null)
                    {
                        StartupTimingLogger.Log("RuntimeService", $"Probe succeeded in {probeStopwatch.ElapsedMilliseconds} ms: {parsedRuntime.DisplayName} ({executablePath})");
                        return parsedRuntime;
                    }
                }

                if (!string.IsNullOrWhiteSpace(standardError))
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe returned stderr after {probeStopwatch.ElapsedMilliseconds} ms: {executablePath} -> {standardError}");
                }
                else
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe returned no usable output after {probeStopwatch.ElapsedMilliseconds} ms: {executablePath}");
                }

                return TryBuildRuntimeFromFileMetadata(executablePath, discoverySource, "file metadata fallback");
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("RuntimeService", $"Probe failed after {probeStopwatch.ElapsedMilliseconds} ms: {executablePath} -> {ex.GetType().Name}: {ex.Message}");
                return TryBuildRuntimeFromFileMetadata(executablePath, discoverySource, "exception fallback");
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        private static PowerShellRuntimeInfo? TryParseRuntimeProbe(string executablePath, string discoverySource, string output)
        {
            var line = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = line.Split('|');
            if (parts.Length < 6)
            {
                return null;
            }

            var edition = string.IsNullOrWhiteSpace(parts[0]) ? "Unknown" : parts[0].Trim();
            var versionText = string.IsNullOrWhiteSpace(parts[1]) ? "Unknown" : parts[1].Trim();

            if (!int.TryParse(parts[2], out var major))
            {
                major = 0;
            }

            if (!int.TryParse(parts[3], out var minor))
            {
                minor = 0;
            }

            if (!int.TryParse(parts[4], out var build))
            {
                build = 0;
            }

            if (!int.TryParse(parts[5], out var revision))
            {
                revision = -1;
            }

            var architecture = parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6])
                ? parts[6].Trim()
                : string.Empty;

            if (string.Equals(Path.GetFileName(executablePath), "powershell.exe", StringComparison.OrdinalIgnoreCase) &&
                executablePath.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase) &&
                major != 5)
            {
                StartupTimingLogger.Log("RuntimeService", $"Ignored suspicious Windows PowerShell version '{versionText}' from {executablePath}.");
                return null;
            }

            return CreateRuntimeInfo(executablePath, discoverySource, edition, versionText, architecture, major, minor, build, revision);
        }

        private static PowerShellRuntimeInfo? TryBuildRuntimeFromFileMetadata(string executablePath, string discoverySource, string fallbackReason)
        {
            if (!ShouldAllowMetadataFallback(executablePath))
            {
                StartupTimingLogger.Log("RuntimeService", $"Skipped file metadata fallback for {executablePath}.");
                return null;
            }

            try
            {
                var fileInfo = FileVersionInfo.GetVersionInfo(executablePath);
                if (!LooksLikePowerShellBinary(fileInfo))
                {
                    StartupTimingLogger.Log("RuntimeService", $"Skipped file metadata fallback because metadata did not look like PowerShell: {executablePath}");
                    return null;
                }
                var parsedVersionText = ParseVersionText(fileInfo.ProductVersion) ?? ParseVersionText(fileInfo.FileVersion);

                var major = fileInfo.ProductMajorPart > 0 ? fileInfo.ProductMajorPart : fileInfo.FileMajorPart;
                var minor = fileInfo.ProductMinorPart >= 0 ? fileInfo.ProductMinorPart : fileInfo.FileMinorPart;
                var build = fileInfo.ProductBuildPart >= 0 ? fileInfo.ProductBuildPart : fileInfo.FileBuildPart;
                var revision = fileInfo.ProductPrivatePart >= 0 ? fileInfo.ProductPrivatePart : fileInfo.FilePrivatePart;

                if (major <= 0 && !TrySplitVersionText(parsedVersionText, out major, out minor, out build, out revision))
                {
                    return null;
                }

                if (major <= 0)
                {
                    return null;
                }

                var isWindowsPowerShellCandidate = string.Equals(Path.GetFileName(executablePath), "powershell.exe", StringComparison.OrdinalIgnoreCase)
                    && executablePath.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase);

                if (isWindowsPowerShellCandidate && major != 5)
                {
                    StartupTimingLogger.Log("RuntimeService", $"Skipped Windows PowerShell metadata fallback because it produced suspicious version '{parsedVersionText ?? BuildVersionText(major, minor, build, revision)}' for {executablePath}.");
                    return null;
                }

                var edition = string.Equals(Path.GetFileName(executablePath), "pwsh.exe", StringComparison.OrdinalIgnoreCase)
                    ? "Core"
                    : "Desktop";
                var architecture = TryDetectExecutableArchitecture(executablePath);

                var versionText = !string.IsNullOrWhiteSpace(parsedVersionText)
                    ? parsedVersionText!
                    : BuildVersionText(major, minor, build, revision);

                var runtime = CreateRuntimeInfo(executablePath, $"{discoverySource} ({fallbackReason})", edition, versionText, architecture, major, minor, build, revision);
                StartupTimingLogger.Log("RuntimeService", $"Runtime built from file metadata: {runtime.DisplayName} ({executablePath})");
                return runtime;
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("RuntimeService", $"File metadata fallback failed for {executablePath}: {ex.Message}");
                return null;
            }
        }

        private static bool ShouldAllowMetadataFallback(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            if (executablePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fileName = Path.GetFileName(executablePath);
            if (string.Equals(fileName, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(fileName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
                && executablePath.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeExecutablePath(string executablePath)
        {
            try
            {
                return Path.GetFullPath(executablePath).Trim();
            }
            catch
            {
                return executablePath.Trim();
            }
        }

        private static bool LooksLikePowerShellBinary(FileVersionInfo fileInfo)
        {
            var productName = fileInfo.ProductName ?? string.Empty;
            var fileDescription = fileInfo.FileDescription ?? string.Empty;
            var originalFilename = fileInfo.OriginalFilename ?? string.Empty;

            return productName.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
                || fileDescription.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
                || originalFilename.Contains("pwsh", StringComparison.OrdinalIgnoreCase)
                || originalFilename.Contains("powershell", StringComparison.OrdinalIgnoreCase);
        }

        private static PowerShellRuntimeInfo CreateRuntimeInfo(
            string executablePath,
            string discoverySource,
            string edition,
            string versionText,
            string architecture,
            int major,
            int minor,
            int build,
            int revision)
        {
            var normalizedVersion = CreateVersion(major, minor, build, revision);
            var isPowerShell7OrLater = string.Equals(Path.GetFileName(executablePath), "pwsh.exe", StringComparison.OrdinalIgnoreCase)
                                        && major >= 7;
            var isWindowsPowerShell = string.Equals(Path.GetFileName(executablePath), "powershell.exe", StringComparison.OrdinalIgnoreCase)
                                      && executablePath.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase);

            var displayName = BuildDisplayName(edition, versionText, isPowerShell7OrLater, isWindowsPowerShell);

            return new PowerShellRuntimeInfo(
                displayName,
                edition,
                versionText,
                normalizedVersion,
                architecture?.Trim() ?? string.Empty,
                executablePath,
                discoverySource,
                isPowerShell7OrLater,
                isWindowsPowerShell,
                isPreferred: false);
        }

        private static string TryDetectExecutableArchitecture(string executablePath)
        {
            try
            {
                using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(stream);
                if (stream.Length < 0x40)
                {
                    return string.Empty;
                }

                stream.Seek(0x3C, SeekOrigin.Begin);
                var peHeaderOffset = reader.ReadInt32();
                if (peHeaderOffset <= 0 || peHeaderOffset + 6 > stream.Length)
                {
                    return string.Empty;
                }

                stream.Seek(peHeaderOffset + 4, SeekOrigin.Begin);
                var machine = reader.ReadUInt16();
                return machine switch
                {
                    0x014c => "X86",
                    0x8664 => "X64",
                    0x01c4 => "Arm",
                    0xAA64 => "Arm64",
                    _ => string.Empty,
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Version CreateVersion(int major, int minor, int build, int revision)
        {
            var normalizedBuild = build < 0 ? 0 : build;

            if (revision < 0)
            {
                return new Version(Math.Max(major, 0), Math.Max(minor, 0), normalizedBuild);
            }

            return new Version(Math.Max(major, 0), Math.Max(minor, 0), normalizedBuild, revision);
        }

        private static string BuildDisplayName(string edition, string versionText, bool isPowerShell7OrLater, bool isWindowsPowerShell)
        {
            if (isPowerShell7OrLater)
            {
                return $"PowerShell {versionText}";
            }

            if (isWindowsPowerShell)
            {
                return $"Windows PowerShell {versionText}";
            }

            if (string.Equals(edition, "Core", StringComparison.OrdinalIgnoreCase))
            {
                return $"PowerShell Core {versionText}";
            }

            return $"PowerShell {versionText}";
        }

        private static int GetRuntimePriority(PowerShellRuntimeInfo runtime)
        {
            if (runtime.IsPowerShell7OrLater)
            {
                return 300;
            }

            if (string.Equals(runtime.Edition, "Core", StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }

            if (runtime.IsWindowsPowerShell)
            {
                return 100;
            }

            return 0;
        }

        private static string BuildSummaryText(IReadOnlyList<PowerShellRuntimeInfo> detectedRuntimes, PowerShellRuntimeInfo? preferredRuntime)
        {
            if (preferredRuntime is null)
            {
                return "Runtime: No PowerShell runtime detected";
            }

            return $"Runtime: {preferredRuntime.DisplayName} preferred ({detectedRuntimes.Count} detected)";
        }

        private static string? ParseVersionText(string? rawVersionText)
        {
            if (string.IsNullOrWhiteSpace(rawVersionText))
            {
                return null;
            }

            var trimmed = rawVersionText.Trim();
            var numericChars = new List<char>();

            foreach (var character in trimmed)
            {
                if (char.IsDigit(character) || character == '.')
                {
                    numericChars.Add(character);
                    continue;
                }

                if (numericChars.Count > 0)
                {
                    break;
                }
            }

            var result = new string(numericChars.ToArray()).Trim('.');
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static bool TrySplitVersionText(string? versionText, out int major, out int minor, out int build, out int revision)
        {
            major = 0;
            minor = 0;
            build = 0;
            revision = -1;

            if (string.IsNullOrWhiteSpace(versionText))
            {
                return false;
            }

            var parts = versionText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out major))
            {
                return false;
            }

            int.TryParse(parts[1], out minor);
            if (parts.Length > 2)
            {
                int.TryParse(parts[2], out build);
            }

            if (parts.Length > 3)
            {
                int.TryParse(parts[3], out revision);
            }

            return true;
        }

        private static string BuildVersionText(int major, int minor, int build, int revision)
        {
            if (revision >= 0)
            {
                return $"{major}.{minor}.{Math.Max(build, 0)}.{revision}";
            }

            if (build > 0)
            {
                return $"{major}.{minor}.{build}";
            }

            return $"{major}.{minor}";
        }
    }
}
