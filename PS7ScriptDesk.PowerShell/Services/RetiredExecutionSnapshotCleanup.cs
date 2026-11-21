using System;
using System.IO;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.PowerShell.Services
{
    // Removes snapshots left by the retired process-per-run execution service.
    public static class RetiredExecutionSnapshotCleanup
    {
        private static readonly Regex LegacySnapshotFileNamePattern = new(@"^\d{8}_\d{6}_\d{3}_.+\.ps1$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private const string ExecutionSnapshotFilePrefix = "psstudio-exec-";

        public static void CleanupStaleExecutionSnapshots()
        {
            try
            {
                var rootDirectory = GetSnapshotRootDirectory(createIfMissing: false);
                if (!Directory.Exists(rootDirectory))
                {
                    return;
                }

                AppLogger.Info("ScriptExecution", $"Cleaning stale execution snapshots from '{rootDirectory}'.");
                foreach (var file in Directory.EnumerateFiles(rootDirectory, "*.ps1", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (!TryValidateManagedSnapshotPath(file, out _, out var normalizedSnapshotPath))
                        {
                            continue;
                        }

                        var fileName = Path.GetFileName(file);
                        if (!IsManagedSnapshotFileName(fileName))
                        {
                            continue;
                        }

                        File.Delete(normalizedSnapshotPath);
                        AppLogger.Info("ScriptExecution", $"Deleted stale execution snapshot '{Path.GetFileName(normalizedSnapshotPath)}'.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("ScriptExecution", $"Failed to delete stale execution snapshot '{file}'. {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("ScriptExecution", $"Stale execution snapshot cleanup failed. {ex.Message}");
            }
        }

        private static string GetSnapshotRootDirectory(bool createIfMissing)
        {
            if (!AppTemporaryStorage.TryGetManagedRootDirectory("ExecutionSnapshots", createIfMissing, out var rootDirectory, out var failureReason))
            {
                throw new IOException($"Execution snapshot storage is unavailable. {failureReason}");
            }

            return rootDirectory;
        }

        private static bool IsManagedSnapshotFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return fileName.StartsWith(ExecutionSnapshotFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                   LegacySnapshotFileNamePattern.IsMatch(fileName);
        }

        private static bool TryValidateManagedSnapshotPath(string snapshotPath, out string normalizedRootDirectory, out string normalizedSnapshotPath)
        {
            normalizedRootDirectory = string.Empty;
            normalizedSnapshotPath = string.Empty;

            try
            {
                var rootDirectory = GetSnapshotRootDirectory(createIfMissing: false);
                if (!AppTemporaryStorage.TryValidateManagedPath(rootDirectory, snapshotPath, out normalizedRootDirectory, out normalizedSnapshotPath, out var failureReason))
                {
                    AppLogger.Warning("ScriptExecution", $"Skipped execution snapshot deletion outside the managed temp root. Path='{snapshotPath}'. {failureReason}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("ScriptExecution", $"Skipped execution snapshot deletion because the managed temp root could not be resolved. Path='{snapshotPath}'. {ex.Message}");
                return false;
            }
        }
    }
}
