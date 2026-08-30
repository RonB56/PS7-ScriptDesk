using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services
{
    public sealed class DocumentRecoveryService : IDocumentRecoveryService
    {
        private const int SchemaVersion = 1;
        private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _recoveryStorageDirectory;
        private readonly TimeSpan _recoveryWriteDelay;

        public DocumentRecoveryService()
            : this(Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "CrashRecovery"), TimeSpan.FromSeconds(2))
        {
        }

        public DocumentRecoveryService(string recoveryStorageDirectory, TimeSpan? recoveryWriteDelay = null)
        {
            if (string.IsNullOrWhiteSpace(recoveryStorageDirectory))
            {
                throw new ArgumentException("Recovery storage directory must be provided.", nameof(recoveryStorageDirectory));
            }

            _recoveryStorageDirectory = Path.GetFullPath(recoveryStorageDirectory);
            _recoveryWriteDelay = recoveryWriteDelay.GetValueOrDefault(TimeSpan.FromSeconds(2));
        }

        public string RecoveryStorageDirectory => _recoveryStorageDirectory;

        public TimeSpan RecoveryWriteDelay => _recoveryWriteDelay;

        public IReadOnlyList<DocumentRecoveryCandidate> GetRecoverableDocuments()
        {
            if (!Directory.Exists(_recoveryStorageDirectory))
            {
                return Array.Empty<DocumentRecoveryCandidate>();
            }

            var candidates = new List<DocumentRecoveryCandidate>();
            foreach (var recoveryFilePath in Directory.EnumerateFiles(_recoveryStorageDirectory, "*.ps7recovery.json", SearchOption.TopDirectoryOnly))
            {
                RecoveryFileModel? model = null;
                try
                {
                    model = JsonSerializer.Deserialize<RecoveryFileModel>(
                        File.ReadAllText(recoveryFilePath),
                        SerializerOptions);

                    if (model is null ||
                        model.SchemaVersion != SchemaVersion ||
                        string.IsNullOrWhiteSpace(model.RecoveryId) ||
                        string.IsNullOrWhiteSpace(model.DisplayName) ||
                        model.Content is null)
                    {
                        QuarantineInvalidRecoveryFile(recoveryFilePath, "InvalidOrUnsupportedMetadata");
                        continue;
                    }

                    var status = EvaluateOriginalFileStatus(model);
                    candidates.Add(new DocumentRecoveryCandidate(
                        model.RecoveryId,
                        NormalizePathOrNull(model.OriginalFilePath),
                        model.DisplayName,
                        model.Content,
                        model.SnapshotCreatedUtc,
                        model.LastRecoveryWriteUtc,
                        model.OriginalLastWriteTimeUtc,
                        model.OriginalLength,
                        model.OriginalContentSha256,
                        model.IsUntitled,
                        status.Status,
                        status.Description));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    DeveloperDiagnostics.LogException(
                        "CrashRecovery",
                        ex,
                        "Recovery artifact could not be read; it will be quarantined without blocking startup.",
                        new Dictionary<string, object?>
                        {
                            ["recoveryFileName"] = Path.GetFileName(recoveryFilePath),
                            ["recoveryId"] = model?.RecoveryId
                        });
                    QuarantineInvalidRecoveryFile(recoveryFilePath, "UnreadableOrCorrupt");
                }
            }

            DeveloperDiagnostics.LogInfo(
                "CrashRecovery",
                "Crash recovery scan completed.",
                new Dictionary<string, object?>
                {
                    ["storageDirectory"] = _recoveryStorageDirectory,
                    ["candidateCount"] = candidates.Count
                });

            return candidates
                .OrderBy(static candidate => candidate.LastRecoveryWriteUtc)
                .ToArray();
        }

        public bool SaveSnapshot(DocumentRecoverySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (string.IsNullOrWhiteSpace(snapshot.RecoveryId))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(_recoveryStorageDirectory);
                var model = new RecoveryFileModel
                {
                    SchemaVersion = SchemaVersion,
                    RecoveryId = snapshot.RecoveryId,
                    OriginalFilePath = NormalizePathOrNull(snapshot.OriginalFilePath),
                    DisplayName = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? "Untitled.ps1" : snapshot.DisplayName,
                    Content = snapshot.Content ?? string.Empty,
                    SnapshotCreatedUtc = snapshot.SnapshotCreatedUtc,
                    LastRecoveryWriteUtc = DateTime.UtcNow,
                    OriginalLastWriteTimeUtc = snapshot.OriginalLastWriteTimeUtc,
                    OriginalLength = snapshot.OriginalLength,
                    OriginalContentSha256 = NormalizeHashOrNull(snapshot.OriginalContentSha256),
                    IsUntitled = snapshot.IsUntitled
                };

                var targetPath = GetRecoveryFilePath(snapshot.RecoveryId);
                var temporaryPath = Path.Combine(
                    _recoveryStorageDirectory,
                    $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
                var json = JsonSerializer.Serialize(model, SerializerOptions);

                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 64 * 1024,
                           options: FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 64 * 1024, leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(targetPath))
                {
                    File.Replace(temporaryPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, targetPath);
                }

                DeveloperDiagnostics.LogInfo(
                    "CrashRecovery",
                    "Recovery snapshot saved.",
                    new Dictionary<string, object?>
                    {
                        ["recoveryId"] = snapshot.RecoveryId,
                        ["recoveryFileName"] = Path.GetFileName(targetPath),
                        ["originalPath"] = model.OriginalFilePath,
                        ["isUntitled"] = model.IsUntitled,
                        ["contentLength"] = model.Content.Length
                    });
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                DeveloperDiagnostics.LogException(
                    "CrashRecovery",
                    ex,
                    "Recovery snapshot write failed without blocking editor input.",
                    new Dictionary<string, object?>
                    {
                        ["recoveryId"] = snapshot.RecoveryId,
                        ["originalPath"] = snapshot.OriginalFilePath,
                        ["isUntitled"] = snapshot.IsUntitled,
                        ["contentLength"] = snapshot.Content?.Length ?? 0
                    });
                return false;
            }
        }

        public bool DiscardRecovery(string recoveryId)
        {
            if (string.IsNullOrWhiteSpace(recoveryId))
            {
                return false;
            }

            try
            {
                var targetPath = GetRecoveryFilePath(recoveryId);
                if (!File.Exists(targetPath))
                {
                    return false;
                }

                File.Delete(targetPath);
                DeveloperDiagnostics.LogInfo(
                    "CrashRecovery",
                    "Recovery snapshot discarded.",
                    new Dictionary<string, object?>
                    {
                        ["recoveryId"] = recoveryId,
                        ["recoveryFileName"] = Path.GetFileName(targetPath)
                    });
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                DeveloperDiagnostics.LogException(
                    "CrashRecovery",
                    ex,
                    "Recovery snapshot discard failed.",
                    new Dictionary<string, object?> { ["recoveryId"] = recoveryId });
                return false;
            }
        }

        private FileStatusEvaluation EvaluateOriginalFileStatus(RecoveryFileModel model)
        {
            var originalPath = NormalizePathOrNull(model.OriginalFilePath);
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                return new FileStatusEvaluation(DocumentRecoveryFileStatus.Unknown, "Untitled document; no original file path exists.");
            }

            try
            {
                var fileInfo = new FileInfo(originalPath);
                fileInfo.Refresh();
                if (!fileInfo.Exists)
                {
                    return new FileStatusEvaluation(DocumentRecoveryFileStatus.OriginalMissing, "Original file is missing or was moved.");
                }

                var metadataMatches =
                    model.OriginalLastWriteTimeUtc.HasValue &&
                    model.OriginalLength.HasValue &&
                    fileInfo.LastWriteTimeUtc == model.OriginalLastWriteTimeUtc.Value &&
                    fileInfo.Length == model.OriginalLength.Value;

                if (metadataMatches)
                {
                    return new FileStatusEvaluation(DocumentRecoveryFileStatus.OriginalUnchanged, "Original file appears unchanged since the recovery snapshot.");
                }

                if (string.IsNullOrWhiteSpace(model.OriginalContentSha256))
                {
                    return new FileStatusEvaluation(DocumentRecoveryFileStatus.OriginalModified, "Original file metadata changed after the recovery snapshot.");
                }

                var currentHash = ComputeSha256(File.ReadAllText(originalPath));
                return string.Equals(currentHash, model.OriginalContentSha256, StringComparison.OrdinalIgnoreCase)
                    ? new FileStatusEvaluation(DocumentRecoveryFileStatus.OriginalUnchanged, "Original file metadata changed, but its content still matches the recovery baseline.")
                    : new FileStatusEvaluation(DocumentRecoveryFileStatus.OriginalModified, "Original file content changed after the recovery snapshot.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                DeveloperDiagnostics.LogException(
                    "CrashRecovery",
                    ex,
                    "Original file status could not be evaluated during recovery scan.",
                    new Dictionary<string, object?>
                    {
                        ["recoveryId"] = model.RecoveryId,
                        ["originalPath"] = originalPath
                    });
                return new FileStatusEvaluation(DocumentRecoveryFileStatus.Unknown, "Original file status could not be verified.");
            }
        }

        private string GetRecoveryFilePath(string recoveryId)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recoveryId.Trim()))).ToLowerInvariant();
            return Path.Combine(_recoveryStorageDirectory, $"{hash}.ps7recovery.json");
        }

        private static string? NormalizePathOrNull(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(filePath.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static string? NormalizeHashOrNull(string? hash)
            => string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();

        private static string ComputeSha256(string content)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));

        private static void QuarantineInvalidRecoveryFile(string recoveryFilePath, string reason)
        {
            try
            {
                if (!File.Exists(recoveryFilePath))
                {
                    return;
                }

                var quarantinePath = $"{recoveryFilePath}.{reason}.{DateTime.UtcNow:yyyyMMddHHmmss}.corrupt";
                File.Move(recoveryFilePath, quarantinePath);
                DeveloperDiagnostics.LogWarning(
                    "CrashRecovery",
                    "Invalid recovery artifact was quarantined.",
                    new Dictionary<string, object?>
                    {
                        ["recoveryFileName"] = Path.GetFileName(recoveryFilePath),
                        ["quarantineFileName"] = Path.GetFileName(quarantinePath),
                        ["reason"] = reason
                    });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                DeveloperDiagnostics.LogException(
                    "CrashRecovery",
                    ex,
                    "Invalid recovery artifact could not be quarantined.",
                    new Dictionary<string, object?>
                    {
                        ["recoveryFileName"] = Path.GetFileName(recoveryFilePath),
                        ["reason"] = reason
                    });
            }
        }

        private sealed record FileStatusEvaluation(DocumentRecoveryFileStatus Status, string Description);

        private sealed class RecoveryFileModel
        {
            public int SchemaVersion { get; set; }

            public string RecoveryId { get; set; } = string.Empty;

            public string? OriginalFilePath { get; set; }

            public string DisplayName { get; set; } = string.Empty;

            public string? Content { get; set; }

            public DateTime SnapshotCreatedUtc { get; set; }

            public DateTime LastRecoveryWriteUtc { get; set; }

            public DateTime? OriginalLastWriteTimeUtc { get; set; }

            public long? OriginalLength { get; set; }

            public string? OriginalContentSha256 { get; set; }

            public bool IsUntitled { get; set; }
        }
    }
}
