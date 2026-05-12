using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;

namespace PowerShellStudio.Shell.Editor
{
    internal sealed class EditorMetadataCacheManifest
    {
        public int SchemaVersion { get; set; } = 2;
        public string RuntimePath { get; set; } = string.Empty;
        public long RuntimeFileLength { get; set; }
        public long RuntimeLastWriteUtcTicks { get; set; }
        public string RuntimeVersion { get; set; } = string.Empty;
        public string PowerShellEdition { get; set; } = string.Empty;
        public string RuntimeArchitecture { get; set; } = string.Empty;
        public string ModuleFingerprintHash { get; set; } = string.Empty;
        public long BuiltUtcTicks { get; set; }
        public long CreatedUtcTicks { get; set; }
        public int CatalogCount { get; set; }
        public int QuickInfoCount { get; set; }
    }


    internal sealed class EditorMetadataCacheEntryInfo
    {
        public EditorMetadataCacheEntryInfo(
            string cacheDirectory,
            EditorMetadataCacheManifest? manifest,
            long sizeBytes,
            bool isLegacyPathCache)
        {
            CacheDirectory = cacheDirectory ?? string.Empty;
            Manifest = manifest;
            SizeBytes = sizeBytes;
            IsLegacyPathCache = isLegacyPathCache;
        }

        public string CacheDirectory { get; }
        public EditorMetadataCacheManifest? Manifest { get; }
        public long SizeBytes { get; }
        public bool IsLegacyPathCache { get; }
    }

    internal sealed class EditorMetadataCacheProbeCandidateInfo
    {
        public EditorMetadataCacheProbeCandidateInfo(
            string cacheDirectory,
            string manifestPath,
            string snapshotPath,
            bool isLegacyPathCache,
            bool directoryExists,
            bool manifestExists,
            long manifestSizeBytes,
            DateTime? manifestLastWriteUtc,
            bool snapshotExists,
            long snapshotSizeBytes,
            DateTime? snapshotLastWriteUtc,
            EditorMetadataCacheManifest? manifest)
        {
            CacheDirectory = cacheDirectory ?? string.Empty;
            ManifestPath = manifestPath ?? string.Empty;
            SnapshotPath = snapshotPath ?? string.Empty;
            IsLegacyPathCache = isLegacyPathCache;
            DirectoryExists = directoryExists;
            ManifestExists = manifestExists;
            ManifestSizeBytes = Math.Max(0, manifestSizeBytes);
            ManifestLastWriteUtc = manifestLastWriteUtc;
            SnapshotExists = snapshotExists;
            SnapshotSizeBytes = Math.Max(0, snapshotSizeBytes);
            SnapshotLastWriteUtc = snapshotLastWriteUtc;
            Manifest = manifest;
        }

        public string CacheDirectory { get; }
        public string ManifestPath { get; }
        public string SnapshotPath { get; }
        public bool IsLegacyPathCache { get; }
        public bool DirectoryExists { get; }
        public bool ManifestExists { get; }
        public long ManifestSizeBytes { get; }
        public DateTime? ManifestLastWriteUtc { get; }
        public bool SnapshotExists { get; }
        public long SnapshotSizeBytes { get; }
        public DateTime? SnapshotLastWriteUtc { get; }
        public EditorMetadataCacheManifest? Manifest { get; }
    }

    internal sealed class EditorMetadataCacheSnapshot
    {
        public EditorMetadataCacheSnapshot(
            PowerShellCommandCatalog catalog,
            IReadOnlyDictionary<string, PowerShellQuickInfo> quickInfos)
        {
            Catalog = catalog ?? PowerShellCommandCatalog.Empty;
            QuickInfos = quickInfos ?? new Dictionary<string, PowerShellQuickInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public PowerShellCommandCatalog Catalog { get; }
        public IReadOnlyDictionary<string, PowerShellQuickInfo> QuickInfos { get; }
    }

