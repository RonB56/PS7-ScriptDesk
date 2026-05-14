using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using PowerShellStudio.Application.Diagnostics;
using Microsoft.Win32;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.PowerShell.Services
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
            var discoveryStopwatch = Stopwatch.StartNew();
            StartupTimingLogger.Log("RuntimeService", "DiscoverRuntimes started.");
            DeveloperDiagnostics.LogOperationStart("Runtime", "DiscoverRuntimes", "PowerShell runtime discovery started.");

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

            StartupTimingLogger.Log("RuntimeService", $"DiscoverRuntimes completed in {discoveryStopwatch.ElapsedMilliseconds} ms with {finalizedRuntimes.Count} validated runtime(s).");
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
                    ["preferredRuntimeVersion"] = finalizedPreferredRuntime?.VersionText
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
            return new PowerShellRuntimeInfo(
                probeResult.RuntimeInfo.DisplayName,
                probeResult.RuntimeInfo.Edition,
                probeResult.RuntimeInfo.VersionText,
                probeResult.RuntimeInfo.Version,
                probeResult.RuntimeInfo.Architecture,
                probeResult.RuntimeInfo.LaunchExecutablePath,
                probeResult.RuntimeInfo.DiscoverySource,
                probeResult.RuntimeInfo.IsPowerShell7OrLater,
                probeResult.RuntimeInfo.IsWindowsPowerShell,
                isPreferred: probeResult.RuntimeInfo.IsPowerShell7OrLater && probeResult.RuntimeInfo.IsValidated,
                probeResult.RuntimeInfo.IsValidated,
                probeResult.RuntimeInfo.IsWindowsAppsAlias,
                probeResult.RuntimeInfo.ResolvedExecutablePath,
                probeResult.RuntimeInfo.PsHome,
                probeResult.RuntimeInfo.ValidationMessage);
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
                        Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$v=$PSVersionTable.PSVersion; $edition=$PSVersionTable.PSEdition; $arch=[System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture; $psHome=$PSHOME; try { $processPath=[System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName } catch { $processPath='' }; [Console]::Out.WriteLine($edition + '|' + $v.ToString() + '|' + $v.Major + '|' + $v.Minor + '|' + $v.Build + '|' + $v.Revision + '|' + $arch.ToString() + '|' + $psHome + '|' + $processPath)\"",
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
