using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using PS7ScriptDesk.Application.Diagnostics;
using Microsoft.Win32;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services
{
    public class RuntimeService : IRuntimeService
    {
        private const int ProbeTimeoutMilliseconds = 4000;
        private const int AliasProbeTimeoutMilliseconds = 2500;
        private const int MaxProbePreviewLength = 300;
        private static readonly string[] RegistryRootsToInspect =
        {
            @"SOFTWARE\Microsoft\PowerShellCore\InstalledVersions",
            @"SOFTWARE\WOW6432Node\Microsoft\PowerShellCore\InstalledVersions"
        };

        private readonly string? _configuredRuntimePath;

        public RuntimeService(string? configuredRuntimePath = null)
        {
            _configuredRuntimePath = NormalizeExecutablePath(configuredRuntimePath);
        }

        public RuntimeDiscoveryResult DiscoverRuntimes()
        {
            return DiscoverRuntimes(requireLaunchValidation: false);
        }

        public RuntimeDiscoveryResult DiscoverRuntimes(bool requireLaunchValidation)
        {
            var discoveryStopwatch = Stopwatch.StartNew();
            StartupTimingLogger.Log(
                "RuntimeService",
                requireLaunchValidation
                    ? "DiscoverRuntimes started. Mode=LaunchValidation"
                    : "DiscoverRuntimes started. Mode=MetadataFastPath");
            DeveloperDiagnostics.LogOperationStart(
                "Runtime",
                "DiscoverRuntimes",
                requireLaunchValidation
                    ? "PowerShell runtime discovery started with launch validation required."
                    : "PowerShell runtime discovery started with metadata fast path allowed.");

            var candidateResults = new List<RuntimeDiscoveryCandidateInfo>();
            var detectedRuntimes = new List<PowerShellRuntimeInfo>();

            var candidates = EnumerateCandidateExecutablePaths().ToList();
            StartupTimingLogger.Log(
                "RuntimeService",
                $"Candidate enumeration produced {candidates.Count} candidate(s). ConfiguredRuntimePath='{_configuredRuntimePath ?? string.Empty}', " +
                $"ProgramFiles='{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}', " +
                $"ProgramFilesX86='{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)}', " +
                $"LocalApplicationData='{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}'.");

            if (candidates.Count == 0)
            {
                StartupTimingLogger.Log(
                    "RuntimeService",
                    "No pwsh.exe candidates were discovered from configured path, Program Files, registry, PATH, WindowsApps alias, or where.exe.");
            }

            foreach (var candidate in candidates)
            {
                StartupTimingLogger.Log("RuntimeService", $"Considering runtime candidate '{candidate.Path}' from {candidate.Source}.");

                if (!requireLaunchValidation)
                {
                    var metadataResult = TryBuildRuntimeFromFileMetadata(candidate.Path, candidate.Source);
                    if (metadataResult.RuntimeInfo is not null)
                    {
                        candidateResults.Add(metadataResult.CandidateInfo);
                        detectedRuntimes.Add(metadataResult.RuntimeInfo);
                        LogAcceptedCandidate(metadataResult.RuntimeInfo, metadataResult.CandidateInfo);
                        continue;
                    }
                }

                // Unqualified command-resolution candidates such as "pwsh.exe" cannot be
                // trusted from file metadata because Windows resolves them only at launch
                // time.  Manual refresh and background safety verification require a real
                // launch probe so stale/corrupt/blocked pwsh.exe paths are not trusted forever.
                var probeResult = ProbeRuntimeCandidate(candidate.Path, candidate.Source);
                candidateResults.Add(probeResult.CandidateInfo);

                if (probeResult.RuntimeInfo is null)
                {
                    LogRejectedCandidate(probeResult.CandidateInfo);
                    continue;
                }

                detectedRuntimes.Add(probeResult.RuntimeInfo);
                LogAcceptedCandidate(probeResult.RuntimeInfo, probeResult.CandidateInfo);
            }

            var consolidatedRuntimes = ConsolidateDuplicateRuntimes(detectedRuntimes);
            var orderedRuntimes = consolidatedRuntimes
                .OrderByDescending(GetRuntimePriority)
                .ThenByDescending(runtime => runtime.IsValidated)
                .ThenByDescending(runtime => runtime.Version)
                .ThenBy(runtime => runtime.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preferredRuntime = orderedRuntimes.FirstOrDefault(runtime => runtime.IsPowerShell7OrLater && runtime.IsValidated);
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
                                 string.Equals(runtime.ExecutablePath, preferredRuntime.ExecutablePath, StringComparison.OrdinalIgnoreCase),
                    runtime.IsValidated,
                    runtime.IsWindowsAppsAlias,
                    runtime.ResolvedExecutablePath,
                    runtime.PsHome,
                    runtime.ValidationMessage))
                .ToList();

            var finalizedPreferredRuntime = finalizedRuntimes.FirstOrDefault(runtime => runtime.IsPreferred);
            var summaryText = BuildSummaryText(finalizedRuntimes, finalizedPreferredRuntime);

            StartupTimingLogger.Log(
                "RuntimeService",
                $"DiscoverRuntimes completed in {discoveryStopwatch.ElapsedMilliseconds} ms with {finalizedRuntimes.Count} validated runtime(s). Mode={(requireLaunchValidation ? "LaunchValidation" : "MetadataFastPath")}");
            DeveloperDiagnostics.LogOperationStop(
                "Runtime",
                "DiscoverRuntimes",
                finalizedPreferredRuntime is null
                    ? "PowerShell runtime discovery completed without a valid PowerShell 7 runtime."
                    : "PowerShell runtime discovery completed with a valid PowerShell 7 runtime.",
                discoveryStopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["candidateCount"] = candidateResults.Count,
                    ["validatedRuntimeCount"] = finalizedRuntimes.Count,
                    ["preferredRuntimePath"] = finalizedPreferredRuntime?.ExecutablePath,
                    ["preferredRuntimeVersion"] = finalizedPreferredRuntime?.VersionText,
                    ["requireLaunchValidation"] = requireLaunchValidation
                });
            return new RuntimeDiscoveryResult(finalizedRuntimes, finalizedPreferredRuntime, summaryText, candidateResults);
        }

        public PowerShellRuntimeInfo? TryResolveRuntimeIdentity(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            var normalizedRuntimePath = NormalizeExecutablePath(executablePath);
            var stopwatch = Stopwatch.StartNew();

            var metadataResult = TryBuildRuntimeFromFileMetadata(normalizedRuntimePath, "Persisted runtime selection");
            if (metadataResult.RuntimeInfo is not null)
            {
                stopwatch.Stop();
                StartupTimingLogger.Log(
                    "RuntimeService",
                    $"Persisted runtime identity resolved from file metadata in {stopwatch.ElapsedMilliseconds} ms: {metadataResult.RuntimeInfo.DisplayName} ({normalizedRuntimePath})");
                return CreatePreferredRuntimeCopy(metadataResult.RuntimeInfo);
            }

            var probeResult = ProbeRuntimeCandidate(normalizedRuntimePath, "Persisted runtime selection");
            stopwatch.Stop();

            if (probeResult.RuntimeInfo is null)
            {
                StartupTimingLogger.Log(
                    "RuntimeService",
                    $"Persisted runtime identity could not be resolved for '{normalizedRuntimePath}' after {stopwatch.ElapsedMilliseconds} ms. " +
                    $"Reason={probeResult.CandidateInfo.FailureReason}");
                return null;
            }

            StartupTimingLogger.Log("RuntimeService", $"Persisted runtime identity resolved in {stopwatch.ElapsedMilliseconds} ms: {probeResult.RuntimeInfo.DisplayName} ({normalizedRuntimePath})");
            return CreatePreferredRuntimeCopy(probeResult.RuntimeInfo);
        }

        public RuntimeValidationResult ValidateRuntimePath(string executablePath, string source)
        {
            var probeResult = ProbeRuntimeCandidate(executablePath, source);

            if (probeResult.RuntimeInfo is null)
            {
                LogRejectedCandidate(probeResult.CandidateInfo);
            }
            else
            {
                LogAcceptedCandidate(probeResult.RuntimeInfo, probeResult.CandidateInfo);
            }

            return new RuntimeValidationResult(probeResult.RuntimeInfo, probeResult.CandidateInfo);
        }

        public RuntimeValidationResult ValidateRuntimePathFromFileMetadata(string executablePath, string source)
        {
            var metadataResult = TryBuildRuntimeFromFileMetadata(executablePath, source);

            if (metadataResult.RuntimeInfo is null)
            {
                LogRejectedCandidate(metadataResult.CandidateInfo);
            }
            else
            {
                LogAcceptedCandidate(metadataResult.RuntimeInfo, metadataResult.CandidateInfo);
            }

            return new RuntimeValidationResult(metadataResult.RuntimeInfo, metadataResult.CandidateInfo);
        }

        private static PowerShellRuntimeInfo CreatePreferredRuntimeCopy(PowerShellRuntimeInfo runtime)
        {
            return new PowerShellRuntimeInfo(
                runtime.DisplayName,
                runtime.Edition,
                runtime.VersionText,
                runtime.Version,
                runtime.Architecture,
                runtime.LaunchExecutablePath,
                runtime.DiscoverySource,
                runtime.IsPowerShell7OrLater,
                runtime.IsWindowsPowerShell,
                isPreferred: runtime.IsPowerShell7OrLater && runtime.IsValidated,
                runtime.IsValidated,
                runtime.IsWindowsAppsAlias,
                runtime.ResolvedExecutablePath,
                runtime.PsHome,
                runtime.ValidationMessage);
        }

        private static IReadOnlyList<PowerShellRuntimeInfo> ConsolidateDuplicateRuntimes(IReadOnlyList<PowerShellRuntimeInfo> detectedRuntimes)
        {
            var consolidated = new Dictionary<string, PowerShellRuntimeInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var runtime in detectedRuntimes)
            {
                var key = BuildRuntimeDeduplicationKey(runtime);

                if (!consolidated.TryGetValue(key, out var existingRuntime))
                {
                    consolidated[key] = runtime;
                    continue;
                }

                var preferredRuntime = ChoosePreferredDuplicate(existingRuntime, runtime);
                consolidated[key] = preferredRuntime;
                StartupTimingLogger.Log(
                    "RuntimeService",
                    $"Removed duplicate runtime candidate during consolidation. Key='{key}', Kept='{preferredRuntime.ExecutablePath}', " +
                    $"Discarded='{(ReferenceEquals(preferredRuntime, existingRuntime) ? runtime.ExecutablePath : existingRuntime.ExecutablePath)}'.");
            }

            return consolidated.Values.ToList();
        }

        private static string BuildRuntimeDeduplicationKey(PowerShellRuntimeInfo runtime)
        {
            if (runtime.IsWindowsPowerShell)
            {
                return string.Join(
                    "|",
                    "winps",
                    NormalizeDisplayToken(runtime.VersionText),
                    NormalizeDisplayToken(runtime.Edition));
            }

            var resolvedPath = NormalizeComparablePath(runtime.ResolvedExecutablePath);
            var executablePath = NormalizeComparablePath(runtime.ExecutablePath);

            return string.Join(
                "|",
                NormalizeDisplayToken(runtime.Edition),
                NormalizeDisplayToken(runtime.VersionText),
                !string.IsNullOrWhiteSpace(resolvedPath) ? resolvedPath : executablePath,
                NormalizeDisplayToken(runtime.Architecture));
        }

        private static PowerShellRuntimeInfo ChoosePreferredDuplicate(PowerShellRuntimeInfo first, PowerShellRuntimeInfo second)
        {
            if (first.IsValidated != second.IsValidated)
            {
                return second.IsValidated ? second : first;
            }

            if (first.IsWindowsAppsAlias != second.IsWindowsAppsAlias)
            {
                return first.IsWindowsAppsAlias ? second : first;
            }

            var firstPathPreference = GetExecutablePathPreference(first);
            var secondPathPreference = GetExecutablePathPreference(second);
            if (secondPathPreference != firstPathPreference)
            {
                return secondPathPreference > firstPathPreference ? second : first;
            }

            if (first.IsWindowsPowerShell && second.IsWindowsPowerShell)
            {
                var firstScore = GetWindowsPowerShellPathPreference(first.ExecutablePath);
                var secondScore = GetWindowsPowerShellPathPreference(second.ExecutablePath);
                if (secondScore != firstScore)
                {
                    return secondScore > firstScore ? second : first;
                }
            }

            return second.Version > first.Version ? second : first;
        }

        private static int GetExecutablePathPreference(PowerShellRuntimeInfo runtime)
        {
            var executablePath = NormalizeComparablePath(runtime.ExecutablePath);
            var resolvedPath = NormalizeComparablePath(runtime.ResolvedExecutablePath);

            if (!string.IsNullOrWhiteSpace(resolvedPath) &&
                string.Equals(executablePath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return 2;
            }

            if (!IsUnqualifiedExecutableName(executablePath))
            {
                return 1;
            }

            return 0;
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

        private IEnumerable<(string Path, string Source)> EnumerateCandidateExecutablePaths()
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(_configuredRuntimePath) && seenPaths.Add(_configuredRuntimePath))
            {
                yield return (_configuredRuntimePath, "Configured path");
            }

            foreach (var candidate in GetKnownPowerShellCoreCandidates(includeWindowsAppsAlias: false))
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

            var hasExistingQualifiedCandidate = seenPaths.Any(path => !IsUnqualifiedExecutableName(path) && File.Exists(path));

            // Only fall back to command resolution/where.exe when no existing direct pwsh.exe
            // path was found.  If Program Files, registry, PATH, or the configured path already
            // produced a concrete executable, launching an extra "pwsh.exe" probe only adds
            // startup latency and usually resolves to the same file anyway.
            if (!hasExistingQualifiedCandidate)
            {
                foreach (var candidate in GetCommandResolutionCandidates())
                {
                    if (seenPaths.Add(candidate.Path))
                    {
                        yield return candidate;
                    }
                }

                foreach (var candidate in GetWindowsAppsAliasCandidates())
                {
                    if (seenPaths.Add(candidate.Path))
                    {
                        yield return candidate;
                    }
                }

                foreach (var candidate in GetWhereCommandCandidates())
                {
                    if (seenPaths.Add(candidate.Path))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        private static IEnumerable<(string Path, string Source)> GetKnownPowerShellCoreCandidates(bool includeWindowsAppsAlias)
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

            if (!includeWindowsAppsAlias)
            {
                yield break;
            }

            foreach (var candidate in GetWindowsAppsAliasCandidates())
            {
                yield return candidate;
            }
        }

        private static IEnumerable<(string Path, string Source)> GetWindowsAppsAliasCandidates()
        {
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

            }
        }

        private static IEnumerable<(string Path, string Source)> GetCommandResolutionCandidates()
        {
            // This catches Microsoft Store / winget alias installs where pwsh.exe is launchable
            // through normal process command resolution even when Program Files and registry probes
            // do not expose a direct executable path to this packaged desktop app.
            yield return ("pwsh.exe", "Process command resolution (PATH/App Execution Alias)");
        }

        private static IEnumerable<(string Path, string Source)> GetWhereCommandCandidates()
        {
            foreach (var executableName in new[] { "pwsh.exe" })
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

        private static void AddIfDirectoryExists(ISet<string> roots, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                roots.Add(path);
            }
        }

        private static RuntimeProbeResult TryBuildRuntimeFromFileMetadata(string executablePath, string discoverySource)
        {
            var normalizedRuntimePath = NormalizeExecutablePath(executablePath);
            var isCommandResolutionCandidate = IsUnqualifiedExecutableName(normalizedRuntimePath);
            var isWindowsAppsAlias = IsWindowsAppsAliasPath(normalizedRuntimePath);
            var exists = !string.IsNullOrWhiteSpace(normalizedRuntimePath) &&
                         !isCommandResolutionCandidate &&
                         File.Exists(normalizedRuntimePath);

            var fileVersion = string.Empty;
            var productVersion = string.Empty;
            TryReadVersionInfo(normalizedRuntimePath, out fileVersion, out productVersion);

            if (!exists)
            {
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    isCommandResolutionCandidate
                        ? "Command-resolution candidates require a launch probe."
                        : "Candidate path did not exist.");
            }

            if (!string.Equals(Path.GetFileName(normalizedRuntimePath), "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    "PS7 ScriptDesk requires pwsh.exe from PowerShell 7.0 or newer. powershell.exe is not supported.");
            }

            if (!TryExtractPowerShellFileVersion(productVersion, fileVersion, out var parsedVersion, out var versionText, out var failureReason))
            {
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    failureReason);
            }

            if (parsedVersion.Major < 7)
            {
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    $"File metadata reported PowerShell version {versionText}; PS7 or later is required.",
                    edition: "Core",
                    versionText: versionText,
                    architecture: InferRuntimeArchitecture(normalizedRuntimePath));
            }

            var runtimeInfo = CreateRuntimeInfo(
                normalizedRuntimePath,
                discoverySource,
                "Core",
                versionText,
                InferRuntimeArchitecture(normalizedRuntimePath),
                parsedVersion.Major,
                parsedVersion.Minor,
                parsedVersion.Build < 0 ? 0 : parsedVersion.Build,
                parsedVersion.Revision,
                isValidated: true,
                resolvedExecutablePath: normalizedRuntimePath,
                psHome: Path.GetDirectoryName(normalizedRuntimePath) ?? string.Empty,
                validationMessage: "Trusted from pwsh.exe file metadata; launch verification is deferred until the console starts.");

            StartupTimingLogger.Log(
                "RuntimeService",
                $"Runtime built from file metadata: {runtimeInfo.DisplayName} ({normalizedRuntimePath})");

            return RuntimeProbeResult.MetadataSuccess(
                runtimeInfo,
                normalizedRuntimePath,
                discoverySource,
                exists,
                isWindowsAppsAlias,
                fileVersion,
                productVersion);
        }

        private static bool TryExtractPowerShellFileVersion(
            string productVersion,
            string fileVersion,
            out Version parsedVersion,
            out string versionText,
            out string failureReason)
        {
            if (TryParseLeadingVersion(productVersion, out parsedVersion))
            {
                versionText = FormatPowerShellVersionText(parsedVersion);
                failureReason = string.Empty;
                return true;
            }

            if (TryParseLeadingVersion(fileVersion, out parsedVersion))
            {
                versionText = FormatPowerShellVersionText(parsedVersion);
                failureReason = string.Empty;
                return true;
            }

            parsedVersion = new Version(0, 0, 0);
            versionText = string.Empty;
            failureReason = "PowerShell version could not be read from pwsh.exe file metadata.";
            return false;
        }

        private static bool TryParseLeadingVersion(string? value, out Version parsedVersion)
        {
            parsedVersion = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            var end = 0;
            while (end < trimmed.Length && (char.IsDigit(trimmed[end]) || trimmed[end] == '.'))
            {
                end++;
            }

            var versionCandidate = trimmed.Substring(0, end).Trim('.');
            if (string.IsNullOrWhiteSpace(versionCandidate))
            {
                return false;
            }

            var parts = versionCandidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
            {
                return false;
            }

            var build = 0;
            var revision = -1;
            if (parts.Length >= 3)
            {
                _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out build);
            }

            if (parts.Length >= 4)
            {
                _ = int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision);
            }

            parsedVersion = CreateVersion(major, minor, build, revision);
            return true;
        }

        private static string FormatPowerShellVersionText(Version version)
        {
            if (version.Build > 0)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }

            return $"{version.Major}.{version.Minor}";
        }

        private static string InferRuntimeArchitecture(string executablePath)
        {
            if (executablePath.Contains(@"\Program Files (x86)", StringComparison.OrdinalIgnoreCase))
            {
                return "X86";
            }

            return RuntimeInformation.OSArchitecture.ToString();
        }

        private static RuntimeProbeResult ProbeRuntimeCandidate(string executablePath, string discoverySource)
        {
            var normalizedRuntimePath = NormalizeExecutablePath(executablePath);
            var isCommandResolutionCandidate = IsUnqualifiedExecutableName(normalizedRuntimePath);
            var isWindowsAppsAlias = IsWindowsAppsAliasPath(normalizedRuntimePath);
            var exists = !string.IsNullOrWhiteSpace(normalizedRuntimePath) &&
                         (isCommandResolutionCandidate || File.Exists(normalizedRuntimePath));
            var fileVersion = string.Empty;
            var productVersion = string.Empty;

            TryReadVersionInfo(normalizedRuntimePath, out fileVersion, out productVersion);

            if (!exists)
            {
                StartupTimingLogger.Log("RuntimeService", $"Skipped missing candidate: {normalizedRuntimePath}");
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    "Candidate path did not exist.");
            }

            if (!string.Equals(Path.GetFileName(normalizedRuntimePath), "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    "PS7 ScriptDesk requires pwsh.exe from PowerShell 7.0 or newer. powershell.exe is not supported.");
            }

            var timeoutMilliseconds = isWindowsAppsAlias ? AliasProbeTimeoutMilliseconds : ProbeTimeoutMilliseconds;
            var probeStopwatch = Stopwatch.StartNew();

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = normalizedRuntimePath,
                        Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$v=$PSVersionTable.PSVersion; $edition=$PSVersionTable.PSEdition; $arch=[System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture; $runtimePsHome=$PSHOME; try { $processPath=[System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName } catch { $processPath='' }; [Console]::Out.WriteLine($edition + '|' + $v.ToString() + '|' + $v.Major + '|' + $v.Minor + '|' + $v.Build + '|' + $v.Revision + '|' + $arch.ToString() + '|' + $runtimePsHome + '|' + $processPath)\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe timed out after {probeStopwatch.ElapsedMilliseconds} ms: {normalizedRuntimePath}");
                    TryKillProcess(process);
                    return RuntimeProbeResult.Failure(
                        normalizedRuntimePath,
                        discoverySource,
                        exists,
                        isWindowsAppsAlias,
                        fileVersion,
                        productVersion,
                        $"Launch timed out after {timeoutMilliseconds} ms.",
                        launchSucceeded: true,
                        timedOut: true);
                }

                var standardOutput = process.StandardOutput.ReadToEnd().Trim();
                var standardError = process.StandardError.ReadToEnd().Trim();
                var outputSummary = SummarizeText(standardOutput);
                var errorSummary = SummarizeText(standardError);

                if (process.ExitCode != 0)
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe exited with code {process.ExitCode} after {probeStopwatch.ElapsedMilliseconds} ms: {normalizedRuntimePath}");
                    return RuntimeProbeResult.Failure(
                        normalizedRuntimePath,
                        discoverySource,
                        exists,
                        isWindowsAppsAlias,
                        fileVersion,
                        productVersion,
                        $"Launch exited with code {process.ExitCode}.",
                        launchSucceeded: true,
                        exitCode: process.ExitCode,
                        stdoutSummary: outputSummary,
                        stderrSummary: errorSummary);
                }

                if (string.IsNullOrWhiteSpace(standardOutput))
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe returned no output after {probeStopwatch.ElapsedMilliseconds} ms: {normalizedRuntimePath}");
                    return RuntimeProbeResult.Failure(
                        normalizedRuntimePath,
                        discoverySource,
                        exists,
                        isWindowsAppsAlias,
                        fileVersion,
                        productVersion,
                        "Launch succeeded but returned no version output.",
                        launchSucceeded: true,
                        exitCode: process.ExitCode,
                        stdoutSummary: outputSummary,
                        stderrSummary: errorSummary);
                }

                if (!TryParseRuntimeProbe(
                        normalizedRuntimePath,
                        discoverySource,
                        standardOutput,
                        out var runtimeInfo,
                        out var failureReason,
                        out var resolvedExecutablePath,
                        out var psHome,
                        out var edition,
                        out var versionText,
                        out var architecture))
                {
                    StartupTimingLogger.Log("RuntimeService", $"Probe returned unusable output after {probeStopwatch.ElapsedMilliseconds} ms: {normalizedRuntimePath}. Reason={failureReason}");
                    return RuntimeProbeResult.Failure(
                        normalizedRuntimePath,
                        discoverySource,
                        exists,
                        isWindowsAppsAlias,
                        fileVersion,
                        productVersion,
                        failureReason,
                        launchSucceeded: true,
                        exitCode: process.ExitCode,
                        stdoutSummary: outputSummary,
                        stderrSummary: errorSummary,
                        edition: edition,
                        versionText: versionText,
                        architecture: architecture,
                        resolvedExecutablePath: resolvedExecutablePath,
                        psHome: psHome);
                }

                StartupTimingLogger.Log("RuntimeService", $"Probe succeeded in {probeStopwatch.ElapsedMilliseconds} ms: {runtimeInfo!.DisplayName} ({normalizedRuntimePath})");
                return RuntimeProbeResult.Success(
                    runtimeInfo,
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    process.ExitCode,
                    outputSummary,
                    errorSummary);
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("RuntimeService", $"Probe failed after {probeStopwatch.ElapsedMilliseconds} ms: {normalizedRuntimePath} -> {ex.GetType().Name}: {ex.Message}");
                return RuntimeProbeResult.Failure(
                    normalizedRuntimePath,
                    discoverySource,
                    exists,
                    isWindowsAppsAlias,
                    fileVersion,
                    productVersion,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryParseRuntimeProbe(
            string executablePath,
            string discoverySource,
            string output,
            out PowerShellRuntimeInfo? runtimeInfo,
            out string failureReason,
            out string resolvedExecutablePath,
            out string psHome,
            out string edition,
            out string versionText,
            out string architecture)
        {
            runtimeInfo = null;
            failureReason = string.Empty;
            resolvedExecutablePath = string.Empty;
            psHome = string.Empty;
            edition = string.Empty;
            versionText = string.Empty;
            architecture = string.Empty;

            var line = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(line))
            {
                failureReason = "Validation returned an empty output line.";
                return false;
            }

            var parts = line.Split('|');
            if (parts.Length < 9)
            {
                failureReason = "Validation output was missing required fields.";
                return false;
            }

            edition = parts[0].Trim();
            versionText = parts[1].Trim();
            resolvedExecutablePath = parts[8].Trim();
            psHome = parts[7].Trim();
            architecture = parts[6].Trim();

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

            if (!string.Equals(edition, "Core", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = $"Candidate returned PowerShell edition '{edition}', not PowerShell Core.";
                return false;
            }

            if (major < 7)
            {
                failureReason = $"Candidate returned PowerShell version {versionText}; PS7 or later is required.";
                return false;
            }

            runtimeInfo = CreateRuntimeInfo(
                executablePath,
                discoverySource,
                edition,
                versionText,
                architecture,
                major,
                minor,
                build,
                revision,
                isValidated: true,
                resolvedExecutablePath: resolvedExecutablePath,
                psHome: psHome,
                validationMessage: "Launch probe succeeded.");
            return true;
        }

        private static void TryReadVersionInfo(string executablePath, out string fileVersion, out string productVersion)
        {
            fileVersion = string.Empty;
            productVersion = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return;
                }

                var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                fileVersion = versionInfo.FileVersion ?? string.Empty;
                productVersion = versionInfo.ProductVersion ?? string.Empty;
            }
            catch
            {
                // Best-effort metadata only.
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

        private static bool IsUnqualifiedExecutableName(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var trimmed = executablePath.Trim();
            return string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.OrdinalIgnoreCase) &&
                   trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowsAppsAliasPath(string executablePath)
        {
            return executablePath.Contains(@"\AppData\Local\Microsoft\WindowsApps\pwsh.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string SummarizeText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text.Trim().Replace(Environment.NewLine, " ").Replace('\n', ' ').Replace('\r', ' ');
            return normalized.Length <= MaxProbePreviewLength
                ? normalized
                : normalized.Substring(0, MaxProbePreviewLength) + "...";
        }

        private static string NormalizeExecutablePath(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return string.Empty;
            }

            var trimmed = executablePath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            // Keep bare commands such as "pwsh.exe" as commands. Turning them into
            // CWD-relative paths breaks Microsoft Store App Execution Alias installs and
            // PATH-based resolution in packaged desktop apps.
            if (IsUnqualifiedExecutableName(trimmed))
            {
                return trimmed;
            }

            try
            {
                return Path.GetFullPath(trimmed).Trim();
            }
            catch
            {
                return trimmed;
            }
        }

        private static string NormalizeComparablePath(string? path)
        {
            return NormalizeExecutablePath(path).Trim();
        }

        private static string NormalizeDisplayToken(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
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
            int revision,
            bool isValidated,
            string resolvedExecutablePath,
            string psHome,
            string validationMessage)
        {
            var normalizedVersion = CreateVersion(major, minor, build, revision);
            var isPowerShell7OrLater = string.Equals(edition, "Core", StringComparison.OrdinalIgnoreCase) && major >= 7;
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
                isPreferred: false,
                isValidated,
                IsWindowsAppsAliasPath(executablePath),
                resolvedExecutablePath,
                psHome,
                validationMessage);
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
            if (runtime.IsPowerShell7OrLater && runtime.IsValidated && !runtime.IsWindowsAppsAlias)
            {
                return 400;
            }

            if (runtime.IsPowerShell7OrLater && runtime.IsValidated)
            {
                return 350;
            }

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
            if (preferredRuntime is not null)
            {
                return $"Runtime: {preferredRuntime.DisplayName} preferred ({detectedRuntimes.Count} validated)";
            }

            return "Runtime: PowerShell 7 was not found or could not be launched. Install PowerShell 7 or configure the pwsh.exe path.";
        }

        private static void LogRejectedCandidate(RuntimeDiscoveryCandidateInfo candidateInfo)
        {
            StartupTimingLogger.Log(
                "RuntimeService",
                $"Rejected runtime candidate '{candidateInfo.CandidatePath}' from {candidateInfo.Source}. " +
                $"Exists={candidateInfo.Exists}, WindowsAppsAlias={candidateInfo.IsWindowsAppsAlias}, ValidationAttempted={candidateInfo.ValidationAttempted}, " +
                $"LaunchSucceeded={candidateInfo.LaunchSucceeded}, TimedOut={candidateInfo.TimedOut}, ExitCode={candidateInfo.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}, " +
                $"Edition='{candidateInfo.Edition}', Version='{candidateInfo.VersionText}', Architecture='{candidateInfo.Architecture}', " +
                $"ResolvedPath='{candidateInfo.ResolvedExecutablePath}', PSHOME='{candidateInfo.PsHome}', " +
                $"FileVersion='{candidateInfo.FileVersion}', ProductVersion='{candidateInfo.ProductVersion}', " +
                $"Stdout='{candidateInfo.StdoutSummary}', Stderr='{candidateInfo.StderrSummary}', Reason={candidateInfo.FailureReason}");
            DeveloperDiagnostics.LogDecision(
                "Runtime",
                "RuntimeCandidateEvaluated",
                "Runtime candidate rejected during discovery or validation.",
                "Rejected",
                new Dictionary<string, object?>
                {
                    ["candidatePath"] = candidateInfo.CandidatePath,
                    ["source"] = candidateInfo.Source,
                    ["exists"] = candidateInfo.Exists,
                    ["isWindowsAppsAlias"] = candidateInfo.IsWindowsAppsAlias,
                    ["validationAttempted"] = candidateInfo.ValidationAttempted,
                    ["launchSucceeded"] = candidateInfo.LaunchSucceeded,
                    ["validationSucceeded"] = candidateInfo.ValidationSucceeded,
                    ["timedOut"] = candidateInfo.TimedOut,
                    ["exitCode"] = candidateInfo.ExitCode,
                    ["failureReason"] = candidateInfo.FailureReason,
                    ["version"] = candidateInfo.VersionText,
                    ["edition"] = candidateInfo.Edition,
                    ["architecture"] = candidateInfo.Architecture,
                    ["resolvedPath"] = candidateInfo.ResolvedExecutablePath,
                    ["psHome"] = candidateInfo.PsHome,
                    ["stdout"] = candidateInfo.StdoutSummary,
                    ["stderr"] = candidateInfo.StderrSummary,
                    ["fileVersion"] = candidateInfo.FileVersion,
                    ["productVersion"] = candidateInfo.ProductVersion
                });
        }

        private static void LogAcceptedCandidate(PowerShellRuntimeInfo runtimeInfo, RuntimeDiscoveryCandidateInfo candidateInfo)
        {
            StartupTimingLogger.Log(
                "RuntimeService",
                $"Accepted runtime candidate '{runtimeInfo.ExecutablePath}' from {candidateInfo.Source}. " +
                $"Version={runtimeInfo.VersionText}, Edition={runtimeInfo.Edition}, Architecture={runtimeInfo.Architecture}, " +
                $"ResolvedPath='{runtimeInfo.ResolvedExecutablePath}', PSHOME='{runtimeInfo.PsHome}', WindowsAppsAlias={runtimeInfo.IsWindowsAppsAlias}, " +
                $"FileVersion='{candidateInfo.FileVersion}', ProductVersion='{candidateInfo.ProductVersion}', Stdout='{candidateInfo.StdoutSummary}', Stderr='{candidateInfo.StderrSummary}'.");
            DeveloperDiagnostics.LogDecision(
                "Runtime",
                "RuntimeCandidateEvaluated",
                "Runtime candidate accepted during discovery or validation.",
                "Accepted",
                new Dictionary<string, object?>
                {
                    ["candidatePath"] = candidateInfo.CandidatePath,
                    ["source"] = candidateInfo.Source,
                    ["resolvedPath"] = runtimeInfo.ResolvedExecutablePath,
                    ["psHome"] = runtimeInfo.PsHome,
                    ["version"] = runtimeInfo.VersionText,
                    ["edition"] = runtimeInfo.Edition,
                    ["architecture"] = runtimeInfo.Architecture,
                    ["isWindowsAppsAlias"] = runtimeInfo.IsWindowsAppsAlias,
                    ["fileVersion"] = candidateInfo.FileVersion,
                    ["productVersion"] = candidateInfo.ProductVersion
                });
        }

        private sealed class RuntimeProbeResult
        {
            private RuntimeProbeResult(PowerShellRuntimeInfo? runtimeInfo, RuntimeDiscoveryCandidateInfo candidateInfo)
            {
                RuntimeInfo = runtimeInfo;
                CandidateInfo = candidateInfo;
            }

            public PowerShellRuntimeInfo? RuntimeInfo { get; }

            public RuntimeDiscoveryCandidateInfo CandidateInfo { get; }

            public static RuntimeProbeResult Success(
                PowerShellRuntimeInfo runtimeInfo,
                string candidatePath,
                string source,
                bool exists,
                bool isWindowsAppsAlias,
                string fileVersion,
                string productVersion,
                int? exitCode,
                string stdoutSummary,
                string stderrSummary)
            {
                return new RuntimeProbeResult(
                    runtimeInfo,
                    new RuntimeDiscoveryCandidateInfo(
                        candidatePath,
                        source,
                        exists,
                        isWindowsAppsAlias,
                        validationAttempted: true,
                        launchSucceeded: true,
                        validationSucceeded: true,
                        timedOut: false,
                        exitCode,
                        runtimeInfo.Edition,
                        runtimeInfo.VersionText,
                        runtimeInfo.Architecture,
                        runtimeInfo.ResolvedExecutablePath,
                        runtimeInfo.PsHome,
                        stdoutSummary,
                        stderrSummary,
                        fileVersion,
                        productVersion,
                        string.Empty));
            }

            public static RuntimeProbeResult MetadataSuccess(
                PowerShellRuntimeInfo runtimeInfo,
                string candidatePath,
                string source,
                bool exists,
                bool isWindowsAppsAlias,
                string fileVersion,
                string productVersion)
            {
                return new RuntimeProbeResult(
                    runtimeInfo,
                    new RuntimeDiscoveryCandidateInfo(
                        candidatePath,
                        source,
                        exists,
                        isWindowsAppsAlias,
                        validationAttempted: false,
                        launchSucceeded: false,
                        validationSucceeded: true,
                        timedOut: false,
                        exitCode: null,
                        runtimeInfo.Edition,
                        runtimeInfo.VersionText,
                        runtimeInfo.Architecture,
                        runtimeInfo.ResolvedExecutablePath,
                        runtimeInfo.PsHome,
                        "File metadata fast path",
                        string.Empty,
                        fileVersion,
                        productVersion,
                        string.Empty));
            }

            public static RuntimeProbeResult Failure(
                string candidatePath,
                string source,
                bool exists,
                bool isWindowsAppsAlias,
                string fileVersion,
                string productVersion,
                string failureReason,
                bool launchSucceeded = false,
                bool timedOut = false,
                int? exitCode = null,
                string edition = "",
                string versionText = "",
                string architecture = "",
                string resolvedExecutablePath = "",
                string psHome = "",
                string stdoutSummary = "",
                string stderrSummary = "")
            {
                return new RuntimeProbeResult(
                    null,
                    new RuntimeDiscoveryCandidateInfo(
                        candidatePath,
                        source,
                        exists,
                        isWindowsAppsAlias,
                        validationAttempted: exists,
                        launchSucceeded,
                        validationSucceeded: false,
                        timedOut,
                        exitCode,
                        edition,
                        versionText,
                        architecture,
                        resolvedExecutablePath,
                        psHome,
                        stdoutSummary,
                        stderrSummary,
                        fileVersion,
                        productVersion,
                        failureReason));
            }
        }
    }
}
