using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Provides PowerShell engine-backed IntelliSense completions by keeping a persistent
    /// background pwsh.exe session alive and dispatching
    /// <c>[System.Management.Automation.CommandCompletion]::CompleteInput</c> requests
    /// through it. Uses the same marker/base64-payload protocol as
    /// <see cref="PowerShellDiagnosticsService"/> for reliable output extraction.
    ///
    /// Editor metadata warmup now uses a two-tier design:
    /// 1. Load a persisted binary snapshot immediately when one exists for the selected runtime.
    /// 2. Refresh or build a new full snapshot in a separate background helper process so the UI
    ///    stays responsive and live IntelliSense never waits for the metadata crawler.
    /// </summary>
    public sealed class PowerShellCompletionService : IDisposable
    {
        private const int MaxQuickInfoCacheEntries = 10000;
        private const int MaxCommandCatalogEntries = 12000;

        private readonly object _syncRoot = new();
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly StringBuilder _sharedOutputTail = new();
        private readonly Dictionary<string, CachedQuickInfo> _quickInfoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CachedCommandCatalog> _commandCatalogCache = new(StringComparer.OrdinalIgnoreCase);

        private Process? _process;
        private StreamWriter? _stdin;
        private CancellationTokenSource? _processCancellationTokenSource;
        private Task? _stdoutReaderTask;
        private Task? _stderrReaderTask;
        private TaskCompletionSource<bool>? _readyCompletionSource;
        private ActiveRequest? _activeRequest;
        private string? _readyMarker;
        private string? _activeRuntimePath;
        private Process? _metadataBuilderProcess;
        private CancellationTokenSource? _metadataBuilderCancellationTokenSource;
        private Task? _metadataBuilderStdoutReaderTask;
        private Task? _metadataBuilderStderrReaderTask;
        private string? _metadataBuilderRuntimePath;
        private MetadataInitialLoadDiagnostics? _activeMetadataInitialLoadDiagnostics;
        private string? _loadedMetadataRuntimePath;
        private EditorMetadataSnapshotHealth _loadedMetadataHealth = EditorMetadataSnapshotHealth.Empty;
        private bool _loadedPersistedMetadataForRuntime;
        private bool _disposed;

        public event EventHandler<EditorMetadataWarmupStatusChangedEventArgs>? MetadataWarmupStatusChanged;

        public async Task<CompletionServiceResult> GetCompletionsAsync(
            string scriptText,
            int cursorOffset,
            string pwshExecutablePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pwshExecutablePath))
                return CompletionServiceResult.Empty;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var payload = await ExecuteTransportRequestAsync(
                    pwshExecutablePath,
                    request => BuildCompletionCommand(scriptText, cursorOffset, request.StartMarker, request.EndMarker),
                    TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                return ParsePayload(payload, cursorOffset, scriptText);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                AppLogger.Debug("EditorCompletion", $"Completion request canceled after {stopwatch.ElapsedMilliseconds:N0} ms.");
                return CompletionServiceResult.Empty;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                AppLogger.Warning("EditorCompletion", $"Completion request failed after {stopwatch.ElapsedMilliseconds:N0} ms: {ex.Message}");
                return CompletionServiceResult.Empty;
            }
        }

        public async Task<PowerShellQuickInfo?> GetCommandQuickInfoAsync(
            string commandName,
            string pwshExecutablePath,
            bool requireParameters = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandName) ||
                string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                return null;
            }

            var normalizedCommandName = commandName.Trim();
            var cacheKey = BuildQuickInfoCacheKey(pwshExecutablePath, normalizedCommandName);
            if (TryGetCachedQuickInfo(cacheKey, out var cachedQuickInfo))
            {
                if (!requireParameters || HasUsableParameterMetadata(cachedQuickInfo))
                {
                    return cachedQuickInfo;
                }

                AppLogger.Debug(
                    "EditorCompletion",
                    $"Cached quick-info for '{normalizedCommandName}' did not include parameter metadata. Bypassing the cached entry and requesting fresh command metadata.");
            }

            var fetchCommandName = normalizedCommandName;
            if (TryResolveCachedCommandReference(pwshExecutablePath, normalizedCommandName, out var commandReference) &&
                commandReference is not null &&
                commandReference.IsAlias &&
                !string.IsNullOrWhiteSpace(commandReference.ResolvedCommandName))
            {
                var resolvedCacheKey = BuildQuickInfoCacheKey(pwshExecutablePath, commandReference.ResolvedCommandName);
                if (TryGetCachedQuickInfo(resolvedCacheKey, out var resolvedQuickInfo) && resolvedQuickInfo is not null)
                {
                    if (!requireParameters || HasUsableParameterMetadata(resolvedQuickInfo))
                    {
                        AddQuickInfoToCache(cacheKey, resolvedQuickInfo);
                        return resolvedQuickInfo;
                    }

                    AppLogger.Debug(
                        "EditorCompletion",
                        $"Cached quick-info for alias target '{commandReference.ResolvedCommandName}' did not include parameter metadata. Requesting fresh command metadata for '{normalizedCommandName}'.");
                }

                fetchCommandName = commandReference.ResolvedCommandName;
            }

            var quickInfo = await FetchCommandQuickInfoAsync(
                    fetchCommandName,
                    pwshExecutablePath,
                    includeHelp: true,
                    timeout: TimeSpan.FromSeconds(4),
                    cancellationToken)
                .ConfigureAwait(false);

            if (quickInfo is not null)
            {
                AddQuickInfoToCache(cacheKey, quickInfo);
                if (!string.Equals(fetchCommandName, normalizedCommandName, StringComparison.OrdinalIgnoreCase))
                {
                    AddQuickInfoToCache(BuildQuickInfoCacheKey(pwshExecutablePath, fetchCommandName), quickInfo);
                }
            }

            return quickInfo;
        }

        public bool TryGetCachedCommandQuickInfo(
            string? pwshExecutablePath,
            string? commandName,
            out PowerShellQuickInfo? quickInfo)
        {
            quickInfo = null;
            if (string.IsNullOrWhiteSpace(pwshExecutablePath) || string.IsNullOrWhiteSpace(commandName))
            {
                return false;
            }

            var normalizedCommandName = commandName.Trim();
            if (TryGetCachedQuickInfo(BuildQuickInfoCacheKey(pwshExecutablePath, normalizedCommandName), out quickInfo))
            {
                return true;
            }

            if (TryResolveCachedCommandReference(pwshExecutablePath, normalizedCommandName, out var commandReference) &&
                commandReference is not null &&
                commandReference.IsAlias &&
                !string.IsNullOrWhiteSpace(commandReference.ResolvedCommandName) &&
                TryGetCachedQuickInfo(BuildQuickInfoCacheKey(pwshExecutablePath, commandReference.ResolvedCommandName), out quickInfo) &&
                quickInfo is not null)
            {
                AddQuickInfoToCache(BuildQuickInfoCacheKey(pwshExecutablePath, normalizedCommandName), quickInfo);
                return true;
            }

            quickInfo = null;
            return false;
        }

        public bool TryGetCachedCommandReference(
            string? pwshExecutablePath,
            string? commandName,
            out PowerShellCommandReference? commandReference)
        {
            commandReference = null;
            if (string.IsNullOrWhiteSpace(pwshExecutablePath) || string.IsNullOrWhiteSpace(commandName))
            {
                return false;
            }

            return TryResolveCachedCommandReference(pwshExecutablePath, commandName.Trim(), out commandReference);
        }

        public IReadOnlyList<PowerShellCommandReference> GetCachedCommandReferences(string? pwshExecutablePath)
        {
            if (string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                return Array.Empty<PowerShellCommandReference>();
            }

            var cacheKey = NormalizePath(pwshExecutablePath);
            lock (_syncRoot)
            {
                if (_commandCatalogCache.TryGetValue(cacheKey, out var cachedCatalog) &&
                    DateTimeOffset.UtcNow - cachedCatalog.CachedAt <= TimeSpan.FromHours(1))
                {
                    return cachedCatalog.Catalog.Commands;
                }
            }

            return Array.Empty<PowerShellCommandReference>();
        }

        public void StartMetadataWarmup(PowerShellRuntimeInfo? runtimeInfo)
        {
            RequestMetadataWarmup(runtimeInfo, forceRebuild: false, isUserInitiated: false);
        }

        public void RefreshMetadata(PowerShellRuntimeInfo? runtimeInfo)
        {
            RequestMetadataWarmup(runtimeInfo, forceRebuild: true, isUserInitiated: true);
        }

        private void RequestMetadataWarmup(PowerShellRuntimeInfo? runtimeInfo, bool forceRebuild, bool isUserInitiated)
        {
            if (runtimeInfo is null || string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath))
            {
                return;
            }

            if (!runtimeInfo.IsPowerShell7OrLater || !runtimeInfo.IsValidated)
            {
                var invalidRuntimeDetail = "PowerShell 7 was not found or could not be launched. Install PowerShell 7 or configure the pwsh.exe path.";
                var invalidRuntimeDiagnostics = !forceRebuild && !isUserInitiated
                    ? MetadataInitialLoadDiagnostics.TryCreate(runtimeInfo)
                    : null;
                invalidRuntimeDiagnostics?.RecordWarmupRequested(runtimeInfo);
                invalidRuntimeDiagnostics?.RecordFailure("Startup metadata warmup requested", invalidRuntimeDetail);
                invalidRuntimeDiagnostics?.FinalizeFailure("Startup metadata warmup requested", invalidRuntimeDetail);
                RaiseMetadataWarmupStatus(
                    new EditorMetadataWarmupStatus(
                        EditorMetadataWarmupPhase.Failed,
                        "Editor metadata failed; see log",
                        NormalizePath(runtimeInfo.LaunchExecutablePath),
                        detailText: invalidRuntimeDiagnostics is null
                            ? invalidRuntimeDetail
                            : AppendMetadataLogSupportHint(invalidRuntimeDetail, invalidRuntimeDiagnostics.LogPath),
                        reason: forceRebuild
                            ? EditorMetadataWarmupReason.ManualRefresh
                            : EditorMetadataWarmupReason.FirstRunBuild));
                AppLogger.Warning(
                    "EditorMetadata",
                    $"Metadata warmup rejected runtime DisplayPath='{runtimeInfo.ExecutablePath}', LaunchPath='{runtimeInfo.LaunchExecutablePath}'. Version={runtimeInfo.VersionText}, Edition={runtimeInfo.Edition}, " +
                    $"Validated={runtimeInfo.IsValidated}, IsPowerShell7OrLater={runtimeInfo.IsPowerShell7OrLater}.");
                return;
            }

            var normalizedRuntimePath = NormalizePath(runtimeInfo.LaunchExecutablePath);
            var startupDiagnostics = !forceRebuild && !isUserInitiated
                ? MetadataInitialLoadDiagnostics.TryCreate(runtimeInfo)
                : null;
            startupDiagnostics?.RecordWarmupRequested(runtimeInfo);
            if (startupDiagnostics is not null)
            {
                lock (_syncRoot)
                {
                    _activeMetadataInitialLoadDiagnostics = startupDiagnostics;
                }
            }

            var performanceLogPath = startupDiagnostics?.LogPath ?? MetadataPerformanceLog.CreateLogFile(
                normalizedRuntimePath,
                forceRebuild ? "Manual metadata refresh" : "Metadata warmup");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Request received. ForceRebuild={forceRebuild}, UserInitiated={isUserInitiated}.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShell version: {runtimeInfo.VersionText ?? string.Empty}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShell edition: {runtimeInfo.Edition ?? string.Empty}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Runtime architecture: {runtimeInfo.Architecture ?? string.Empty}");
            AppLogger.Info(
                "EditorMetadata",
                $"{(forceRebuild ? "Metadata refresh" : "Metadata warmup")} requested for runtime '{normalizedRuntimePath}'. Version={runtimeInfo.VersionText}, Edition={runtimeInfo.Edition}, Architecture={runtimeInfo.Architecture}.");

            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (!forceRebuild &&
                    _loadedPersistedMetadataForRuntime &&
                    string.Equals(_loadedMetadataRuntimePath, normalizedRuntimePath, StringComparison.OrdinalIgnoreCase) &&
                    _metadataBuilderProcess is null)
                {
                    AppLogger.Info("EditorMetadata", $"Skipping metadata rebuild for runtime '{normalizedRuntimePath}' because a healthy full cache is already loaded.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, "Cache hit decision: healthy full cache is already loaded in memory; metadata rebuild skipped.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh finished UTC: {DateTime.UtcNow:O}");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Output path for this performance log: {performanceLogPath}");
                    startupDiagnostics?.RecordSuccess("Success", "AlreadyLoadedInMemory");
                    startupDiagnostics?.FinalizeSuccess("Success");
                    ClearActiveMetadataInitialLoadDiagnostics(startupDiagnostics);
                    return;
                }

                if (_metadataBuilderProcess is not null &&
                    !_metadataBuilderProcess.HasExited &&
                    string.Equals(_metadataBuilderRuntimePath, normalizedRuntimePath, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info("EditorMetadata", $"Metadata builder is already running for runtime '{normalizedRuntimePath}'.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, "Metadata builder launch skipped because a builder process is already running for this runtime.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh finished UTC: {DateTime.UtcNow:O}");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Output path for this performance log: {performanceLogPath}");
                    startupDiagnostics?.RecordSuccess("PartialSuccess", "BuilderAlreadyRunning");
                    startupDiagnostics?.FinalizeSuccess("PartialSuccess");
                    ClearActiveMetadataInitialLoadDiagnostics(startupDiagnostics);
                    return;
                }
            }

            StopMetadataBuilderProcess();

            if (!forceRebuild)
            {
                RaiseMetadataWarmupStatus(
                    new EditorMetadataWarmupStatus(
                        EditorMetadataWarmupPhase.Scheduled,
                        "Loading cached editor metadata",
                        normalizedRuntimePath,
                        detailText: "Checking whether a saved full metadata cache can be reused for this PowerShell runtime.",
                        isLoadedFromCache: true,
                        reason: EditorMetadataWarmupReason.CachedLoad));
            }

            var loadedFromCache = TryLoadPersistedMetadataSnapshot(runtimeInfo, out var snapshot, out var manifest, out var loadReason, performanceLogPath, startupDiagnostics);
            AppLogger.Info("EditorMetadata", loadReason);
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache decision: {(loadedFromCache ? "hit" : "miss/rebuild required")}. Reason: {loadReason}");

            if (loadedFromCache && snapshot is not null && manifest is not null)
            {
                var loadedCount = snapshot.QuickInfos.Count > 0 ? snapshot.QuickInfos.Count : snapshot.Catalog.Commands.Count;
                var metadataHealth = BuildMetadataCacheHealth(snapshot);
                RaiseMetadataWarmupStatus(
                    new EditorMetadataWarmupStatus(
                        EditorMetadataWarmupPhase.Completed,
                        "Editor metadata ready",
                        normalizedRuntimePath,
                        loadedCount,
                        loadedCount,
                        $"Loaded from cache. Commands: {metadataHealth.CommandCount:N0}. Quick info: {metadataHealth.QuickInfoCount:N0}. Parameterized commands: {metadataHealth.ParameterizedQuickInfoCount:N0}. Total parameters are available from the cached snapshot. Created UTC: {GetManifestCreationUtcText(manifest)}.",
                        commandCount: metadataHealth.CommandCount,
                        quickInfoCount: metadataHealth.QuickInfoCount,
                        parameterizedQuickInfoCount: metadataHealth.ParameterizedQuickInfoCount,
                        getChildItemParameterCount: metadataHealth.GetChildItemParameterCount,
                        isLoadedFromCache: true,
                        reason: EditorMetadataWarmupReason.CachedLoad));

                if (!forceRebuild)
                {
                    AppLogger.Info("EditorMetadata", $"Using cached full editor metadata for runtime '{normalizedRuntimePath}'.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, "Metadata load result: cache load only; full rebuild was not started.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh finished UTC: {DateTime.UtcNow:O}");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Output path for this performance log: {performanceLogPath}");
                    startupDiagnostics?.RecordSnapshotCounts(snapshot);
                    startupDiagnostics?.RecordSuccess("Success", "LoadedFromCache");
                    startupDiagnostics?.FinalizeSuccess("Success");
                    ClearActiveMetadataInitialLoadDiagnostics(startupDiagnostics);
                    return;
                }
            }

            var cachedHealth = loadedFromCache && snapshot is not null
                ? BuildMetadataCacheHealth(snapshot)
                : EditorMetadataSnapshotHealth.Empty;
            var warmupReason = forceRebuild
                ? EditorMetadataWarmupReason.ManualRefresh
                : string.IsNullOrWhiteSpace(loadReason)
                    ? EditorMetadataWarmupReason.FirstRunBuild
                    : EditorMetadataWarmupReason.CacheRebuild;

            var detailText = forceRebuild
                ? loadedFromCache
                    ? "PS7 ScriptDesk is refreshing the saved full editor metadata snapshot in the background."
                    : "PS7 ScriptDesk is building a full editor metadata snapshot because no healthy cache is currently available."
                : string.IsNullOrWhiteSpace(loadReason)
                    ? "PS7 ScriptDesk is building the first-run editor metadata cache in the background."
                    : $"PS7 ScriptDesk is rebuilding full editor metadata because the saved snapshot was missing, stale, or incomplete. {loadReason}";

            RaiseMetadataWarmupStatus(
                new EditorMetadataWarmupStatus(
                    loadedFromCache ? EditorMetadataWarmupPhase.RefreshingCachedMetadata : EditorMetadataWarmupPhase.Scheduled,
                    forceRebuild
                        ? "Refreshing editor metadata"
                        : loadedFromCache
                            ? "Loading cached editor metadata"
                            : "Building first-run editor metadata",
                    normalizedRuntimePath,
                    detailText: detailText,
                    commandCount: cachedHealth.CommandCount,
                    quickInfoCount: cachedHealth.QuickInfoCount,
                    parameterizedQuickInfoCount: cachedHealth.ParameterizedQuickInfoCount,
                    getChildItemParameterCount: cachedHealth.GetChildItemParameterCount,
                    isLoadedFromCache: loadedFromCache,
                    reason: warmupReason));

            if (isUserInitiated)
            {
                AppLogger.Info("EditorMetadata", $"Manual metadata refresh starting for runtime '{normalizedRuntimePath}'.");
            }

            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Full rebuild/cache refresh starting. WarmupReason={warmupReason}, LoadedFromCacheBeforeRefresh={loadedFromCache}.");
            LaunchMetadataBuilderProcess(runtimeInfo, loadedFromCache, warmupReason, performanceLogPath, startupDiagnostics);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopMetadataBuilderProcess();
            lock (_syncRoot)
            {
                _quickInfoCache.Clear();
                _commandCatalogCache.Clear();
            }

            TeardownProcess();
            try { _requestGate.Dispose(); } catch { }
        }

        private void RaiseMetadataWarmupStatus(EditorMetadataWarmupStatus status)
        {
            _activeMetadataInitialLoadDiagnostics?.RecordUiStatus(status);
            var handler = MetadataWarmupStatusChanged;
            if (handler is null || status is null)
            {
                return;
            }

            try
            {
                handler(this, new EditorMetadataWarmupStatusChangedEventArgs(status));
            }
            catch
            {
                // UI listeners should never break background metadata warmup.
            }
        }

        private static void LogSnapshotParameterMetadataHealth(string runtimePath, EditorMetadataCacheSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            var metadataHealth = BuildMetadataCacheHealth(snapshot);

            AppLogger.Info(
                "EditorMetadata",
                $"Cached parameter metadata health for runtime '{runtimePath}': {EditorMetadataSnapshotValidator.Describe(metadataHealth)}.");
        }

        private static bool HasUsableParameterMetadata(PowerShellQuickInfo? quickInfo)
        {
            return quickInfo is not null && quickInfo.Parameters.Count > 0;
        }

        private static EditorMetadataSnapshotHealth BuildMetadataCacheHealth(EditorMetadataCacheSnapshot snapshot)
        {
            return EditorMetadataSnapshotValidator.BuildHealth(snapshot);
        }

        private EditorMetadataSnapshotHealth GetLoadedMetadataHealth(string? runtimePath)
        {
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                return EditorMetadataSnapshotHealth.Empty;
            }

            var normalizedRuntimePath = NormalizePath(runtimePath);
            lock (_syncRoot)
            {
                return string.Equals(_loadedMetadataRuntimePath, normalizedRuntimePath, StringComparison.OrdinalIgnoreCase)
                    ? _loadedMetadataHealth
                    : EditorMetadataSnapshotHealth.Empty;
            }
        }

        private bool TryResolveCachedCommandReference(
            string pwshExecutablePath,
            string commandName,
            out PowerShellCommandReference? commandReference)
        {
            commandReference = null;
            if (string.IsNullOrWhiteSpace(pwshExecutablePath) || string.IsNullOrWhiteSpace(commandName))
            {
                return false;
            }

            var normalizedRuntimePath = NormalizePath(pwshExecutablePath);
            lock (_syncRoot)
            {
                if (_commandCatalogCache.TryGetValue(normalizedRuntimePath, out var cachedCatalog) &&
                    DateTimeOffset.UtcNow - cachedCatalog.CachedAt <= TimeSpan.FromHours(1))
                {
                    commandReference = cachedCatalog.Catalog.Commands.FirstOrDefault(reference =>
                        string.Equals(reference.Name, commandName, StringComparison.OrdinalIgnoreCase));
                    return commandReference is not null;
                }
            }

            return false;
        }

        private bool TryGetCachedQuickInfo(string cacheKey, out PowerShellQuickInfo? quickInfo)
        {
            lock (_syncRoot)
            {
                if (_quickInfoCache.TryGetValue(cacheKey, out var cached) &&
                    DateTimeOffset.UtcNow - cached.CachedAt <= TimeSpan.FromMinutes(60))
                {
                    quickInfo = cached.QuickInfo;
                    return true;
                }
            }

            quickInfo = null;
            return false;
        }

        private void AddQuickInfoToCache(string cacheKey, PowerShellQuickInfo quickInfo)
        {
            lock (_syncRoot)
            {
                if (_quickInfoCache.Count >= MaxQuickInfoCacheEntries)
                {
                    foreach (var staleKey in _quickInfoCache
                                 .OrderBy(pair => pair.Value.CachedAt)
                                 .Take(Math.Max(1, MaxQuickInfoCacheEntries / 4))
                                 .Select(pair => pair.Key)
                                 .ToList())
                    {
                        _quickInfoCache.Remove(staleKey);
                    }
                }

                _quickInfoCache[cacheKey] = new CachedQuickInfo(quickInfo, DateTimeOffset.UtcNow);
            }
        }

        private static string BuildQuickInfoCacheKey(string pwshExecutablePath, string commandName)
        {
            return NormalizePath(pwshExecutablePath) + "|" + commandName.Trim();
        }

        private async Task<PowerShellQuickInfo?> FetchCommandQuickInfoAsync(
            string commandName,
            string pwshExecutablePath,
            bool includeHelp,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var payload = await ExecuteTransportRequestAsync(
                        pwshExecutablePath,
                        request => BuildCommandQuickInfoCommand(commandName, request.StartMarker, request.EndMarker, includeHelp),
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                return ParseQuickInfoPayload(payload);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                AppLogger.Debug("EditorCompletion", $"Quick-info request for '{commandName}' canceled after {stopwatch.ElapsedMilliseconds:N0} ms.");
                return null;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                AppLogger.Warning("EditorCompletion", $"Quick-info request for '{commandName}' failed after {stopwatch.ElapsedMilliseconds:N0} ms: {ex.Message}");
                return null;
            }
        }

        private bool TryLoadPersistedMetadataSnapshot(
            PowerShellRuntimeInfo runtimeInfo,
            out EditorMetadataCacheSnapshot? snapshot,
            out EditorMetadataCacheManifest? manifest,
            out string reason,
            string? performanceLogPath = null,
            MetadataInitialLoadDiagnostics? startupDiagnostics = null)
        {
            snapshot = null;
            manifest = null;
            reason = string.Empty;
            var normalizedRuntimePath = NormalizePath(runtimeInfo.LaunchExecutablePath);
            var stopwatch = Stopwatch.StartNew();
            MetadataPerformanceLog.AppendSection(performanceLogPath, "Cache load decision");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase started: Cache probe. StartUtc={DateTime.UtcNow:O}. Runtime='{normalizedRuntimePath}'.");
            foreach (var candidate in EditorMetadataCacheStore.GetCacheProbeCandidates(
                         normalizedRuntimePath,
                         runtimeInfo.VersionText ?? string.Empty,
                         runtimeInfo.Edition ?? string.Empty,
                         runtimeInfo.Architecture ?? string.Empty,
                         runtimeInfo.PsHome ?? string.Empty))
            {
                startupDiagnostics?.RecordCacheProbeCandidate(candidate);
            }

            if (!EditorMetadataCacheStore.TryLoadSnapshot(
                    normalizedRuntimePath,
                    runtimeInfo.VersionText ?? string.Empty,
                    runtimeInfo.Edition ?? string.Empty,
                    runtimeInfo.Architecture ?? string.Empty,
                    runtimeInfo.PsHome ?? string.Empty,
                    out var loadedSnapshot,
                    out var loadedManifest,
                    out var loadedCacheDirectory,
                    out var loadedFromLegacyPathCache))
            {
                stopwatch.Stop();
                lock (_syncRoot)
                {
                    _loadedPersistedMetadataForRuntime = false;
                    _loadedMetadataRuntimePath = normalizedRuntimePath;
                    _loadedMetadataHealth = EditorMetadataSnapshotHealth.Empty;
                }

                reason = $"No cached metadata snapshot could be loaded for runtime '{normalizedRuntimePath}' after {stopwatch.ElapsedMilliseconds:N0} ms.";
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache read time: {stopwatch.ElapsedMilliseconds:N0} ms.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache hit/miss reason: {reason}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Cache probe. Result=Missing. DurationMs={stopwatch.ElapsedMilliseconds:N0}. EndUtc={DateTime.UtcNow:O}");
                startupDiagnostics?.RecordCacheDecision(false, "MissingOrUnreadable", reason);
                return false;
            }

            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase started: Snapshot validation. StartUtc={DateTime.UtcNow:O}");
            var validation = EditorMetadataSnapshotValidator.Validate(loadedSnapshot);
            if (!validation.IsHealthy)
            {
                stopwatch.Stop();
                reason = $"Saved metadata snapshot for runtime '{normalizedRuntimePath}' is incomplete or corrupt. {validation.Message} {EditorMetadataSnapshotValidator.Describe(validation.Health)}";
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache read time: {stopwatch.ElapsedMilliseconds:N0} ms.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache hit/miss reason: {reason}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Snapshot validation. Result=Rejected. EndUtc={DateTime.UtcNow:O}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Cache probe. Result=RejectedByValidator. DurationMs={stopwatch.ElapsedMilliseconds:N0}. EndUtc={DateTime.UtcNow:O}");
                AppLogger.Warning("EditorMetadata", reason);
                EditorMetadataCacheStore.QuarantineSnapshotDirectory(loadedCacheDirectory, "invalid-full-metadata", out _);
                ResetLoadedMetadataState(normalizedRuntimePath);
                startupDiagnostics?.RecordCacheDecision(false, "RejectedByValidator", reason, loadedSnapshot);
                return false;
            }

            if (!DoesManifestMatchRuntime(runtimeInfo, loadedManifest, out var manifestReason))
            {
                stopwatch.Stop();
                reason = $"Saved metadata snapshot for runtime '{normalizedRuntimePath}' is stale. {manifestReason}";
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache read time: {stopwatch.ElapsedMilliseconds:N0} ms.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache hit/miss reason: {reason}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Snapshot validation. Result=Stale. EndUtc={DateTime.UtcNow:O}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Cache probe. Result=Stale. DurationMs={stopwatch.ElapsedMilliseconds:N0}. EndUtc={DateTime.UtcNow:O}");
                AppLogger.Warning("EditorMetadata", reason);
                AppLogger.Info("EditorMetadata", $"Preserving stale metadata cache for possible future runtime switching. Runtime='{normalizedRuntimePath}', CacheDirectory='{loadedCacheDirectory}'.");
                ResetLoadedMetadataState(normalizedRuntimePath);
                startupDiagnostics?.RecordCacheDecision(false, "Stale", reason, loadedSnapshot);
                return false;
            }

            ApplyPersistedMetadataSnapshot(normalizedRuntimePath, loadedSnapshot);
            snapshot = loadedSnapshot;
            manifest = loadedManifest;
            var metadataHealth = validation.Health;
            stopwatch.Stop();
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache read time: {stopwatch.ElapsedMilliseconds:N0} ms.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache hit/miss reason: saved metadata snapshot is healthy and matches the selected runtime.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache directory: {loadedCacheDirectory}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Loaded from legacy path cache: {loadedFromLegacyPathCache}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cached command count: {metadataHealth.CommandCount:N0}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cached quick-info count: {metadataHealth.QuickInfoCount:N0}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cached parameterized command count: {metadataHealth.ParameterizedQuickInfoCount:N0}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Snapshot validation. Result=Accepted. EndUtc={DateTime.UtcNow:O}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Cache probe. Result=LoadedFromCache. DurationMs={stopwatch.ElapsedMilliseconds:N0}. EndUtc={DateTime.UtcNow:O}");
            AppLogger.Info("EditorMetadata", $"Loaded cached metadata snapshot for runtime '{normalizedRuntimePath}' in {stopwatch.ElapsedMilliseconds:N0} ms. CacheDirectory='{loadedCacheDirectory}', LegacyPathCache={loadedFromLegacyPathCache}. {EditorMetadataSnapshotValidator.Describe(metadataHealth)}.");

            lock (_syncRoot)
            {
                _loadedPersistedMetadataForRuntime = true;
                _loadedMetadataRuntimePath = normalizedRuntimePath;
                _loadedMetadataHealth = metadataHealth;
            }

            reason = $"Loaded cached full editor metadata snapshot for runtime '{normalizedRuntimePath}' in {stopwatch.ElapsedMilliseconds:N0} ms. CacheDirectory='{loadedCacheDirectory}', LegacyPathCache={loadedFromLegacyPathCache}. {EditorMetadataSnapshotValidator.Describe(metadataHealth)}.";
            startupDiagnostics?.RecordCacheDecision(true, loadedFromLegacyPathCache ? "LoadedFromLegacyCache" : "LoadedFromCache", reason, loadedSnapshot);
            return true;
        }

        private bool DoesManifestMatchRuntime(
            PowerShellRuntimeInfo runtimeInfo,
            EditorMetadataCacheManifest manifest,
            out string decisionReason)
        {
            var normalizedRuntimePath = NormalizePath(runtimeInfo.LaunchExecutablePath);
            decisionReason = "The saved metadata snapshot matches the current runtime identity.";

            if (manifest is null)
            {
                decisionReason = "The saved metadata snapshot does not include manifest information.";
                return false;
            }

            if (!string.Equals(EditorMetadataCacheStore.NormalizeRuntimePath(manifest.RuntimePath), normalizedRuntimePath, StringComparison.OrdinalIgnoreCase))
            {
                decisionReason = "The selected runtime path changed.";
                return false;
            }

            if (manifest.SchemaVersion < 2)
            {
                decisionReason = $"The saved metadata snapshot uses schema version {manifest.SchemaVersion}, but version 2 or later is required.";
                return false;
            }

            if (manifest.CatalogCount <= 0 || manifest.QuickInfoCount <= 0)
            {
                decisionReason = "The saved metadata snapshot is incomplete.";
                return false;
            }

            if (manifest.CreatedUtcTicks <= 0 && manifest.BuiltUtcTicks <= 0)
            {
                decisionReason = "The saved metadata snapshot does not include a creation timestamp.";
                return false;
            }

            if (!string.Equals(manifest.RuntimeVersion?.Trim(), runtimeInfo.VersionText?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                decisionReason = $"The PowerShell runtime version changed from '{manifest.RuntimeVersion}' to '{runtimeInfo.VersionText}'.";
                return false;
            }

            if (!string.Equals(manifest.PowerShellEdition?.Trim(), runtimeInfo.Edition?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                decisionReason = $"The PowerShell runtime edition changed from '{manifest.PowerShellEdition}' to '{runtimeInfo.Edition}'.";
                return false;
            }

            if (!string.Equals((manifest.RuntimeArchitecture ?? string.Empty).Trim(), (runtimeInfo.Architecture ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                decisionReason = $"The PowerShell runtime architecture changed from '{manifest.RuntimeArchitecture}' to '{runtimeInfo.Architecture}'.";
                return false;
            }

            if (!string.Equals((manifest.RuntimePsHome ?? string.Empty).Trim(), (runtimeInfo.PsHome ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                decisionReason = $"The PowerShell runtime PSHOME changed from '{manifest.RuntimePsHome}' to '{runtimeInfo.PsHome}'.";
                return false;
            }

            return true;
        }

        private void ApplyPersistedMetadataSnapshot(string normalizedRuntimePath, EditorMetadataCacheSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                var cacheKeyPrefix = normalizedRuntimePath + "|";
                foreach (var staleQuickInfoKey in _quickInfoCache.Keys.Where(key => key.StartsWith(cacheKeyPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _quickInfoCache.Remove(staleQuickInfoKey);
                }

                _commandCatalogCache[normalizedRuntimePath] = new CachedCommandCatalog(snapshot.Catalog, DateTimeOffset.UtcNow);
                foreach (var pair in snapshot.QuickInfos)
                {
                    _quickInfoCache[BuildQuickInfoCacheKey(normalizedRuntimePath, pair.Key)] = new CachedQuickInfo(pair.Value, DateTimeOffset.UtcNow);
                }
            }
        }

        private void ResetLoadedMetadataState(string normalizedRuntimePath)
        {
            lock (_syncRoot)
            {
                var cacheKeyPrefix = normalizedRuntimePath + "|";
                foreach (var staleQuickInfoKey in _quickInfoCache.Keys.Where(key => key.StartsWith(cacheKeyPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _quickInfoCache.Remove(staleQuickInfoKey);
                }

                _commandCatalogCache.Remove(normalizedRuntimePath);
                _loadedPersistedMetadataForRuntime = false;
                _loadedMetadataRuntimePath = normalizedRuntimePath;
                _loadedMetadataHealth = EditorMetadataSnapshotHealth.Empty;
            }
        }

        private static string GetManifestCreationUtcText(EditorMetadataCacheManifest manifest)
        {
            if (manifest is null)
            {
                return "unknown";
            }

            var ticks = manifest.CreatedUtcTicks > 0 ? manifest.CreatedUtcTicks : manifest.BuiltUtcTicks;
            if (ticks <= 0)
            {
                return "unknown";
            }

            try
            {
                return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)).ToString("u", CultureInfo.InvariantCulture).Trim();
            }
            catch
            {
                return "unknown";
            }
        }

        private void LaunchMetadataBuilderProcess(PowerShellRuntimeInfo runtimeInfo, bool readyCacheAlreadyLoaded, EditorMetadataWarmupReason warmupReason, string performanceLogPath, MetadataInitialLoadDiagnostics? startupDiagnostics)
        {
            var normalizedRuntimePath = NormalizePath(runtimeInfo.LaunchExecutablePath);
            var currentExecutablePath = Environment.ProcessPath;
            AppLogger.Info("EditorMetadata", $"Metadata builder launch request ProcessStartInfo.FileName will remain '{currentExecutablePath}'. Worker runtime launch path='{normalizedRuntimePath}'.");
            startupDiagnostics?.RecordBackgroundRefreshStarted();
            MetadataPerformanceLog.AppendSection(performanceLogPath, "Background process launch requested");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Background process launch requested UTC: {DateTime.UtcNow:O}");
            if (string.IsNullOrWhiteSpace(currentExecutablePath) || !File.Exists(currentExecutablePath))
            {
                MetadataPerformanceLog.AppendLine(performanceLogPath, "Metadata builder launch failed: PS7 ScriptDesk could not locate its helper executable.");
                startupDiagnostics?.RecordFailure("Background process launch requested", "PS7 ScriptDesk could not locate its helper executable to build the metadata cache.");
                startupDiagnostics?.FinalizeFailure("Background process launch requested", "PS7 ScriptDesk could not locate its helper executable to build the metadata cache.");
                RaiseMetadataWarmupStatus(
                    new EditorMetadataWarmupStatus(
                        EditorMetadataWarmupPhase.Failed,
                        "Editor metadata failed; see log",
                        normalizedRuntimePath,
                        detailText: AppendMetadataLogSupportHint("PS7 ScriptDesk could not locate its helper executable to build the metadata cache.", performanceLogPath),
                        reason: warmupReason));
                return;
            }

            var builderCancellationTokenSource = new CancellationTokenSource();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = currentExecutablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.ArgumentList.Add(EditorMetadataBuilderHost.BuilderSwitch);
            process.StartInfo.ArgumentList.Add("--runtime");
            process.StartInfo.ArgumentList.Add(normalizedRuntimePath);
            process.StartInfo.ArgumentList.Add("--performance-log");
            process.StartInfo.ArgumentList.Add(performanceLogPath);

            try
            {
                var builderStartStopwatch = Stopwatch.StartNew();
                process.Start();
                builderStartStopwatch.Stop();
                TryLowerChildProcessPriority(process);
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Builder helper process Start() elapsed: {builderStartStopwatch.ElapsedMilliseconds:N0} ms. ProcessId={process.Id}.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Background process launch requested. Result=Started. DurationMs={builderStartStopwatch.ElapsedMilliseconds:N0}. EndUtc={DateTime.UtcNow:O}");
                AppLogger.Info("EditorMetadata", $"Started metadata builder helper process {process.Id} for runtime '{normalizedRuntimePath}'. CachedReady={readyCacheAlreadyLoaded}. PerformanceLog='{performanceLogPath}'.");
            }
            catch (Exception ex)
            {
                builderCancellationTokenSource.Dispose();
                process.Dispose();
                var cachedHealth = readyCacheAlreadyLoaded ? GetLoadedMetadataHealth(normalizedRuntimePath) : EditorMetadataSnapshotHealth.Empty;
                startupDiagnostics?.RecordException("Background process launch requested", ex);

                RaiseMetadataWarmupStatus(
                    new EditorMetadataWarmupStatus(
                        readyCacheAlreadyLoaded ? EditorMetadataWarmupPhase.Warning : EditorMetadataWarmupPhase.Failed,
                        readyCacheAlreadyLoaded ? "Metadata refresh failed; cached metadata still in use." : "Editor metadata failed; see log",
                        normalizedRuntimePath,
                        detailText: readyCacheAlreadyLoaded
                            ? AppendMetadataLogSupportHint($"PS7 ScriptDesk kept using the last known-good metadata snapshot, but could not start the refresh helper: {ex.Message}", performanceLogPath)
                            : AppendMetadataLogSupportHint(ex.Message, performanceLogPath),
                        commandCount: cachedHealth.CommandCount,
                        quickInfoCount: cachedHealth.QuickInfoCount,
                        parameterizedQuickInfoCount: cachedHealth.ParameterizedQuickInfoCount,
                        getChildItemParameterCount: cachedHealth.GetChildItemParameterCount,
                        isLoadedFromCache: readyCacheAlreadyLoaded,
                        reason: warmupReason));
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder launch failed: {ex}");
                AppLogger.Error("EditorMetadata", $"Failed to start metadata builder helper for runtime '{normalizedRuntimePath}'.", ex);
                startupDiagnostics?.FinalizeFailure("Background process launch requested", ex.Message);
                return;
            }

            lock (_syncRoot)
            {
                _metadataBuilderProcess = process;
                _metadataBuilderCancellationTokenSource = builderCancellationTokenSource;
                _metadataBuilderRuntimePath = normalizedRuntimePath;
                _activeMetadataInitialLoadDiagnostics = startupDiagnostics;
                _metadataBuilderStdoutReaderTask = MonitorMetadataBuilderProcessAsync(process, runtimeInfo, readyCacheAlreadyLoaded, warmupReason, performanceLogPath, builderCancellationTokenSource, startupDiagnostics);
                _metadataBuilderStderrReaderTask = DrainMetadataBuilderErrorsAsync(process, builderCancellationTokenSource, performanceLogPath);
            }
        }

        private async Task MonitorMetadataBuilderProcessAsync(
            Process process,
            PowerShellRuntimeInfo runtimeInfo,
            bool readyCacheAlreadyLoaded,
            EditorMetadataWarmupReason warmupReason,
            string? performanceLogPath,
            CancellationTokenSource builderCancellationTokenSource,
            MetadataInitialLoadDiagnostics? startupDiagnostics)
        {
            var normalizedRuntimePath = NormalizePath(runtimeInfo.LaunchExecutablePath);
            var cancellationToken = builderCancellationTokenSource.Token;
            var processStartUtc = DateTimeOffset.UtcNow;
            var receivedTerminalStatus = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (!EditorMetadataBuilderProtocol.TryParseStatusLine(line, out var message) || message is null)
                    {
                        continue;
                    }

                    if (message.Phase == EditorMetadataWarmupPhase.Completed ||
                        message.Phase == EditorMetadataWarmupPhase.Failed ||
                        message.Phase == EditorMetadataWarmupPhase.Canceled)
                    {
                        receivedTerminalStatus = true;
                    }

                    HandleMetadataBuilderStatus(runtimeInfo, readyCacheAlreadyLoaded, warmupReason, performanceLogPath, message);
                }

                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation during shutdown/runtime change.
                }

                if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
                {
                    AppLogger.Warning("EditorMetadata", $"Metadata builder helper for runtime '{normalizedRuntimePath}' exited with code {process.ExitCode}.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder helper exited with code {process.ExitCode}.");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder helper start UTC: {processStartUtc:O}");
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder helper exit UTC: {DateTime.UtcNow:O}");
                    ReportMetadataBuilderFailure(
                        normalizedRuntimePath,
                        readyCacheAlreadyLoaded,
                        warmupReason,
                        $"The metadata helper exited with code {process.ExitCode}.",
                        performanceLogPath);
                }
                else if (!cancellationToken.IsCancellationRequested && process.ExitCode == 0 && !receivedTerminalStatus)
                {
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder helper exited without a terminal status message. StartUtc={processStartUtc:O}, ExitUtc={DateTime.UtcNow:O}");
                    ReportMetadataBuilderFailure(
                        normalizedRuntimePath,
                        readyCacheAlreadyLoaded,
                        warmupReason,
                        "The metadata helper exited successfully but did not report a terminal metadata status.",
                        performanceLogPath);
                }
            }
            catch (OperationCanceledException)
            {
                startupDiagnostics?.RecordFailure("Background process cancelled", "Metadata initial-load helper was canceled.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder helper canceled UTC: {DateTime.UtcNow:O}");
                // Ignore cancellation during shutdown/runtime change.
            }
            catch (Exception ex)
            {
                AppLogger.Error("EditorMetadata", $"Metadata builder monitor failed for runtime '{normalizedRuntimePath}'.", ex);
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder monitor failed: {ex}");
                startupDiagnostics?.RecordException("Background process monitor", ex);
                ReportMetadataBuilderFailure(normalizedRuntimePath, readyCacheAlreadyLoaded, warmupReason, ex.Message, performanceLogPath);
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_metadataBuilderProcess, process))
                    {
                        _metadataBuilderProcess = null;
                        _metadataBuilderRuntimePath = normalizedRuntimePath;
                    }

                    if (ReferenceEquals(_metadataBuilderCancellationTokenSource, builderCancellationTokenSource))
                    {
                        _metadataBuilderCancellationTokenSource = null;
                    }

                    if (ReferenceEquals(_activeMetadataInitialLoadDiagnostics, startupDiagnostics))
                    {
                        _activeMetadataInitialLoadDiagnostics = null;
                    }
                }

                try { process.Dispose(); } catch { }
            }
        }

        private async Task DrainMetadataBuilderErrorsAsync(Process process, CancellationTokenSource builderCancellationTokenSource, string? performanceLogPath)
        {
            var cancellationToken = builderCancellationTokenSource.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        System.Diagnostics.Debug.WriteLine($"[PowerShellCompletionService] Metadata builder: {line}");
                        AppLogger.Debug("EditorMetadata", $"Metadata helper stderr: {line.Trim()}");
                        MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata helper stderr: {line.Trim()}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation during shutdown/runtime change.
            }
            catch
            {
                // Best-effort stderr drain only.
            }
        }

        private void HandleMetadataBuilderStatus(
            PowerShellRuntimeInfo runtimeInfo,
            bool readyCacheAlreadyLoaded,
            EditorMetadataWarmupReason warmupReason,
            string? performanceLogPath,
            EditorMetadataBuilderStatusMessage message)
        {
            var normalizedRuntimePath = NormalizePath(runtimeInfo.LaunchExecutablePath);
            var hasReadyMetadata = readyCacheAlreadyLoaded || RuntimeHasReadyMetadataSnapshot(normalizedRuntimePath);
            var metadataHealth = GetLoadedMetadataHealth(normalizedRuntimePath);
            AppLogger.Debug("EditorMetadata", $"Metadata helper status for runtime '{normalizedRuntimePath}': Phase={message.Phase}, Processed={message.ProcessedCount}, Total={message.TotalCount}, Message={message.Message}");
            switch (message.Phase)
            {
                case EditorMetadataWarmupPhase.BuildingCommandCatalog:
                case EditorMetadataWarmupPhase.LoadingCommandMetadata:
                    RaiseMetadataWarmupStatus(
                        new EditorMetadataWarmupStatus(
                            hasReadyMetadata ? EditorMetadataWarmupPhase.RefreshingCachedMetadata : message.Phase,
                            hasReadyMetadata ? "Refreshing editor metadata" : message.Message,
                            normalizedRuntimePath,
                            message.ProcessedCount,
                            message.TotalCount,
                            hasReadyMetadata
                                ? $"PS7 ScriptDesk is using the saved editor metadata snapshot while a newer full snapshot is refreshed in the background. {message.DetailText}".Trim()
                                : message.DetailText,
                            commandCount: metadataHealth.CommandCount,
                            quickInfoCount: metadataHealth.QuickInfoCount,
                            parameterizedQuickInfoCount: metadataHealth.ParameterizedQuickInfoCount,
                            getChildItemParameterCount: metadataHealth.GetChildItemParameterCount,
                            isLoadedFromCache: hasReadyMetadata,
                            reason: warmupReason));
                    break;

                case EditorMetadataWarmupPhase.Completed:
                    if (TryLoadPersistedMetadataSnapshot(runtimeInfo, out var snapshot, out var manifest, out var loadReason, performanceLogPath, _activeMetadataInitialLoadDiagnostics) && snapshot is not null && manifest is not null)
                    {
                        var completedMetadataHealth = BuildMetadataCacheHealth(snapshot);
                        MetadataPerformanceLog.AppendSection(performanceLogPath, "Main process completion");
                        MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh completed and validated in main process. {EditorMetadataSnapshotValidator.Describe(completedMetadataHealth)}");
                        MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Metadata ready full. Result=Success. EndUtc={DateTime.UtcNow:O}");
                        MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh finished UTC: {DateTime.UtcNow:O}");
                        MetadataPerformanceLog.AppendLine(performanceLogPath, $"Output path for this performance log: {performanceLogPath}");
                        AppLogger.Info("EditorMetadata", $"Metadata refresh completed for runtime '{normalizedRuntimePath}'. {EditorMetadataSnapshotValidator.Describe(completedMetadataHealth)}.");
                        _activeMetadataInitialLoadDiagnostics?.RecordSnapshotCounts(snapshot);
                        _activeMetadataInitialLoadDiagnostics?.RecordSuccess("Success", "RebuiltInBackground");
                        RaiseMetadataWarmupStatus(
                            new EditorMetadataWarmupStatus(
                                EditorMetadataWarmupPhase.Completed,
                                "Editor metadata ready",
                                normalizedRuntimePath,
                                snapshot.QuickInfos.Count,
                                snapshot.QuickInfos.Count,
                                $"Full editor metadata cache is ready and saved for future launches. {message.DetailText} Created UTC: {GetManifestCreationUtcText(manifest)}.".Trim(),
                                commandCount: completedMetadataHealth.CommandCount,
                                quickInfoCount: completedMetadataHealth.QuickInfoCount,
                                parameterizedQuickInfoCount: completedMetadataHealth.ParameterizedQuickInfoCount,
                                getChildItemParameterCount: completedMetadataHealth.GetChildItemParameterCount,
                                reason: warmupReason));
                        _activeMetadataInitialLoadDiagnostics?.FinalizeSuccess("Success");
                    }
                    else
                    {
                        MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata helper reported completion, but saved cache could not be validated. {loadReason}");
                        AppLogger.Warning("EditorMetadata", $"Metadata helper reported completion for runtime '{normalizedRuntimePath}', but the saved cache could not be validated. {loadReason}");
                        ReportMetadataBuilderFailure(normalizedRuntimePath, hasReadyMetadata, warmupReason, loadReason, performanceLogPath);
                    }
                    break;

                case EditorMetadataWarmupPhase.Failed:
                    ReportMetadataBuilderFailure(normalizedRuntimePath, hasReadyMetadata, warmupReason, message.DetailText, performanceLogPath);
                    break;

                case EditorMetadataWarmupPhase.Canceled:
                    if (!hasReadyMetadata)
                    {
                        RaiseMetadataWarmupStatus(
                            new EditorMetadataWarmupStatus(
                                EditorMetadataWarmupPhase.Canceled,
                                message.Message,
                                normalizedRuntimePath,
                                detailText: message.DetailText,
                                commandCount: metadataHealth.CommandCount,
                                quickInfoCount: metadataHealth.QuickInfoCount,
                                parameterizedQuickInfoCount: metadataHealth.ParameterizedQuickInfoCount,
                                getChildItemParameterCount: metadataHealth.GetChildItemParameterCount,
                                isLoadedFromCache: hasReadyMetadata,
                                reason: warmupReason));
                    }
                    break;
            }
        }

        private void ReportMetadataBuilderFailure(string normalizedRuntimePath, bool hasReadyMetadata, EditorMetadataWarmupReason warmupReason, string? detailText, string? performanceLogPath = null)
        {
            var safeDetailText = string.IsNullOrWhiteSpace(detailText)
                ? "The background metadata builder did not complete successfully."
                : detailText.Trim();
            _activeMetadataInitialLoadDiagnostics?.RecordBackgroundRefreshFailure("Metadata failed", safeDetailText);
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata builder reported failure. HasReadyMetadata={hasReadyMetadata}. Detail={safeDetailText}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Phase completed: Metadata failed. Result=Failure. EndUtc={DateTime.UtcNow:O}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh finished UTC: {DateTime.UtcNow:O}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Output path for this performance log: {performanceLogPath}");
            AppLogger.Warning("EditorMetadata", $"Metadata builder reported failure for runtime '{normalizedRuntimePath}'. HasReadyMetadata={hasReadyMetadata}. Detail={safeDetailText}");

            if (hasReadyMetadata)
            {
                var metadataHealth = GetLoadedMetadataHealth(normalizedRuntimePath);
                RaiseMetadataWarmupStatus(
                    new EditorMetadataWarmupStatus(
                        EditorMetadataWarmupPhase.Warning,
                        "Metadata refresh failed; cached metadata still in use.",
                        normalizedRuntimePath,
                        detailText: AppendMetadataLogSupportHint($"PS7 ScriptDesk is still using the last known-good metadata snapshot. Refresh error: {safeDetailText}", performanceLogPath),
                        commandCount: metadataHealth.CommandCount,
                        quickInfoCount: metadataHealth.QuickInfoCount,
                        parameterizedQuickInfoCount: metadataHealth.ParameterizedQuickInfoCount,
                        getChildItemParameterCount: metadataHealth.GetChildItemParameterCount,
                        isLoadedFromCache: true,
                        reason: warmupReason));
                _activeMetadataInitialLoadDiagnostics?.FinalizeFailure("Metadata ready partial", safeDetailText);
                return;
            }

            RaiseMetadataWarmupStatus(
                new EditorMetadataWarmupStatus(
                    EditorMetadataWarmupPhase.Failed,
                    "Editor metadata failed; see log",
                    normalizedRuntimePath,
                    detailText: AppendMetadataLogSupportHint(safeDetailText, performanceLogPath),
                    reason: warmupReason));
            _activeMetadataInitialLoadDiagnostics?.FinalizeFailure("Metadata failed", safeDetailText);
        }

        private static string AppendMetadataLogSupportHint(string detailText, string? performanceLogPath)
        {
            var safeDetailText = string.IsNullOrWhiteSpace(detailText)
                ? "Metadata failed."
                : detailText.Trim();

            if (string.IsNullOrWhiteSpace(performanceLogPath))
            {
                return safeDetailText;
            }

            return $"{safeDetailText}{Environment.NewLine}Metadata diagnostic log:{Environment.NewLine}{performanceLogPath}";
        }

        private bool RuntimeHasReadyMetadataSnapshot(string normalizedRuntimePath)
        {
            lock (_syncRoot)
            {
                return _loadedPersistedMetadataForRuntime &&
                       string.Equals(_loadedMetadataRuntimePath, normalizedRuntimePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ClearActiveMetadataInitialLoadDiagnostics(MetadataInitialLoadDiagnostics? diagnostics)
        {
            if (diagnostics is null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeMetadataInitialLoadDiagnostics, diagnostics))
                {
                    _activeMetadataInitialLoadDiagnostics = null;
                }
            }
        }

        private void StopMetadataBuilderProcess()
        {
            Process? processToStop = null;
            CancellationTokenSource? cancellationToStop = null;
            MetadataInitialLoadDiagnostics? diagnosticsToFinalize = null;

            lock (_syncRoot)
            {
                processToStop = _metadataBuilderProcess;
                cancellationToStop = _metadataBuilderCancellationTokenSource;
                diagnosticsToFinalize = _activeMetadataInitialLoadDiagnostics;
                _metadataBuilderProcess = null;
                _metadataBuilderCancellationTokenSource = null;
                _metadataBuilderStdoutReaderTask = null;
                _metadataBuilderStderrReaderTask = null;
                _activeMetadataInitialLoadDiagnostics = null;
            }

            try { cancellationToStop?.Cancel(); } catch { }
            if (processToStop is not null)
            {
                AppLogger.Info("EditorMetadata", $"Stopping metadata builder helper process {processToStop.Id}.");
                diagnosticsToFinalize?.FinalizeFailure("Background process cancelled", $"Metadata builder helper process {processToStop.Id} was stopped before completion.");
            }
            try
            {
                if (processToStop is not null && !processToStop.HasExited)
                {
                    processToStop.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort only.
            }

            try { processToStop?.Dispose(); } catch { }
            try { cancellationToStop?.Dispose(); } catch { }
        }

        private static void TryLowerChildProcessPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch
            {
                // Best effort only.
            }
        }

        private async Task EnsureProcessReadyAsync(string pwshExecutablePath, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var normalizedPath = NormalizePath(pwshExecutablePath);

            lock (_syncRoot)
            {
                if (_process is not null && !_process.HasExited &&
                    string.Equals(_activeRuntimePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            TeardownProcess();

            var processCts = new CancellationTokenSource();
            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyMarker = $"##PSSTUDIO_COMP_READY_{Guid.NewGuid():N}##";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pwshExecutablePath,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -NoExit",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                },
                EnableRaisingEvents = true,
            };
            process.Exited += (_, _) => HandleProcessExited(process);
            PowerShellBackgroundProcessEnvironment.Apply(process.StartInfo, "Completion", pwshExecutablePath);
            AppLogger.Info("EditorCompletion", $"Completion helper ProcessStartInfo.FileName='{process.StartInfo.FileName}'.");

            try { process.Start(); }
            catch { processCts.Dispose(); process.Dispose(); throw; }

            lock (_syncRoot)
            {
                _process = process;
                _stdin = process.StandardInput;
                _processCancellationTokenSource = processCts;
                _readyCompletionSource = readyTcs;
                _readyMarker = readyMarker;
                _activeRuntimePath = normalizedPath;
                _sharedOutputTail.Clear();
                _activeRequest = null;
            }

            _stdoutReaderTask = Task.Run(
                () => ReadLoopAsync(process, process.StandardOutput, false, processCts.Token),
                processCts.Token);
            _stderrReaderTask = Task.Run(
                () => ReadLoopAsync(process, process.StandardError, true, processCts.Token),
                processCts.Token);
            ObserveBackgroundTask(_stdoutReaderTask, "completion stdout reader");
            ObserveBackgroundTask(_stderrReaderTask, "completion stderr reader");

            try
            {
                await SendCommandAsync(
                    $"[Console]::Out.Write('{readyMarker}'); [Console]::Out.Flush()",
                    cancellationToken).ConfigureAwait(false);
            }
            catch { TeardownProcess(); throw; }

            using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readinessCts.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                var ready = await readyTcs.Task.WaitAsync(readinessCts.Token).ConfigureAwait(false);
                if (!ready)
                {
                    TeardownProcess();
                    throw new IOException("Completion PS process exited before it was ready.");
                }
            }
            catch { TeardownProcess(); throw; }
        }

        private async Task SendCommandAsync(string command, CancellationToken cancellationToken)
        {
            StreamWriter? stdin;
            lock (_syncRoot) { stdin = _stdin; }
            if (stdin is null) throw new InvalidOperationException("Completion PS session not available.");
            await stdin.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }

        private async Task<string> ExecuteTransportRequestAsync(
            string pwshExecutablePath,
            Func<ActiveRequest, string> commandFactory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var entered = false;
            try
            {
                await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;

                await EnsureProcessReadyAsync(pwshExecutablePath, cancellationToken).ConfigureAwait(false);

                var request = new ActiveRequest();
                lock (_syncRoot) { _activeRequest = request; }

                var command = commandFactory(request);

                try
                {
                    await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ClearActiveRequest(request);
                    throw;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    return await request.CompletionSource.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!request.CompletionSource.Task.IsCompleted)
                    {
                        var reason = cancellationToken.IsCancellationRequested
                            ? "canceled before the completion transport replied"
                            : $"timed out after {timeout.TotalMilliseconds:N0} ms";
                        AbortActiveRequestAndResetProcess(request, reason);
                    }

                    throw;
                }
                finally
                {
                    ClearActiveRequest(request);
                }
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                if (entered)
                {
                    try { _requestGate.Release(); } catch { }
                }
            }
        }

        private async Task ReadLoopAsync(Process owningProcess, TextReader reader, bool isError, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    ProcessIncomingChunk(new string(buffer, 0, read), isError);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            finally
            {
                if (!cancellationToken.IsCancellationRequested || HasExited(owningProcess))
                {
                    HandleProcessExited(owningProcess);
                }
            }
        }

        private void ProcessIncomingChunk(string chunk, bool isError)
        {
            TaskCompletionSource<bool>? readyTcs = null;
            ActiveRequest? request;
            string? completedPayload = null;

            lock (_syncRoot)
            {
                if (isError)
                {
                    _activeRequest?.ErrorCapture.Append(chunk);
                }
                else
                {
                    _sharedOutputTail.Append(chunk);
                    TrimIfNeeded(_sharedOutputTail);

                    if (_readyCompletionSource?.Task.IsCompleted == false &&
                        !string.IsNullOrWhiteSpace(_readyMarker) &&
                        _sharedOutputTail.ToString().Contains(_readyMarker, StringComparison.Ordinal))
                    {
                        readyTcs = _readyCompletionSource;
                        _sharedOutputTail.Clear();
                    }
                }

                request = _activeRequest;
                if (request is not null && !isError)
                {
                    request.Capture.Append(chunk);
                    if (TryExtractPayloadBlock(request.Capture.ToString(), request.StartMarker, request.EndMarker, out var extracted))
                        completedPayload = extracted;
                }
            }

            readyTcs?.TrySetResult(true);
            if (request is not null && completedPayload is not null)
                request.CompletionSource.TrySetResult(completedPayload);
        }

        private void HandleProcessExited(Process exitedProcess)
        {
            ActiveRequest? request;
            TaskCompletionSource<bool>? readyTcs;
            string? runtimePath;
            bool expectedShutdown;
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_process, exitedProcess)) return;
                if (_stdin is null && _readyCompletionSource is null && _activeRequest is null && _activeRuntimePath is null)
                {
                    return;
                }

                request = _activeRequest;
                readyTcs = _readyCompletionSource;
                runtimePath = _activeRuntimePath;
                expectedShutdown = _disposed || _processCancellationTokenSource?.IsCancellationRequested == true;
                _activeRequest = null; _readyCompletionSource = null;
                _stdin = null; _readyMarker = null; _activeRuntimePath = null;
                _sharedOutputTail.Clear();
            }

            if (expectedShutdown)
            {
                readyTcs?.TrySetCanceled();
                request?.CompletionSource.TrySetCanceled();
                return;
            }

            var exitCodeText = TryGetExitCodeText(exitedProcess);
            AppLogger.Warning(
                "EditorCompletion",
                $"Completion PowerShell process exited unexpectedly. Runtime='{runtimePath ?? "(unknown)"}', {exitCodeText}, PendingRequest={request is not null}. PS7 ScriptDesk will recreate the completion session on the next IntelliSense request.");

            var requestErrorText = request?.ErrorCapture.ToString();
            if (!string.IsNullOrWhiteSpace(requestErrorText))
            {
                AppLogger.Debug("EditorCompletion", $"Completion PowerShell stderr before exit: {TrimForLog(requestErrorText)}");
            }

            readyTcs?.TrySetResult(false);
            request?.CompletionSource.TrySetResult(BuildFailurePayload("The completion PowerShell process exited unexpectedly."));
        }

        private void ClearActiveRequest(ActiveRequest request)
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeRequest, request))
                    _activeRequest = null;
            }
        }

        private void TeardownProcess()
        {
            Process? process; CancellationTokenSource? cts;
            TaskCompletionSource<bool>? readyTcs; ActiveRequest? request;
            Task? stdout, stderr;

            lock (_syncRoot)
            {
                process = _process; cts = _processCancellationTokenSource;
                readyTcs = _readyCompletionSource; request = _activeRequest;
                stdout = _stdoutReaderTask; stderr = _stderrReaderTask;
                _process = null; _stdin = null; _processCancellationTokenSource = null;
                _readyCompletionSource = null; _activeRequest = null;
                _readyMarker = null; _activeRuntimePath = null;
                _stdoutReaderTask = null; _stderrReaderTask = null;
                _sharedOutputTail.Clear();
            }

            readyTcs?.TrySetCanceled();
            request?.CompletionSource.TrySetCanceled();
            try { cts?.Cancel(); } catch { }
            try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { stdout?.Wait(250); stderr?.Wait(250); } catch { }
            cts?.Dispose(); process?.Dispose();
        }

        private void AbortActiveRequestAndResetProcess(ActiveRequest request, string reason)
        {
            string? runtimePath;
            bool shouldReset;

            lock (_syncRoot)
            {
                runtimePath = _activeRuntimePath;
                shouldReset = ReferenceEquals(_activeRequest, request) || _process is not null;
            }

            if (!shouldReset)
            {
                return;
            }

            AppLogger.Info(
                "EditorCompletion",
                $"Resetting completion PowerShell session because an in-flight request was abandoned: {reason}. Runtime='{runtimePath ?? "(unknown)"}'.");

            TeardownProcess();
        }

        private static void ObserveBackgroundTask(Task task, string operationName)
        {
            _ = task.ContinueWith(
                completedTask =>
                {
                    var exception = completedTask.Exception;
                    if (exception is null)
                    {
                        return;
                    }

                    _ = exception;
                    AppLogger.Error("EditorCompletion", $"Background {operationName} failed.", exception);
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static string TryGetExitCodeText(Process process)
        {
            try
            {
                return process.HasExited
                    ? $"ExitCode={process.ExitCode}"
                    : "ExitCode=unknown";
            }
            catch
            {
                return "ExitCode=unknown";
            }
        }

        private static string TrimForLog(string text)
        {
            const int maxLength = 512;
            var trimmed = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            return trimmed.Length <= maxLength
                ? trimmed
                : trimmed.Substring(0, maxLength) + "…";
        }

        private static string BuildFailurePayload(string message)
        {
            var response = new
            {
                ok = false,
                msg = string.IsNullOrWhiteSpace(message) ? "The completion PowerShell process exited unexpectedly." : message,
                items = Array.Empty<object>()
            };

            var json = JsonSerializer.Serialize(response);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        private static void TrimIfNeeded(StringBuilder sb)
        {
            const int max = 32768, retain = 16384;
            if (sb.Length <= max) return;
            var tail = sb.ToString(sb.Length - retain, retain);
            sb.Clear(); sb.Append(tail);
        }

        private static string BuildCompletionCommand(string scriptText, int cursorOffset, string startMarker, string endMarker)
        {
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(scriptText));
            var cursor = Math.Max(0, Math.Min(cursorOffset, scriptText.Length));

            return
                "& { " +
                "$ErrorActionPreference = 'Stop'; " +
                "$WarningPreference = 'SilentlyContinue'; " +
                "$ProgressPreference = 'SilentlyContinue'; " +
                "try { " +
                $"$b64='{b64}'; " +
                "$s=[System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String($b64)); " +
                $"$r=[System.Management.Automation.CommandCompletion]::CompleteInput($s,{cursor},$null); " +
                "$items=@($r.CompletionMatches|Select-Object -First 500|ForEach-Object{" +
                "[PSCustomObject]@{t=$_.CompletionText;l=$_.ListItemText;k=[int]$_.ResultType;d=$_.ToolTip}}); " +
                "$resp=[PSCustomObject]@{ok=$true;ri=$r.ReplacementIndex;rl=$r.ReplacementLength;items=@($items)}; " +
                "} catch { " +
                "$resp=[PSCustomObject]@{ok=$false;msg=$_.Exception.Message;ri=0;rl=0;items=@()}; " +
                "}; " +
                "$json=$resp|ConvertTo-Json -Compress -Depth 5; " +
                "$payload=[System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json)); " +
                $"$t=[string]::Concat([Environment]::NewLine,'{startMarker}',[Environment]::NewLine,'PAYLOAD:',$payload,[Environment]::NewLine,'{endMarker}',[Environment]::NewLine); " +
                "[Console]::Out.Write($t); " +
                "[Console]::Out.Flush(); " +
                "}";
        }

        private static string BuildCommandQuickInfoCommand(string commandName, string startMarker, string endMarker, bool includeHelp)
        {
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(commandName));
            var includeHelpLiteral = includeHelp ? "$true" : "$false";

            return $$"""
& {
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'

function Resolve-QuickInfoCommand {
    param([string]$CommandName)

    $commands = @(
        Get-Command -Name $CommandName -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandType -ne [System.Management.Automation.CommandTypes]::Alias }
    )

    if ($commands.Count -eq 0) {
        $commands = @(Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
    }

    if ($commands.Count -eq 0) {
        return $null
    }

    return $commands[0]
}

function Get-CommandMetadataView {
    param($Command)

    if ($null -eq $Command) {
        return $null
    }

    $parameterSets = @()
    $parameterDictionary = $null

    try {
        $commandMetadata = [System.Management.Automation.CommandMetadata]::new($Command)
        if ($null -ne $commandMetadata) {
            $parameterSets = @($commandMetadata.ParameterSets)
            $parameterDictionary = $commandMetadata.Parameters
        }
    }
    catch {
        # Fall back to the original command metadata below.
    }

    if (($null -eq $parameterDictionary -or $parameterDictionary.Count -eq 0)) {
        try {
            $parameterDictionary = $Command.Parameters
        }
        catch {
            $parameterDictionary = $null
        }
    }

    if ($parameterSets.Count -eq 0) {
        try {
            $parameterSets = @($Command.ParameterSets)
        }
        catch {
            $parameterSets = @()
        }
    }

    return [PSCustomObject]@{
        Command = $Command
        ParameterSets = $parameterSets
        Parameters = $parameterDictionary
    }
}

function Build-SyntaxText {
    param(
        [string]$CommandName,
        $ParameterSets
    )

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $resolvedParameterSets = @($ParameterSets | Select-Object -First 6)
    if ($resolvedParameterSets.Count -eq 0) {
        $lines.Add([string]$CommandName)
        return ($lines -join [Environment]::NewLine)
    }

    foreach ($parameterSet in $resolvedParameterSets) {
        $segments = New-Object 'System.Collections.Generic.List[string]'
        $segments.Add([string]$CommandName)

        $orderedParameters = @(
            $parameterSet.Parameters |
                Sort-Object @{ Expression = { if ($_.IsMandatory) { 0 } else { 1 } } }, Position, Name
        )

        foreach ($parameter in ($orderedParameters | Select-Object -First 16)) {
            $segment = '-' + [string]$parameter.Name
            $isSwitch = $false
            if ($null -ne $parameter.ParameterType) {
                $isSwitch = $parameter.ParameterType.FullName -eq 'System.Management.Automation.SwitchParameter'
            }

            if (-not $isSwitch) {
                $typeName = if ($null -ne $parameter.ParameterType -and -not [string]::IsNullOrWhiteSpace([string]$parameter.ParameterType.Name)) {
                    [string]$parameter.ParameterType.Name
                }
                else {
                    'Object'
                }

                $segment += ' <' + $typeName + '>'
            }

            if (-not $parameter.IsMandatory) {
                $segment = '[' + $segment + ']'
            }

            $segments.Add($segment)
        }

        $lines.Add(($segments -join ' '))
    }

    return ($lines -join [Environment]::NewLine)
}

function Build-ParameterItems {
    param($ParameterDictionary)

    if ($null -eq $ParameterDictionary) {
        return @()
    }

    return @(
        $ParameterDictionary.GetEnumerator() |
            Sort-Object Key |
            Select-Object -First 250 |
            ForEach-Object {
                $attrs = @($_.Value.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] })
                $mandatory = [bool](@($attrs | Where-Object { $_.Mandatory } | Select-Object -First 1))
                $positionAttribute = @($attrs | Where-Object { $_.Position -ge 0 } | Select-Object -First 1)
                $position = if ($positionAttribute.Count -gt 0) { [int]$positionAttribute[0].Position } else { $null }
                $aliases = @($_.Value.Aliases)
                $validateValues = @()
                $validateSetAttrs = @($_.Value.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] })
                foreach ($validateSetAttr in $validateSetAttrs) {
                    $validateValues += @($validateSetAttr.ValidValues)
                }

                $type = $_.Value.ParameterType
                $enumValues = if ($type -and $type.IsEnum) { [System.Enum]::GetNames($type) } else { @() }
                $isSwitch = if ($type) { $type.FullName -eq 'System.Management.Automation.SwitchParameter' } else { $false }
                $typeName = if ($type) { [string]$type.Name } else { '' }

                [PSCustomObject]@{
                    n = $_.Key
                    t = $typeName
                    m = $mandatory
                    p = $position
                    a = @($aliases)
                    v = @($validateValues)
                    e = @($enumValues)
                    s = [bool]$isSwitch
                }
            }
    )
}

try {
    $b64 = '{{b64}}'
    $name = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String($b64))
    $includeHelp = {{includeHelpLiteral}}
    $cmd = Resolve-QuickInfoCommand -CommandName $name

    if ($null -eq $cmd) {
        $resp = [PSCustomObject]@{ ok = $false; msg = 'Command not found' }
    }
    else {
        $metadataView = Get-CommandMetadataView -Command $cmd
        $parameterSets = if ($null -ne $metadataView) { @($metadataView.ParameterSets) } else { @() }
        $parameterDictionary = if ($null -ne $metadataView) { $metadataView.Parameters } else { $null }
        $syntax = Build-SyntaxText -CommandName ([string]$cmd.Name) -ParameterSets $parameterSets

        if ($includeHelp) {
            $help = Get-Help -Name $cmd.Name -ErrorAction SilentlyContinue
            $synopsis = if ($help -and $help.Synopsis) { [string]$help.Synopsis } else { '' }
        }
        else {
            $synopsis = ''
        }

        $paramItems = Build-ParameterItems -ParameterDictionary $parameterDictionary
        $resp = [PSCustomObject]@{
            ok = $true
            title = $cmd.Name
            kind = $cmd.CommandType.ToString()
            module = $cmd.ModuleName
            synopsis = $synopsis
            syntax = $syntax
            parameters = @($paramItems)
        }
    }
}
catch {
    $resp = [PSCustomObject]@{ ok = $false; msg = $_.Exception.Message }
}

$json = $resp | ConvertTo-Json -Compress -Depth 7
$payload = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
$t = [string]::Concat([Environment]::NewLine, '{{startMarker}}', [Environment]::NewLine, 'PAYLOAD:', $payload, [Environment]::NewLine, '{{endMarker}}', [Environment]::NewLine)
[Console]::Out.Write($t)
[Console]::Out.Flush()
}
""";
        }

        private static string BuildCommandCatalogCommand(string startMarker, string endMarker)
        {
            var limit = MaxCommandCatalogEntries.ToString(CultureInfo.InvariantCulture);
            return
                "& { " +
                "$ErrorActionPreference = 'Stop'; " +
                "$WarningPreference = 'SilentlyContinue'; " +
                "$ProgressPreference = 'SilentlyContinue'; " +
                "try { " +
                "$items=@(Get-Command -ErrorAction SilentlyContinue | Sort-Object Name,CommandType | Select-Object -First " + limit + " | ForEach-Object { " +
                "$isAlias=($_.CommandType -eq [System.Management.Automation.CommandTypes]::Alias); " +
                "$resolved=if($isAlias){ [string]$_.Definition } else { '' }; " +
                "[PSCustomObject]@{n=$_.Name;t=$_.CommandType.ToString();m=[string]$_.ModuleName;a=[bool]$isAlias;r=$resolved} " +
                "}); " +
                "$resp=[PSCustomObject]@{ok=$true;commands=@($items)}; " +
                "} catch { " +
                "$resp=[PSCustomObject]@{ok=$false;msg=$_.Exception.Message;commands=@()}; " +
                "}; " +
                "$json=$resp|ConvertTo-Json -Compress -Depth 5; " +
                "$payload=[System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json)); " +
                $"$t=[string]::Concat([Environment]::NewLine,'{startMarker}',[Environment]::NewLine,'PAYLOAD:',$payload,[Environment]::NewLine,'{endMarker}',[Environment]::NewLine); " +
                "[Console]::Out.Write($t); " +
                "[Console]::Out.Flush(); " +
                "}";
        }

        private static bool TryExtractPayloadBlock(string text, string startMarker, string endMarker, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrEmpty(text)) return false;

            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var blockStart = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (string.Equals(trimmed, startMarker, StringComparison.Ordinal)) { blockStart = i; continue; }
                if (blockStart >= 0 && string.Equals(trimmed, endMarker, StringComparison.Ordinal))
                {
                    for (var j = blockStart + 1; j < i; j++)
                    {
                        var pl = lines[j].Trim();
                        if (pl.StartsWith("PAYLOAD:", StringComparison.Ordinal))
                        {
                            payload = pl.Substring("PAYLOAD:".Length).Trim();
                            return true;
                        }
                    }
                    return false;
                }
            }
            return false;
        }

        private static CompletionServiceResult ParsePayload(string payload, int cursorOffset, string scriptText)
        {
            if (string.IsNullOrWhiteSpace(payload)) return CompletionServiceResult.Empty;

            string json;
            try { var bytes = Convert.FromBase64String(payload); json = Encoding.UTF8.GetString(bytes); }
            catch { return CompletionServiceResult.Empty; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                    return CompletionServiceResult.Empty;

                var replacementIndex = root.TryGetProperty("ri", out var riEl) ? riEl.GetInt32() : cursorOffset;
                var replacementLength = root.TryGetProperty("rl", out var rlEl) ? rlEl.GetInt32() : 0;

                replacementIndex = Math.Clamp(replacementIndex, 0, scriptText.Length);
                replacementLength = Math.Clamp(replacementLength, 0, scriptText.Length - replacementIndex);

                var items = new List<CompletionServiceItem>();
                if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var itemEl in itemsEl.EnumerateArray())
                    {
                        var completionText = itemEl.TryGetProperty("t", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var listItemText = itemEl.TryGetProperty("l", out var l) ? l.GetString() ?? completionText : completionText;
                        var kind = itemEl.TryGetProperty("k", out var k) ? (CompletionItemKind)k.GetInt32() : CompletionItemKind.Text;
                        var tooltip = itemEl.TryGetProperty("d", out var d) ? d.GetString() ?? string.Empty : string.Empty;

                        if (!string.IsNullOrEmpty(completionText))
                            items.Add(new CompletionServiceItem(completionText, listItemText, kind, tooltip));
                    }
                }

                return new CompletionServiceResult(replacementIndex, replacementLength, items);
            }
            catch { return CompletionServiceResult.Empty; }
        }

        private static PowerShellQuickInfo? ParseQuickInfoPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;

            string json;
            try
            {
                var bytes = Convert.FromBase64String(payload);
                json = Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                    return null;

                var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(title)) return null;

                var kind = root.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? string.Empty : string.Empty;
                var module = root.TryGetProperty("module", out var moduleEl) ? moduleEl.GetString() ?? string.Empty : string.Empty;
                var synopsis = root.TryGetProperty("synopsis", out var synopsisEl) ? synopsisEl.GetString() ?? string.Empty : string.Empty;
                var syntax = root.TryGetProperty("syntax", out var syntaxEl) ? syntaxEl.GetString() ?? string.Empty : string.Empty;

                var parameters = new List<PowerShellParameterQuickInfo>();
                if (root.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var paramEl in paramsEl.EnumerateArray())
                    {
                        var name = paramEl.TryGetProperty("n", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var typeName = paramEl.TryGetProperty("t", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
                        var mandatory = paramEl.TryGetProperty("m", out var mEl) && mEl.ValueKind == JsonValueKind.True;
                        int? position = null;
                        if (paramEl.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt32(out var parsedPosition))
                        {
                            position = parsedPosition;
                        }

                        var aliases = ReadStringArray(paramEl, "a");
                        var validValues = ReadStringArray(paramEl, "v");
                        var enumValues = ReadStringArray(paramEl, "e");
                        var isSwitch = paramEl.TryGetProperty("s", out var switchEl) && switchEl.ValueKind == JsonValueKind.True;

                        parameters.Add(new PowerShellParameterQuickInfo(
                            name,
                            typeName,
                            mandatory,
                            position,
                            aliases,
                            validValues,
                            enumValues,
                            isSwitch));
                    }
                }

                return new PowerShellQuickInfo(title, kind, module, synopsis, syntax, parameters);
            }
            catch { return null; }
        }

        private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
        {
            var values = new List<string>();
            if (!element.TryGetProperty(propertyName, out var arrayElement) ||
                arrayElement.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value) &&
                    !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static PowerShellCommandCatalog ParseCommandCatalogPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return PowerShellCommandCatalog.Empty;

            string json;
            try
            {
                var bytes = Convert.FromBase64String(payload);
                json = Encoding.UTF8.GetString(bytes);
            }
            catch { return PowerShellCommandCatalog.Empty; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                {
                    return PowerShellCommandCatalog.Empty;
                }

                var commands = new List<PowerShellCommandReference>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("commands", out var commandsEl) && commandsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var commandEl in commandsEl.EnumerateArray())
                    {
                        var name = commandEl.TryGetProperty("n", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var kind = commandEl.TryGetProperty("t", out var kindEl) ? kindEl.GetString() ?? string.Empty : string.Empty;
                        var moduleName = commandEl.TryGetProperty("m", out var moduleEl) ? moduleEl.GetString() ?? string.Empty : string.Empty;
                        var isAlias = commandEl.TryGetProperty("a", out var aliasEl) && aliasEl.ValueKind == JsonValueKind.True;
                        var resolvedCommandName = commandEl.TryGetProperty("r", out var resolvedEl) ? resolvedEl.GetString() ?? string.Empty : string.Empty;
                        var key = isAlias ? "alias:" + name : "command:" + name;
                        if (!seen.Add(key)) continue;

                        commands.Add(new PowerShellCommandReference(name, kind, moduleName, isAlias, resolvedCommandName));
                    }
                }

                return commands.Count == 0
                    ? PowerShellCommandCatalog.Empty
                    : new PowerShellCommandCatalog(commands);
            }
            catch { return PowerShellCommandCatalog.Empty; }
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path.Trim(); }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PowerShellCompletionService));
        }

        private sealed class CachedQuickInfo
        {
            public CachedQuickInfo(PowerShellQuickInfo quickInfo, DateTimeOffset cachedAt)
            {
                QuickInfo = quickInfo;
                CachedAt = cachedAt;
            }

            public PowerShellQuickInfo QuickInfo { get; }
            public DateTimeOffset CachedAt { get; }
        }

        private sealed class CachedCommandCatalog
        {
            public CachedCommandCatalog(PowerShellCommandCatalog catalog, DateTimeOffset cachedAt)
            {
                Catalog = catalog;
                CachedAt = cachedAt;
            }

            public PowerShellCommandCatalog Catalog { get; }
            public DateTimeOffset CachedAt { get; }
        }

        private sealed class ActiveRequest
        {
            public ActiveRequest()
            {
                var id = Guid.NewGuid().ToString("N");
                StartMarker = $"##PSSTUDIO_COMP_START_{id}##";
                EndMarker = $"##PSSTUDIO_COMP_END_{id}##";
            }

            public string StartMarker { get; }
            public string EndMarker { get; }
            public StringBuilder Capture { get; } = new();
            public StringBuilder ErrorCapture { get; } = new();
            public TaskCompletionSource<string> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Result types
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class CompletionServiceResult
    {
        public static readonly CompletionServiceResult Empty = new(0, 0, Array.Empty<CompletionServiceItem>());

        public CompletionServiceResult(int replacementIndex, int replacementLength, IReadOnlyList<CompletionServiceItem> items)
        {
            ReplacementIndex = replacementIndex;
            ReplacementLength = replacementLength;
            Items = items;
        }

        public int ReplacementIndex { get; }
        public int ReplacementLength { get; }
        public IReadOnlyList<CompletionServiceItem> Items { get; }
        public bool HasItems => Items.Count > 0;
    }

    public sealed class CompletionServiceItem
    {
        public CompletionServiceItem(string completionText, string listItemText, CompletionItemKind kind, string tooltip)
        {
            CompletionText = completionText;
            ListItemText = listItemText;
            Kind = kind;
            Tooltip = tooltip;
        }

        public string CompletionText { get; }
        public string ListItemText { get; }
        public CompletionItemKind Kind { get; }
        public string Tooltip { get; }
    }

    public sealed class PowerShellQuickInfo
    {
        public PowerShellQuickInfo(
            string title,
            string kind,
            string moduleName,
            string synopsis,
            string syntax,
            IReadOnlyList<PowerShellParameterQuickInfo> parameters)
        {
            Title = title;
            Kind = kind;
            ModuleName = moduleName;
            Synopsis = synopsis;
            Syntax = syntax;
            Parameters = parameters;
        }

        public string Title { get; }
        public string Kind { get; }
        public string ModuleName { get; }
        public string Synopsis { get; }
        public string Syntax { get; }
        public IReadOnlyList<PowerShellParameterQuickInfo> Parameters { get; }
    }

    public sealed class PowerShellParameterQuickInfo
    {
        public PowerShellParameterQuickInfo(
            string name,
            string typeName,
            bool mandatory,
            int? position,
            IReadOnlyList<string>? aliases = null,
            IReadOnlyList<string>? validValues = null,
            IReadOnlyList<string>? enumValues = null,
            bool isSwitch = false)
        {
            Name = name;
            TypeName = typeName;
            Mandatory = mandatory;
            Position = position;
            Aliases = aliases ?? Array.Empty<string>();
            ValidValues = validValues ?? Array.Empty<string>();
            EnumValues = enumValues ?? Array.Empty<string>();
            IsSwitch = isSwitch;
        }

        public string Name { get; }
        public string TypeName { get; }
        public bool Mandatory { get; }
        public int? Position { get; }
        public IReadOnlyList<string> Aliases { get; }
        public IReadOnlyList<string> ValidValues { get; }
        public IReadOnlyList<string> EnumValues { get; }
        public bool IsSwitch { get; }
    }

    public sealed class PowerShellCommandCatalog
    {
        public static readonly PowerShellCommandCatalog Empty = new(Array.Empty<PowerShellCommandReference>());

        public PowerShellCommandCatalog(IReadOnlyList<PowerShellCommandReference> commands)
        {
            Commands = commands;
        }

        public IReadOnlyList<PowerShellCommandReference> Commands { get; }
    }

    public sealed class PowerShellCommandReference
    {
        public PowerShellCommandReference(
            string name,
            string kind,
            string moduleName,
            bool isAlias,
            string resolvedCommandName)
        {
            Name = name;
            Kind = kind;
            ModuleName = moduleName;
            IsAlias = isAlias;
            ResolvedCommandName = resolvedCommandName;
        }

        public string Name { get; }
        public string Kind { get; }
        public string ModuleName { get; }
        public bool IsAlias { get; }
        public string ResolvedCommandName { get; }
    }

    /// <summary>Maps to PowerShell's <c>CompletionResultType</c> enum.</summary>
    public enum CompletionItemKind
    {
        Text             = 0,
        History          = 1,
        Command          = 2,
        ProviderItem     = 3,
        ProviderContainer = 4,
        Property         = 5,
        Method           = 6,
        ParameterName    = 7,
        ParameterValue   = 8,
        Variable         = 9,
        Namespace        = 10,
        Type             = 11,
        Keyword          = 12,
        DynamicKeyword   = 13,
    }
}