    internal static class EditorMetadataCacheStore
    {
        private static int _legacyCacheMigrationAttempted;

        private const int SnapshotFormatVersion = 1;
        private const string SnapshotFileName = "metadata-cache.bin";
        private const string ManifestFileName = "manifest.json";

        private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static bool TryLoadSnapshot(
            string runtimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture,
            out EditorMetadataCacheSnapshot snapshot,
            out EditorMetadataCacheManifest manifest,
            out string cacheDirectory,
            out bool loadedFromLegacyPathCache)
        {
            var stopwatch = Stopwatch.StartNew();
            snapshot = new EditorMetadataCacheSnapshot(PowerShellCommandCatalog.Empty, new Dictionary<string, PowerShellQuickInfo>(StringComparer.OrdinalIgnoreCase));
            manifest = new EditorMetadataCacheManifest();
            cacheDirectory = string.Empty;
            loadedFromLegacyPathCache = false;

            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                return false;
            }

            var normalizedRuntimePath = NormalizeRuntimePath(runtimePath);
            var candidates = GetCacheDirectoryCandidates(
                normalizedRuntimePath,
                runtimeVersion,
                powerShellEdition,
                runtimeArchitecture);

            foreach (var candidate in candidates)
            {
                if (TryLoadSnapshotFromDirectory(candidate.CacheDirectory, normalizedRuntimePath, out snapshot, out manifest, out var loadReason))
                {
                    stopwatch.Stop();
                    cacheDirectory = candidate.CacheDirectory;
                    loadedFromLegacyPathCache = candidate.IsLegacyPathCache;
                    var snapshotHealth = EditorMetadataSnapshotValidator.BuildHealth(snapshot);
                    AppLogger.Info(
                        "EditorMetadataCache",
                        $"Loaded metadata snapshot in {stopwatch.ElapsedMilliseconds:N0} ms from '{cacheDirectory}'. LegacyPathCache={loadedFromLegacyPathCache}. {EditorMetadataSnapshotValidator.Describe(snapshotHealth)}.");
                    return true;
                }

                AppLogger.Info(
                    "EditorMetadataCache",
                    $"Metadata cache candidate was not usable. Runtime='{normalizedRuntimePath}', CacheDirectory='{candidate.CacheDirectory}', LegacyPathCache={candidate.IsLegacyPathCache}, Reason={loadReason}");
            }

            stopwatch.Stop();
            AppLogger.Info(
                "EditorMetadataCache",
                $"No usable metadata snapshot found for runtime '{normalizedRuntimePath}' after {stopwatch.ElapsedMilliseconds:N0} ms. CandidateCount={candidates.Count}.");
            return false;
        }

        public static bool TryLoadSnapshot(
            string runtimePath,
            out EditorMetadataCacheSnapshot snapshot,
            out EditorMetadataCacheManifest manifest)
        {
            return TryLoadSnapshot(
                runtimePath,
                runtimeVersion: string.Empty,
                powerShellEdition: string.Empty,
                runtimeArchitecture: string.Empty,
                out snapshot,
                out manifest,
                out _,
                out _);
        }

