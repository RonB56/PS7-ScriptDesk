using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Shell.Editor
{
    internal sealed class MetadataInitialLoadDiagnostics
    {
        private const int MaxLogsToKeep = 20;
        private static readonly TimeSpan RuntimeProbeTimeout = TimeSpan.FromSeconds(4);
        private readonly object _syncRoot = new();
        private readonly Stopwatch _totalStopwatch = Stopwatch.StartNew();
        private readonly string _selectedRuntimePath;

        private bool _finalized;
        private bool _cacheLoaded;
        private bool _commandCatalogLoaded;
        private bool _parameterMetadataLoaded;
        private bool _backgroundRefreshRan;
        private bool _backgroundRefreshFailed;
        private string _finalMetadataStatus = "InProgress";
        private string _finalUiMetadataStatus = "NotUpdated";
        private string _failurePhase = string.Empty;
        private string _failureMessage = string.Empty;
        private string _cacheOutcome = "Unknown";
        private int _commandCount;
        private int _quickInfoCount;
        private int _parameterizedCommandCount;
        private int _totalParameterCount;
        private int _moduleCount;

        private MetadataInitialLoadDiagnostics(string logPath, string selectedRuntimePath)
        {
            LogPath = logPath;
            _selectedRuntimePath = selectedRuntimePath ?? string.Empty;
        }

        public string LogPath { get; }

        public static string MetadataLogDirectory => Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs", "Metadata");

        public static MetadataInitialLoadDiagnostics? TryCreate(PowerShellRuntimeInfo runtimeInfo)
        {
            if (runtimeInfo is null || string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath))
            {
                return null;
            }

            try
            {
                var logDirectory = MetadataLogDirectory;
                Directory.CreateDirectory(logDirectory);
                CleanupRetention(logDirectory);

                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-ffff", CultureInfo.InvariantCulture);
                var logPath = Path.Combine(logDirectory, $"metadata-initial-load-{timestamp}.log");
                var diagnostics = new MetadataInitialLoadDiagnostics(logPath, EditorMetadataCacheStore.NormalizeRuntimePath(runtimeInfo.LaunchExecutablePath));
                diagnostics.WriteHeader(runtimeInfo, logDirectory);
                AppLogger.Info("EditorMetadata", $"Metadata initial-load diagnostics: {logPath}");
                return diagnostics;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("EditorMetadata", $"Failed to create metadata initial-load diagnostics log. {ex.Message}");
                return null;
            }
        }

        public void RecordWarmupRequested(PowerShellRuntimeInfo runtimeInfo)
        {
            AppendSection("Startup metadata warmup requested");
            AppendLine($"Local timestamp: {DateTime.Now:O}");
            AppendLine($"UTC timestamp: {DateTime.UtcNow:O}");
            AppendLine($"Selected runtime path: {_selectedRuntimePath}");
            AppendLine($"Selected runtime display path: {runtimeInfo.ExecutablePath}");
            AppendLine($"Selected runtime launch path: {runtimeInfo.LaunchExecutablePath}");
            AppendLine($"Selected runtime launch path exists: {File.Exists(runtimeInfo.LaunchExecutablePath)}");
            AppendLine($"Selected runtime version: {runtimeInfo.VersionText ?? string.Empty}");
            AppendLine($"Selected runtime edition: {runtimeInfo.Edition ?? string.Empty}");
            AppendLine($"Selected runtime architecture: {runtimeInfo.Architecture ?? string.Empty}");
        }

        public void RecordCacheProbeCandidate(EditorMetadataCacheProbeCandidateInfo candidate)
        {
            if (candidate is null)
            {
                return;
            }

            AppendLine(
                $"Cache candidate: Directory='{candidate.CacheDirectory}', LegacyPathCache={candidate.IsLegacyPathCache}, DirectoryExists={candidate.DirectoryExists}, " +
                $"ManifestPath='{candidate.ManifestPath}', ManifestExists={candidate.ManifestExists}, ManifestSizeBytes={candidate.ManifestSizeBytes}, ManifestLastWriteUtc={FormatUtc(candidate.ManifestLastWriteUtc)}, " +
                $"SnapshotPath='{candidate.SnapshotPath}', SnapshotExists={candidate.SnapshotExists}, SnapshotSizeBytes={candidate.SnapshotSizeBytes}, SnapshotLastWriteUtc={FormatUtc(candidate.SnapshotLastWriteUtc)}.");

            if (candidate.Manifest is not null)
            {
                AppendLine(
                    $"Cache candidate manifest: SchemaVersion={candidate.Manifest.SchemaVersion}, RuntimeVersion='{candidate.Manifest.RuntimeVersion}', " +
                    $"Edition='{candidate.Manifest.PowerShellEdition}', Architecture='{candidate.Manifest.RuntimeArchitecture}', PSHOME='{candidate.Manifest.RuntimePsHome}', " +
                    $"CatalogCount={candidate.Manifest.CatalogCount}, QuickInfoCount={candidate.Manifest.QuickInfoCount}, " +
                    $"CreatedUtc={FormatUtcTicks(candidate.Manifest.CreatedUtcTicks)}, BuiltUtc={FormatUtcTicks(candidate.Manifest.BuiltUtcTicks)}.");
            }
        }

        public void RecordCacheDecision(
            bool loadedFromCache,
            string outcome,
            string detail,
            EditorMetadataCacheSnapshot? snapshot = null)
        {
            lock (_syncRoot)
            {
                _cacheLoaded = loadedFromCache;
                _cacheOutcome = string.IsNullOrWhiteSpace(outcome) ? "Unknown" : outcome.Trim();
                if (snapshot is not null)
                {
                    PopulateSnapshotCounts(snapshot);
                }
            }

            AppendLine($"Cache decision: LoadedFromCache={loadedFromCache}, Outcome={outcome}, Detail={detail}");
        }

        public void RecordUiStatus(EditorMetadataWarmupStatus status)
        {
            if (status is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                var readyDetail = status.IsCompletedSuccessfully && status.IsLoadedFromCache
                    ? " | Loaded from cache"
                    : string.Empty;
                _finalUiMetadataStatus =
                    $"{status.ReadinessCaption}{readyDetail} | Commands: {status.CommandCount:N0} | Quick info: {status.QuickInfoCount:N0} | Parameterized commands: {status.ParameterizedQuickInfoCount:N0}";
                _commandCount = Math.Max(_commandCount, status.CommandCount);
                _quickInfoCount = Math.Max(_quickInfoCount, status.QuickInfoCount);
                _parameterizedCommandCount = Math.Max(_parameterizedCommandCount, status.ParameterizedQuickInfoCount);
                if (status.HasCommandCatalog)
                {
                    _commandCatalogLoaded = true;
                }

                if (status.HasFullParameterMetadata)
                {
                    _parameterMetadataLoaded = true;
                }
            }

            AppendLine(
                $"UI status updated: Phase={status.Phase}, Reason={status.Reason}, Message='{status.Message}', Detail='{status.DetailText}', " +
                $"IsLoadedFromCache={status.IsLoadedFromCache}, CommandCount={status.CommandCount}, QuickInfoCount={status.QuickInfoCount}, " +
                $"ParameterizedQuickInfoCount={status.ParameterizedQuickInfoCount}, GetChildItemParameterCount={status.GetChildItemParameterCount}.");
        }

        public void RecordBackgroundRefreshStarted()
        {
            lock (_syncRoot)
            {
                _backgroundRefreshRan = true;
            }
        }

        public void RecordBackgroundRefreshFailure(string phase, string message)
        {
            lock (_syncRoot)
            {
                _backgroundRefreshFailed = true;
                if (string.IsNullOrWhiteSpace(_failurePhase))
                {
                    _failurePhase = phase ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(_failureMessage))
                {
                    _failureMessage = message ?? string.Empty;
                }
            }
        }

        public void RecordSnapshotCounts(EditorMetadataCacheSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                PopulateSnapshotCounts(snapshot);
                _commandCatalogLoaded = snapshot.Catalog.Commands.Count > 0;
                _parameterMetadataLoaded = snapshot.QuickInfos.Values.Any(item => item is not null && item.Parameters.Count > 0);
            }
        }

        public void RecordSuccess(string finalStatus, string cacheOutcome)
        {
            lock (_syncRoot)
            {
                _finalMetadataStatus = string.IsNullOrWhiteSpace(finalStatus) ? "Success" : finalStatus.Trim();
                if (!string.IsNullOrWhiteSpace(cacheOutcome))
                {
                    _cacheOutcome = cacheOutcome.Trim();
                }
            }
        }

        public void RecordFailure(string failurePhase, string failureMessage)
        {
            lock (_syncRoot)
            {
                _finalMetadataStatus = "Failure";
                _failurePhase = string.IsNullOrWhiteSpace(failurePhase) ? _failurePhase : failurePhase.Trim();
                _failureMessage = string.IsNullOrWhiteSpace(failureMessage) ? _failureMessage : failureMessage.Trim();
            }
        }

        public void AppendSection(string sectionName)
        {
            MetadataPerformanceLog.AppendSection(LogPath, sectionName);
        }

        public void AppendLine(string message)
        {
            MetadataPerformanceLog.AppendLine(LogPath, message);
        }

        public void RecordException(string phase, Exception exception, string? detail = null)
        {
            if (exception is null)
            {
                return;
            }

            RecordFailure(phase, exception.Message);
            AppendSection($"Exception: {phase}");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                AppendLine(detail);
            }

            var current = exception;
            var depth = 0;
            while (current is not null)
            {
                AppendLine($"Exception[{depth}] Type={current.GetType().FullName}, Message={current.Message}, HResult=0x{current.HResult:X8}");
                AppendLine($"Exception[{depth}] StackTrace={current.StackTrace ?? string.Empty}");
                current = current.InnerException;
                depth++;
            }
        }

        public void FinalizeSuccess(string finalStatus)
        {
            lock (_syncRoot)
            {
                if (_finalized)
                {
                    return;
                }

                _finalized = true;
                if (string.IsNullOrWhiteSpace(_finalMetadataStatus) || string.Equals(_finalMetadataStatus, "InProgress", StringComparison.OrdinalIgnoreCase))
                {
                    _finalMetadataStatus = string.IsNullOrWhiteSpace(finalStatus) ? "Success" : finalStatus.Trim();
                }
            }

            WriteFinalSummary();
        }

        public void FinalizeFailure(string failurePhase, string failureMessage)
        {
            RecordFailure(failurePhase, failureMessage);
            lock (_syncRoot)
            {
                if (_finalized)
                {
                    return;
                }

                _finalized = true;
            }

            WriteFinalSummary();
        }

        private void WriteHeader(PowerShellRuntimeInfo runtimeInfo, string logDirectory)
        {
            AppendLine("Metadata initial-load diagnostics started.");
            AppendLine($"Log path: {LogPath}");
            AppendLine($"Timestamp local: {DateTime.Now:O}");
            AppendLine($"Timestamp UTC: {DateTime.UtcNow:O}");
            AppendLine($"App name: {ApplicationBranding.PublicName}");
            AppendLine($"App internal name: {ApplicationBranding.InternalName}");
            AppendLine($"App version: {ResolveAppVersion()}");
            AppendLine($"Process ID: {Environment.ProcessId}");
            AppendLine($"Process path: {Environment.ProcessPath ?? string.Empty}");
            AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
            AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
            AppendLine($"OS description: {RuntimeInformation.OSDescription}");
            AppendLine($"OS version: {Environment.OSVersion}");
            AppendLine($"Current .NET runtime: {RuntimeInformation.FrameworkDescription}");
            AppendLine($"Current culture: {CultureInfo.CurrentCulture.Name}");
            AppendLine($"Current UI culture: {CultureInfo.CurrentUICulture.Name}");
            AppendLine($"Packaged detection: {DetectPackagingState()}");

            AppendSection("App-local paths");
            LogDirectoryState("LocalAppData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), createIfMissing: false);
            LogDirectoryState("PS7ScriptDesk root", ApplicationBranding.LocalApplicationDataRoot, createIfMissing: true);
            LogDirectoryState("Logs root", Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs"), createIfMissing: true);
            LogDirectoryState("Metadata logs root", logDirectory, createIfMissing: true);
            AppendLine($"AppLogger.CurrentLogDirectory: {AppLogger.CurrentLogDirectory}");
            LogDirectoryState("Metadata cache root", EditorMetadataCacheStore.GetCacheRootDirectory(), createIfMissing: true);
            LogDirectoryState("Metadata worker root", EditorMetadataBuilderHost.GetMetadataWorkerRootDirectory(), createIfMissing: true);
            LogDirectoryState("BackgroundPowerShell root", Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "BackgroundPowerShell"), createIfMissing: false);
            var packageInfo = DetectPackageInfo();
            AppendLine($"Package family name: {packageInfo.PackageFamilyName}");
            AppendLine($"Package LocalAppData path guess: {packageInfo.PackageLocalAppDataPath}");

            AppendSection("PowerShell discovery");
            AppendLine($"Selected pwsh.exe: {_selectedRuntimePath}");
            AppendLine($"Selected pwsh.exe is WindowsApps app execution alias: {_selectedRuntimePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)}");
            AppendLine($"Selected runtime version text: {runtimeInfo.VersionText ?? string.Empty}");
            AppendLine($"Selected runtime edition: {runtimeInfo.Edition ?? string.Empty}");
            AppendLine($"Selected runtime architecture: {runtimeInfo.Architecture ?? string.Empty}");
            AppendLine($"Selected runtime validated: {runtimeInfo.IsValidated}");
            AppendLine($"Selected runtime resolved executable path: {runtimeInfo.ResolvedExecutablePath}");
            AppendLine($"Selected runtime PSHOME: {runtimeInfo.PsHome}");
            AppendLine($"Selected runtime validation message: {runtimeInfo.ValidationMessage}");
            LogRuntimeFileVersion(runtimeInfo.LaunchExecutablePath);
            LogRuntimeDiscovery();
            LogSelectedRuntimePsVersionTable(runtimeInfo.LaunchExecutablePath);

            AppendSection("Background PowerShell environment");
            if (PowerShellBackgroundProcessEnvironment.TryBuildEnvironmentInfo("MetadataBuilder", runtimeInfo.LaunchExecutablePath, createDirectories: true, out var environmentInfo, out var environmentFailureReason))
            {
                AppendLine($"Environment purpose: {environmentInfo.Purpose}");
                AppendLine($"Environment LOCALAPPDATA: {environmentInfo.LocalAppData}");
                AppendLine($"Environment background root: {environmentInfo.BackgroundRoot}");
                AppendLine($"Environment HOME: {environmentInfo.HomePath}");
                AppendLine($"Environment USERPROFILE: {environmentInfo.UserProfilePath}");
                AppendLine($"Environment TEMP: {environmentInfo.TempPath}");
                AppendLine($"Environment TMP: {environmentInfo.TmpPath}");
                AppendLine($"Environment module root: {environmentInfo.ModuleRoot}");
                AppendLine($"Environment PSModuleAnalysisCachePath: {environmentInfo.PsModuleAnalysisCachePath}");
                AppendLine($"Environment PSModulePath contains Documents\\PowerShell: {environmentInfo.ModulePathContainsUserDocumentsPowerShell}");
                AppendLine($"Environment POWERSHELL_TELEMETRY_OPTOUT: {environmentInfo.PowerShellTelemetryOptOut}");
                AppendLine($"Environment POWERSHELL_CLI_TELEMETRY_OPTOUT: {environmentInfo.PowerShellCliTelemetryOptOut}");
                AppendLine($"Environment POWERSHELL_UPDATECHECK: {environmentInfo.PowerShellUpdateCheck}");
                for (var index = 0; index < environmentInfo.ModulePathEntries.Count; index++)
                {
                    AppendLine($"Environment PSModulePath[{index}]: {environmentInfo.ModulePathEntries[index]}");
                }
            }
            else
            {
                AppendLine($"Background environment preview failed: {environmentFailureReason}");
            }
        }

        private void WriteFinalSummary()
        {
            var outcome = ResolveOutcomeLabel();
            AppendSection("Metadata run summary");
            AppendLine($"Final metadata status: {_finalMetadataStatus}");
            AppendLine($"Success / partial success / failure: {outcome}");
            AppendLine($"Did command catalog load: {_commandCatalogLoaded}");
            AppendLine($"Did parameter metadata load: {_parameterMetadataLoaded}");
            AppendLine($"Did cache load: {_cacheLoaded}");
            AppendLine($"Did background refresh run: {_backgroundRefreshRan}");
            AppendLine($"Did background refresh fail: {_backgroundRefreshFailed}");
            AppendLine($"Support log recommended: {(string.Equals(outcome, "Success", StringComparison.OrdinalIgnoreCase) ? "No" : "Yes")}");
            AppendLine($"Total duration ms: {_totalStopwatch.ElapsedMilliseconds:N0}");
            AppendLine($"Cache used or rebuilt: {_cacheOutcome}");
            AppendLine($"Command count: {_commandCount:N0}");
            AppendLine($"Quick-info count: {_quickInfoCount:N0}");
            AppendLine($"Parameterized command count: {_parameterizedCommandCount:N0}");
            AppendLine($"Total parameter count: {_totalParameterCount:N0}");
            AppendLine($"Module count: {_moduleCount:N0}");
            AppendLine($"Final UI metadata status: {_finalUiMetadataStatus}");

            if (!string.Equals(outcome, "Success", StringComparison.OrdinalIgnoreCase))
            {
                AppendSection("Failure summary");
                AppendLine($"Most likely failure phase: {_failurePhase}");
                AppendLine($"Most important exception/error message: {_failureMessage}");
                AppendLine("Support log recommended: Yes");
            }
        }

        private void LogDirectoryState(string label, string path, bool createIfMissing)
        {
            try
            {
                var existsBefore = Directory.Exists(path);
                var createResult = "NotAttempted";
                if (createIfMissing && !existsBefore)
                {
                    Directory.CreateDirectory(path);
                    createResult = Directory.Exists(path) ? "Succeeded" : "Unknown";
                }

                AppendLine($"{label}: Path='{path}', ExistsBefore={existsBefore}, ExistsAfter={Directory.Exists(path)}, CreateAttempted={createIfMissing}, CreateResult={createResult}");
            }
            catch (Exception ex)
            {
                AppendLine($"{label}: Path='{path}', CreateAttempted={createIfMissing}, CreateResult=Failed, Error={ex.GetType().Name}: {ex.Message}");
            }
        }

        private void LogRuntimeFileVersion(string runtimePath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(runtimePath);
                AppendLine($"Selected pwsh.exe file version: {versionInfo.FileVersion ?? string.Empty}");
                AppendLine($"Selected pwsh.exe product version: {versionInfo.ProductVersion ?? string.Empty}");
            }
            catch (Exception ex)
            {
                AppendLine($"Selected pwsh.exe version metadata could not be read: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void LogRuntimeDiscovery()
        {
            try
            {
                var runtimeService = new RuntimeService();
                var discovery = runtimeService.DiscoverRuntimes();
                AppendLine($"Runtime discovery summary: {discovery.SummaryText}");
                AppendLine($"Runtime discovery candidate count: {discovery.CandidateResults.Count}");
                foreach (var candidate in discovery.CandidateResults)
                {
                    AppendLine(
                        $"Runtime candidate: Path='{candidate.CandidatePath}', Source='{candidate.Source}', Exists={candidate.Exists}, " +
                        $"WindowsAppsAlias={candidate.IsWindowsAppsAlias}, ValidationAttempted={candidate.ValidationAttempted}, LaunchSucceeded={candidate.LaunchSucceeded}, " +
                        $"ValidationSucceeded={candidate.ValidationSucceeded}, TimedOut={candidate.TimedOut}, ExitCode={candidate.ExitCode?.ToString() ?? string.Empty}, " +
                        $"Edition='{candidate.Edition}', Version='{candidate.VersionText}', Architecture='{candidate.Architecture}', " +
                        $"ResolvedExecutablePath='{candidate.ResolvedExecutablePath}', PSHOME='{candidate.PsHome}', FileVersion='{candidate.FileVersion}', " +
                        $"ProductVersion='{candidate.ProductVersion}', StdoutSummary='{candidate.StdoutSummary}', StderrSummary='{candidate.StderrSummary}', " +
                        $"FailureReason='{candidate.FailureReason}'");
                }

                foreach (var runtime in discovery.DetectedRuntimes)
                {
                    AppendLine(
                        $"Discovered runtime: Preferred={runtime.IsPreferred}, Path='{runtime.ExecutablePath}', DisplayName='{runtime.DisplayName}', " +
                        $"Version='{runtime.VersionText}', Edition='{runtime.Edition}', Architecture='{runtime.Architecture}', Source='{runtime.DiscoverySource}', " +
                        $"IsPowerShell7OrLater={runtime.IsPowerShell7OrLater}, IsWindowsPowerShell={runtime.IsWindowsPowerShell}, IsValidated={runtime.IsValidated}, " +
                        $"ResolvedExecutablePath='{runtime.ResolvedExecutablePath}', PSHOME='{runtime.PsHome}', ValidationMessage='{runtime.ValidationMessage}'");
                }

                AppendLine($"Preferred runtime resolved during discovery: '{discovery.PreferredRuntime?.ExecutablePath ?? string.Empty}'");
                AppendLine($"Validated PowerShell 7 runtime count: {discovery.DetectedRuntimes.Count}");
            }
            catch (Exception ex)
            {
                AppendLine($"Runtime discovery logging failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void LogSelectedRuntimePsVersionTable(string runtimePath)
        {
            if (string.IsNullOrWhiteSpace(runtimePath) || !File.Exists(runtimePath))
            {
                AppendLine("Selected pwsh.exe PSVersionTable probe skipped because the runtime path was unavailable.");
                return;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = runtimePath,
                        Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"$t = $PSVersionTable; [Console]::Out.WriteLine(('PSVersion={0};PSEdition={1};GitCommitId={2};Platform={3};OS={4};PSCompatibleVersions={5};PSHOME={6};ProcessPath={7}' -f $t.PSVersion, $t.PSEdition, $t.GitCommitId, $t.Platform, $t.OS, ($t.PSCompatibleVersions -join ','), $PSHOME, [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName))\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    }
                };

                var startedAtUtc = DateTimeOffset.UtcNow;
                process.Start();
                if (!process.WaitForExit((int)RuntimeProbeTimeout.TotalMilliseconds))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    AppendLine($"Selected pwsh.exe PSVersionTable probe timed out after {RuntimeProbeTimeout.TotalMilliseconds:N0} ms.");
                    return;
                }

                var standardOutput = process.StandardOutput.ReadToEnd().Trim();
                var standardError = process.StandardError.ReadToEnd().Trim();
                AppendLine($"Selected pwsh.exe PSVersionTable probe started UTC: {startedAtUtc:O}");
                AppendLine($"Selected pwsh.exe PSVersionTable probe exit code: {process.ExitCode}");
                AppendLine($"Selected pwsh.exe alias launch result: {(process.ExitCode == 0 && !string.IsNullOrWhiteSpace(standardOutput) ? "Succeeded" : process.ExitCode == 0 ? "NoOutput" : "Failed")}");
                AppendLine($"Selected pwsh.exe PSVersionTable output: {standardOutput}");
                var realProcessPath = ExtractProbeField(standardOutput, "ProcessPath");
                var psHome = ExtractProbeField(standardOutput, "PSHOME");
                AppendLine($"Selected pwsh.exe resolved process path: {realProcessPath}");
                AppendLine($"Selected pwsh.exe resolved PSHOME: {psHome}");
                AppendLine($"Selected pwsh.exe resolved real target path available: {!string.IsNullOrWhiteSpace(realProcessPath)}");
                AppendLine($"Selected pwsh.exe resolved module/home path under Program Files\\WindowsApps: {ContainsWindowsApps(psHome) || ContainsWindowsApps(realProcessPath)}");
                if (!string.IsNullOrWhiteSpace(standardError))
                {
                    AppendLine($"Selected pwsh.exe PSVersionTable stderr: {standardError}");
                }
            }
            catch (Exception ex)
            {
                AppendLine($"Selected pwsh.exe PSVersionTable probe failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void PopulateSnapshotCounts(EditorMetadataCacheSnapshot snapshot)
        {
            _commandCount = Math.Max(_commandCount, snapshot.Catalog.Commands.Count);
            _quickInfoCount = Math.Max(_quickInfoCount, snapshot.QuickInfos.Count);
            _parameterizedCommandCount = Math.Max(_parameterizedCommandCount, snapshot.QuickInfos.Values.Count(item => item is not null && item.Parameters.Count > 0));
            _totalParameterCount = Math.Max(_totalParameterCount, snapshot.QuickInfos.Values.Sum(item => item?.Parameters.Count ?? 0));
            _moduleCount = Math.Max(_moduleCount, snapshot.Catalog.Commands
                .Select(item => item.ModuleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        }

        private string ResolveOutcomeLabel()
        {
            if (string.Equals(_finalMetadataStatus, "Failure", StringComparison.OrdinalIgnoreCase))
            {
                return "Failure";
            }

            if (_backgroundRefreshFailed || (!_parameterMetadataLoaded && _cacheLoaded))
            {
                return "PartialSuccess";
            }

            return "Success";
        }

        private static string ResolveAppVersion()
        {
            try
            {
                var entryAssembly = Assembly.GetEntryAssembly();
                var informationalVersion = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informationalVersion))
                {
                    return informationalVersion!;
                }

                return entryAssembly?.GetName().Version?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DetectPackagingState()
        {
            var processPath = Environment.ProcessPath ?? string.Empty;
            var baseDirectory = AppContext.BaseDirectory ?? string.Empty;
            var packageInfo = DetectPackageInfo();
            if (packageInfo.IsPackaged ||
                processPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                baseDirectory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return "LikelyPackaged";
            }

            return "LikelyUnpackaged";
        }

        private static void CleanupRetention(string logDirectory)
        {
            try
            {
                var files = new DirectoryInfo(logDirectory)
                    .EnumerateFiles("metadata-initial-load-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();

                foreach (var staleFile in files.Skip(MaxLogsToKeep))
                {
                    try
                    {
                        staleFile.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static (bool IsPackaged, string PackageFamilyName, string PackageLocalAppDataPath) DetectPackageInfo()
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var packageFamilyFromPath = ExtractPackageFamilyName(localAppData);
                var packageFamilyFromRuntime = TryGetPackageFamilyNameFromRuntime();
                var packageFamily = !string.IsNullOrWhiteSpace(packageFamilyFromRuntime)
                    ? packageFamilyFromRuntime
                    : packageFamilyFromPath;
                var packageLocalAppDataPath = string.IsNullOrWhiteSpace(packageFamily)
                    ? string.Empty
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", packageFamily, "LocalCache", ApplicationBranding.InternalName);
                return (!string.IsNullOrWhiteSpace(packageFamilyFromRuntime) || !string.IsNullOrWhiteSpace(packageFamilyFromPath), packageFamily ?? string.Empty, packageLocalAppDataPath);
            }
            catch
            {
                return (false, string.Empty, string.Empty);
            }
        }

        private static string ExtractPackageFamilyName(string localAppData)
        {
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return string.Empty;
            }

            var marker = $"{Path.DirectorySeparatorChar}Packages{Path.DirectorySeparatorChar}";
            var index = localAppData.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return string.Empty;
            }

            var remainder = localAppData[(index + marker.Length)..];
            var separatorIndex = remainder.IndexOf(Path.DirectorySeparatorChar);
            return separatorIndex >= 0 ? remainder[..separatorIndex] : remainder;
        }

        private static string TryGetPackageFamilyNameFromRuntime()
        {
            try
            {
                var packageType = Type.GetType("Windows.ApplicationModel.Package, Windows, ContentType=WindowsRuntime");
                var currentProperty = packageType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                var currentPackage = currentProperty?.GetValue(null);
                var idProperty = currentPackage?.GetType().GetProperty("Id");
                var packageId = idProperty?.GetValue(currentPackage);
                var familyNameProperty = packageId?.GetType().GetProperty("FamilyName");
                return familyNameProperty?.GetValue(packageId)?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractProbeField(string output, string name)
        {
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var marker = name + "=";
            var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return string.Empty;
            }

            index += marker.Length;
            var endIndex = output.IndexOf(';', index);
            return endIndex >= 0 ? output[index..endIndex] : output[index..];
        }

        private static bool ContainsWindowsApps(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatUtc(DateTime? value)
        {
            return value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string FormatUtcTicks(long ticks)
        {
            if (ticks <= 0)
            {
                return string.Empty;
            }

            try
            {
                return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