        private static bool TryLoadSnapshotFromDirectory(
            string cacheDirectory,
            string normalizedRuntimePath,
            out EditorMetadataCacheSnapshot snapshot,
            out EditorMetadataCacheManifest manifest,
            out string reason)
        {
            snapshot = new EditorMetadataCacheSnapshot(PowerShellCommandCatalog.Empty, new Dictionary<string, PowerShellQuickInfo>(StringComparer.OrdinalIgnoreCase));
            manifest = new EditorMetadataCacheManifest();
            reason = string.Empty;

            try
            {
                var manifestPath = Path.Combine(cacheDirectory, ManifestFileName);
                var snapshotPath = Path.Combine(cacheDirectory, SnapshotFileName);
                var manifestExists = File.Exists(manifestPath);
                var snapshotExists = File.Exists(snapshotPath);

                AppLogger.Info(
                    "EditorMetadataCache",
                    $"Checking metadata cache candidate for runtime '{normalizedRuntimePath}'. CacheDirectory='{cacheDirectory}', ManifestPath='{manifestPath}', SnapshotPath='{snapshotPath}', ManifestExists={manifestExists}, SnapshotExists={snapshotExists}.");

                if (!manifestExists || !snapshotExists)
                {
                    reason = "Manifest or snapshot file is missing.";
                    return false;
                }

                var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                manifest = JsonSerializer.Deserialize<EditorMetadataCacheManifest>(manifestJson, ManifestSerializerOptions) ?? new EditorMetadataCacheManifest();
                if (!string.Equals(NormalizeRuntimePath(manifest.RuntimePath), normalizedRuntimePath, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Manifest runtime path '{manifest.RuntimePath}' does not match '{normalizedRuntimePath}'.";
                    return false;
                }

                using var fileStream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: false);

                var formatVersion = reader.ReadInt32();
                if (formatVersion != SnapshotFormatVersion)
                {
                    reason = $"Snapshot format version {formatVersion} does not match expected version {SnapshotFormatVersion}.";
                    return false;
                }

                var catalogCount = reader.ReadInt32();
                var commands = new List<PowerShellCommandReference>(catalogCount);
                for (var index = 0; index < catalogCount; index++)
                {
                    commands.Add(new PowerShellCommandReference(
                        reader.ReadString(),
                        reader.ReadString(),
                        reader.ReadString(),
                        reader.ReadBoolean(),
                        reader.ReadString()));
                }

                var quickInfoCount = reader.ReadInt32();
                var quickInfos = new Dictionary<string, PowerShellQuickInfo>(quickInfoCount, StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < quickInfoCount; index++)
                {
                    var key = reader.ReadString();
                    var title = reader.ReadString();
                    var kind = reader.ReadString();
                    var moduleName = reader.ReadString();
                    var synopsis = reader.ReadString();
                    var syntax = reader.ReadString();
                    var parameterCount = reader.ReadInt32();
                    var parameters = new List<PowerShellParameterQuickInfo>(parameterCount);
                    for (var parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
                    {
                        var name = reader.ReadString();
                        var typeName = reader.ReadString();
                        var mandatory = reader.ReadBoolean();
                        var hasPosition = reader.ReadBoolean();
                        var position = hasPosition ? reader.ReadInt32() : (int?)null;
                        var aliases = ReadStringList(reader);
                        var validValues = ReadStringList(reader);
                        var enumValues = ReadStringList(reader);
                        var isSwitch = reader.ReadBoolean();

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

                    quickInfos[key] = new PowerShellQuickInfo(title, kind, moduleName, synopsis, syntax, parameters);
                }

                snapshot = new EditorMetadataCacheSnapshot(new PowerShellCommandCatalog(commands), quickInfos);
                reason = "Loaded.";
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                AppLogger.Warning("EditorMetadataCache", $"Failed to load metadata snapshot from '{cacheDirectory}'. Runtime='{normalizedRuntimePath}'. {ex.Message}");
                snapshot = new EditorMetadataCacheSnapshot(PowerShellCommandCatalog.Empty, new Dictionary<string, PowerShellQuickInfo>(StringComparer.OrdinalIgnoreCase));
                manifest = new EditorMetadataCacheManifest();
                return false;
            }
        }

        public static void SaveSnapshot(
            string runtimePath,
            EditorMetadataCacheSnapshot snapshot,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture,
            string moduleFingerprintHash)
        {
            var stopwatch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                throw new ArgumentException("Runtime path is required.", nameof(runtimePath));
            }

            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var normalizedRuntimePath = NormalizeRuntimePath(runtimePath);
            var cacheDirectory = GetCacheDirectory(normalizedRuntimePath, runtimeVersion, powerShellEdition, runtimeArchitecture);
            Directory.CreateDirectory(cacheDirectory);

            var manifestPath = Path.Combine(cacheDirectory, ManifestFileName);
            var snapshotPath = Path.Combine(cacheDirectory, SnapshotFileName);
            var temporaryManifestPath = manifestPath + ".tmp";
            var temporarySnapshotPath = snapshotPath + ".tmp";

            var runtimeFileInfo = SafeGetFileInfo(normalizedRuntimePath);
            var manifest = new EditorMetadataCacheManifest
            {
                RuntimePath = normalizedRuntimePath,
                RuntimeFileLength = runtimeFileInfo?.Length ?? 0,
                RuntimeLastWriteUtcTicks = runtimeFileInfo?.LastWriteTimeUtc.Ticks ?? 0,
                RuntimeVersion = runtimeVersion?.Trim() ?? string.Empty,
                PowerShellEdition = powerShellEdition?.Trim() ?? string.Empty,
                RuntimeArchitecture = runtimeArchitecture?.Trim() ?? string.Empty,
                ModuleFingerprintHash = moduleFingerprintHash?.Trim() ?? string.Empty,
                BuiltUtcTicks = DateTime.UtcNow.Ticks,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                CatalogCount = snapshot.Catalog.Commands.Count,
                QuickInfoCount = snapshot.QuickInfos.Count,
            };
            var snapshotHealth = EditorMetadataSnapshotValidator.BuildHealth(snapshot);

            using (var fileStream = new FileStream(temporarySnapshotPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(SnapshotFormatVersion);
                writer.Write(snapshot.Catalog.Commands.Count);
                foreach (var command in snapshot.Catalog.Commands)
                {
                    writer.Write(command.Name ?? string.Empty);
                    writer.Write(command.Kind ?? string.Empty);
                    writer.Write(command.ModuleName ?? string.Empty);
                    writer.Write(command.IsAlias);
                    writer.Write(command.ResolvedCommandName ?? string.Empty);
                }

                writer.Write(snapshot.QuickInfos.Count);
                foreach (var pair in snapshot.QuickInfos.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.Write(pair.Key ?? string.Empty);

                    var quickInfo = pair.Value;
                    writer.Write(quickInfo.Title ?? string.Empty);
                    writer.Write(quickInfo.Kind ?? string.Empty);
                    writer.Write(quickInfo.ModuleName ?? string.Empty);
                    writer.Write(quickInfo.Synopsis ?? string.Empty);
                    writer.Write(quickInfo.Syntax ?? string.Empty);
                    writer.Write(quickInfo.Parameters.Count);
                    foreach (var parameter in quickInfo.Parameters)
                    {
                        writer.Write(parameter.Name ?? string.Empty);
                        writer.Write(parameter.TypeName ?? string.Empty);
                        writer.Write(parameter.Mandatory);
                        writer.Write(parameter.Position.HasValue);
                        if (parameter.Position.HasValue)
                        {
                            writer.Write(parameter.Position.Value);
                        }

                        WriteStringList(writer, parameter.Aliases);
                        WriteStringList(writer, parameter.ValidValues);
                        WriteStringList(writer, parameter.EnumValues);
                        writer.Write(parameter.IsSwitch);
                    }
                }
            }

            var manifestJson = JsonSerializer.Serialize(manifest, ManifestSerializerOptions);
            File.WriteAllText(temporaryManifestPath, manifestJson, new UTF8Encoding(false));

            ReplaceFileAtomically(temporarySnapshotPath, snapshotPath);
            ReplaceFileAtomically(temporaryManifestPath, manifestPath);
            stopwatch.Stop();
            AppLogger.Info("EditorMetadataCache", $"Saved metadata snapshot in {stopwatch.ElapsedMilliseconds:N0} ms. CacheDirectory='{cacheDirectory}', SnapshotPath='{snapshotPath}', ManifestPath='{manifestPath}'. {EditorMetadataSnapshotValidator.Describe(snapshotHealth)}.");
        }

        public static string NormalizeRuntimePath(string runtimePath)
        {
            try
            {
                return Path.GetFullPath(runtimePath).Trim();
            }
            catch
            {
                return runtimePath.Trim();
            }
        }

        public static string ComputeModuleFingerprintHash(string? rawModuleFingerprint)
        {
            if (string.IsNullOrWhiteSpace(rawModuleFingerprint))
            {
                return string.Empty;
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawModuleFingerprint));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }

        public static string GetCacheDirectory(string normalizedRuntimePath)
        {
            // Legacy path-only cache directory retained for backward compatibility with caches
            // created before multi-runtime cache identity was added. New saves use the
            // runtime identity overload below so a PowerShell update at the same path does
            // not overwrite the older version's cache.
            var root = GetCacheRootDirectory();
            return Path.Combine(root, ComputeHash(NormalizeRuntimePath(normalizedRuntimePath)));
        }

        public static string GetCacheDirectory(
            string runtimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture)
        {
            var root = GetCacheRootDirectory();
            var normalizedRuntimePath = NormalizeRuntimePath(runtimePath);
            var identity = BuildRuntimeCacheIdentity(
                normalizedRuntimePath,
                runtimeVersion,
                powerShellEdition,
                runtimeArchitecture);
            return Path.Combine(root, ComputeHash(identity));
        }

        public static string BuildRuntimeCacheIdentity(
            string runtimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture)
        {
            return string.Join(
                "|",
                NormalizeRuntimePath(runtimePath),
                (runtimeVersion ?? string.Empty).Trim(),
                (powerShellEdition ?? string.Empty).Trim(),
                (runtimeArchitecture ?? string.Empty).Trim(),
                $"schema:{new EditorMetadataCacheManifest().SchemaVersion}");
        }

        public static IReadOnlyList<EditorMetadataCacheEntryInfo> GetCacheEntries()
        {
            var root = GetCacheRootDirectory();
            if (!Directory.Exists(root))
            {
                return Array.Empty<EditorMetadataCacheEntryInfo>();
            }

            var entries = new List<EditorMetadataCacheEntryInfo>();
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(Path.GetFileName(directory), "Quarantine", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EditorMetadataCacheManifest? manifest = null;
                try
                {
                    var manifestPath = Path.Combine(directory, ManifestFileName);
                    if (File.Exists(manifestPath))
                    {
                        var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                        manifest = JsonSerializer.Deserialize<EditorMetadataCacheManifest>(manifestJson, ManifestSerializerOptions);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("EditorMetadataCache", $"Could not read metadata cache manifest from '{directory}'. {ex.Message}");
                }

                entries.Add(new EditorMetadataCacheEntryInfo(
                    directory,
                    manifest,
                    SafeGetDirectorySize(directory),
                    IsLegacyPathCacheDirectory(directory, manifest)));
            }

            return entries
                .OrderByDescending(entry => entry.Manifest?.CreatedUtcTicks ?? entry.Manifest?.BuiltUtcTicks ?? 0)
                .ThenBy(entry => entry.CacheDirectory, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<EditorMetadataCacheProbeCandidateInfo> GetCacheProbeCandidates(
            string runtimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture)
        {
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                return Array.Empty<EditorMetadataCacheProbeCandidateInfo>();
            }

            var normalizedRuntimePath = NormalizeRuntimePath(runtimePath);
            var candidates = GetCacheDirectoryCandidates(normalizedRuntimePath, runtimeVersion, powerShellEdition, runtimeArchitecture);
            var results = new List<EditorMetadataCacheProbeCandidateInfo>(candidates.Count);

            foreach (var candidate in candidates)
            {
                var manifestPath = Path.Combine(candidate.CacheDirectory, ManifestFileName);
                var snapshotPath = Path.Combine(candidate.CacheDirectory, SnapshotFileName);
                var manifestInfo = SafeGetFileInfo(manifestPath);
                var snapshotInfo = SafeGetFileInfo(snapshotPath);
                EditorMetadataCacheManifest? manifest = null;

                try
                {
                    if (manifestInfo is not null)
                    {
                        var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                        manifest = JsonSerializer.Deserialize<EditorMetadataCacheManifest>(manifestJson, ManifestSerializerOptions);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("EditorMetadataCache", $"Could not inspect metadata cache probe candidate manifest '{manifestPath}'. {ex.Message}");
                }

                results.Add(new EditorMetadataCacheProbeCandidateInfo(
                    candidate.CacheDirectory,
                    manifestPath,
                    snapshotPath,
                    candidate.IsLegacyPathCache,
                    Directory.Exists(candidate.CacheDirectory),
                    manifestInfo is not null,
                    manifestInfo?.Length ?? 0,
                    manifestInfo?.LastWriteTimeUtc,
                    snapshotInfo is not null,
                    snapshotInfo?.Length ?? 0,
                    snapshotInfo?.LastWriteTimeUtc,
                    manifest));
            }

            return results;
        }

        public static bool DeleteCacheForRuntime(
            string runtimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture,
            out string resultMessage)
        {
            resultMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                resultMessage = "No PowerShell runtime path was supplied.";
                return false;
            }

            var normalizedRuntimePath = NormalizeRuntimePath(runtimePath);
            var identityCacheDirectory = GetCacheDirectory(
                normalizedRuntimePath,
                runtimeVersion,
                powerShellEdition,
                runtimeArchitecture);
            var matchingDirectories = new[] { identityCacheDirectory }
                .Concat(GetCacheEntries()
                    .Where(entry => ManifestMatchesRuntimeIdentity(entry.Manifest, normalizedRuntimePath, runtimeVersion, powerShellEdition, runtimeArchitecture))
                    .Select(entry => entry.CacheDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();

            if (matchingDirectories.Count == 0)
            {
                resultMessage = $"No metadata cache exists for '{normalizedRuntimePath}'.";
                return false;
            }

            var deletedCount = 0;
            foreach (var directory in matchingDirectories)
            {
                if (TryDeleteDirectory(directory))
                {
                    deletedCount++;
                    AppLogger.Info("EditorMetadataCache", $"Deleted metadata cache directory '{directory}' for runtime '{normalizedRuntimePath}'.");
                }
            }

            resultMessage = deletedCount == 1
                ? $"Deleted 1 metadata cache for '{normalizedRuntimePath}'."
                : $"Deleted {deletedCount:N0} metadata cache directories for '{normalizedRuntimePath}'.";
            return deletedCount > 0;
        }

        public static bool DeleteAllCaches(out string resultMessage)
        {
            resultMessage = string.Empty;
            var root = GetCacheRootDirectory();
            if (!Directory.Exists(root))
            {
                resultMessage = "No PowerShell editor metadata cache folder exists.";
                return false;
            }

            var deletedDirectories = 0;
            var failedItems = 0;
            foreach (var directory in Directory.EnumerateDirectories(root).ToList())
            {
                if (TryDeleteDirectory(directory))
                {
                    deletedDirectories++;
                }
                else
                {
                    failedItems++;
                }
            }

            foreach (var file in Directory.EnumerateFiles(root).ToList())
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    failedItems++;
                    AppLogger.Warning("EditorMetadataCache", $"Failed to delete metadata cache file '{file}'. {ex.Message}");
                }
            }

            AppLogger.Info("EditorMetadataCache", $"Deleted all metadata cache entries. DeletedDirectories={deletedDirectories:N0}, Failures={failedItems:N0}, Root='{root}'.");
            resultMessage = failedItems == 0
                ? $"Deleted all PowerShell editor metadata caches ({deletedDirectories:N0} cache folders)."
                : $"Deleted {deletedDirectories:N0} cache folders. {failedItems:N0} items could not be deleted; see the app log.";
            return deletedDirectories > 0 && failedItems == 0;
        }

        public static bool QuarantineSnapshot(string runtimePath, string reason, out string quarantinePath)
        {
            quarantinePath = string.Empty;
            if (string.IsNullOrWhiteSpace(runtimePath))
            {
                return false;
            }

            try
            {
                var normalizedRuntimePath = NormalizeRuntimePath(runtimePath);
                var cacheDirectory = GetCacheDirectory(normalizedRuntimePath);
                if (!Directory.Exists(cacheDirectory))
                {
                    return false;
                }

                var quarantineRoot = Path.Combine(GetCacheRootDirectory(), "Quarantine");
                Directory.CreateDirectory(quarantineRoot);

                var safeReason = SanitizeFileNamePart(reason);
                quarantinePath = Path.Combine(quarantineRoot, $"{Path.GetFileName(cacheDirectory)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{safeReason}");
                Directory.Move(cacheDirectory, quarantinePath);
                AppLogger.Warning("EditorMetadataCache", $"Quarantined metadata snapshot for runtime '{normalizedRuntimePath}' to '{quarantinePath}'. Reason={reason}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("EditorMetadataCache", $"Failed to quarantine metadata snapshot for runtime '{runtimePath}'. Reason={reason}. {ex.Message}");
                quarantinePath = string.Empty;
                return false;
            }
        }


        public static bool QuarantineSnapshotDirectory(string cacheDirectory, string reason, out string quarantinePath)
        {
            quarantinePath = string.Empty;
            if (string.IsNullOrWhiteSpace(cacheDirectory))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(cacheDirectory))
                {
                    return false;
                }

                var root = GetCacheRootDirectory();
                var fullCacheDirectory = Path.GetFullPath(cacheDirectory);
                var fullRoot = Path.GetFullPath(root);
                if (!fullCacheDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warning("EditorMetadataCache", $"Refused to quarantine metadata cache outside cache root. CacheDirectory='{cacheDirectory}', Root='{root}'.");
                    return false;
                }

                var quarantineRoot = Path.Combine(root, "Quarantine");
                Directory.CreateDirectory(quarantineRoot);

                var safeReason = SanitizeFileNamePart(reason);
                quarantinePath = Path.Combine(quarantineRoot, $"{Path.GetFileName(cacheDirectory)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{safeReason}");
                Directory.Move(cacheDirectory, quarantinePath);
                AppLogger.Warning("EditorMetadataCache", $"Quarantined metadata snapshot directory '{cacheDirectory}' to '{quarantinePath}'. Reason={reason}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("EditorMetadataCache", $"Failed to quarantine metadata snapshot directory '{cacheDirectory}'. Reason={reason}. {ex.Message}");
                quarantinePath = string.Empty;
                return false;
            }
        }

        public static string GetCacheRootDirectory()
        {
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationBranding.InternalName,
                "EditorMetadataCache");

            TryMigrateLegacyCacheRoot(cacheRoot);
            return cacheRoot;
        }

        private static void TryMigrateLegacyCacheRoot(string cacheRoot)
        {
            if (Interlocked.Exchange(ref _legacyCacheMigrationAttempted, 1) != 0)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(cacheRoot) || Directory.Exists(cacheRoot))
                {
                    return;
                }

                var legacyCacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ApplicationBranding.LegacyInternalName,
                    "EditorMetadataCache");

                if (!Directory.Exists(legacyCacheRoot))
                {
                    return;
                }

                CopyDirectory(legacyCacheRoot, cacheRoot);
                AppLogger.Info("EditorMetadataCache", $"Migrated legacy editor metadata cache from '{legacyCacheRoot}' to '{cacheRoot}'.");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("EditorMetadataCache", $"Legacy metadata cache migration skipped: {ex.Message}");
            }
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
            {
                var destinationFilePath = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
                File.Copy(filePath, destinationFilePath, overwrite: false);
            }

            foreach (var childDirectoryPath in Directory.EnumerateDirectories(sourceDirectory))
            {
                var destinationChildDirectoryPath = Path.Combine(destinationDirectory, Path.GetFileName(childDirectoryPath));
                CopyDirectory(childDirectoryPath, destinationChildDirectoryPath);
            }
        }

        private static IReadOnlyList<(string CacheDirectory, bool IsLegacyPathCache)> GetCacheDirectoryCandidates(
            string normalizedRuntimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture)
        {
            var identityDirectory = GetCacheDirectory(normalizedRuntimePath, runtimeVersion, powerShellEdition, runtimeArchitecture);
            var legacyDirectory = GetCacheDirectory(normalizedRuntimePath);
            var candidates = new List<(string CacheDirectory, bool IsLegacyPathCache)>
            {
                (identityDirectory, false),
            };

            if (!string.Equals(identityDirectory, legacyDirectory, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((legacyDirectory, true));
            }

            return candidates;
        }

        private static bool ManifestMatchesRuntimeIdentity(
            EditorMetadataCacheManifest? manifest,
            string normalizedRuntimePath,
            string runtimeVersion,
            string powerShellEdition,
            string runtimeArchitecture)
        {
            if (manifest is null)
            {
                return false;
            }

            return string.Equals(NormalizeRuntimePath(manifest.RuntimePath), normalizedRuntimePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((manifest.RuntimeVersion ?? string.Empty).Trim(), (runtimeVersion ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((manifest.PowerShellEdition ?? string.Empty).Trim(), (powerShellEdition ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((manifest.RuntimeArchitecture ?? string.Empty).Trim(), (runtimeArchitecture ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacyPathCacheDirectory(string directory, EditorMetadataCacheManifest? manifest)
        {
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.RuntimePath))
            {
                return false;
            }

            var legacyDirectory = GetCacheDirectory(NormalizeRuntimePath(manifest.RuntimePath));
            return string.Equals(legacyDirectory, directory, StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeHash(string value)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var hashBuilder = new StringBuilder(hashBytes.Length * 2);
            foreach (var item in hashBytes)
            {
                hashBuilder.Append(item.ToString("x2"));
            }

            return hashBuilder.ToString();
        }

        private static bool TryDeleteDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return false;
                }

                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("EditorMetadataCache", $"Failed to delete metadata cache directory '{directory}'. {ex.Message}");
                return false;
            }
        }

        private static long SafeGetDirectorySize(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return 0;
                }

                return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Sum(file =>
                    {
                        try
                        {
                            return new FileInfo(file).Length;
                        }
                        catch
                        {
                            return 0L;
                        }
                    });
            }
            catch
            {
                return 0;
            }
        }

        private static FileInfo? SafeGetFileInfo(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void ReplaceFileAtomically(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }

            File.Move(temporaryPath, destinationPath);
        }

        private static string SanitizeFileNamePart(string? value)
        {
            const string fallback = "metadata";
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var invalidCharacters = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                builder.Append(invalidCharacters.Contains(character) ? '_' : character);
            }

            var sanitized = builder.ToString().Trim();
            if (sanitized.Length > 64)
            {
                sanitized = sanitized.Substring(0, 64);
            }

            return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
        }

        private static void WriteStringList(BinaryWriter writer, IReadOnlyList<string>? values)
        {
            writer.Write(values?.Count ?? 0);
            if (values is null)
            {
                return;
            }

            foreach (var value in values)
            {
                writer.Write(value ?? string.Empty);
            }
        }

        private static List<string> ReadStringList(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var items = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                items.Add(reader.ReadString());
            }

            return items;
        }
    }
}
