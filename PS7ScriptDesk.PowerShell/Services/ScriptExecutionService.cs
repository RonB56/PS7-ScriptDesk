using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services
{
    public class ScriptExecutionService : IScriptExecutionService
    {
        private static readonly Regex LegacySnapshotFileNamePattern = new(@"^\d{8}_\d{6}_\d{3}_.+\.ps1$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private const string ExecutionSnapshotFilePrefix = "psstudio-exec-";
        private readonly object _syncRoot = new();
        private Process? _currentProcess;
        private string? _currentSnapshotPath;
        private bool _stopRequested;

        public bool IsExecutionInProgress
        {
            get
            {
                lock (_syncRoot)
                {
                    return _currentProcess is not null;
                }
            }
        }

        public async Task<ScriptExecutionResult> ExecuteScriptAsync(
            PowerShellRuntimeInfo runtime,
            string documentDisplayName,
            string scriptContent,
            Action<ExecutionOutputRecord> onOutput,
            CancellationToken cancellationToken = default)
        {
            if (runtime is null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            if (onOutput is null)
            {
                throw new ArgumentNullException(nameof(onOutput));
            }

            var snapshotPath = CreateExecutionSnapshot(documentDisplayName, scriptContent);
            var startedAt = DateTime.Now;
            var stopRequestedForThisRun = false;

            using var process = new Process
            {
                StartInfo = BuildStartInfo(runtime, snapshotPath),
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    onOutput(new ExecutionOutputRecord(ExecutionOutputStreamKind.StandardOutput, args.Data, DateTime.Now));
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    onOutput(new ExecutionOutputRecord(ExecutionOutputStreamKind.StandardError, args.Data, DateTime.Now));
                }
            };

            try
            {
                lock (_syncRoot)
                {
                    if (_currentProcess is not null)
                    {
                        throw new InvalidOperationException("A PowerShell execution is already running.");
                    }

                    _stopRequested = false;
                    _currentProcess = process;
                    _currentSnapshotPath = snapshotPath;
                }

                onOutput(new ExecutionOutputRecord(
                    ExecutionOutputStreamKind.Lifecycle,
                    $"Starting execution snapshot for {documentDisplayName}",
                    DateTime.Now));

                if (!process.Start())
                {
                    throw new InvalidOperationException("The PowerShell process could not be started.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cancellationRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        // StopExecutionAsync is synchronous in practice (returns Task.FromResult)
                        // so fire-and-forget is safe and avoids blocking the thread-pool thread
                        // that the cancellation callback runs on.
                        _ = StopExecutionAsync();
                    }
                    catch
                    {
                        // Best effort stop only.
                    }
                });

                await process.WaitForExitAsync().ConfigureAwait(false);
                process.WaitForExit();

                lock (_syncRoot)
                {
                    stopRequestedForThisRun = _stopRequested;
                }

                return new ScriptExecutionResult(
                    runtime.DisplayName,
                    process.ExitCode,
                    stopRequestedForThisRun,
                    snapshotPath,
                    startedAt,
                    DateTime.Now);
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_currentProcess, process))
                    {
                        _currentProcess = null;
                        _currentSnapshotPath = null;
                        _stopRequested = false;
                    }
                }

                TryDeleteSnapshot(snapshotPath);
            }
        }

        public Task<bool> StopExecutionAsync()
        {
            Process? processToStop;

            lock (_syncRoot)
            {
                processToStop = _currentProcess;

                if (processToStop is null)
                {
                    return Task.FromResult(false);
                }

                _stopRequested = true;
            }

            try
            {
                if (!processToStop.HasExited)
                {
                    processToStop.Kill(entireProcessTree: true);
                }

                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        private static ProcessStartInfo BuildStartInfo(PowerShellRuntimeInfo runtime, string snapshotPath)
        {
            var commandText = BuildEncodedCommand(snapshotPath);

            return new ProcessStartInfo
            {
                FileName = runtime.ExecutablePath,
                Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {commandText}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(snapshotPath) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        private static string BuildEncodedCommand(string snapshotPath)
        {
            var escapedSnapshotPath = snapshotPath.Replace("'", "''", StringComparison.Ordinal);

            var command = string.Join(
                Environment.NewLine,
                "$ProgressPreference = 'SilentlyContinue'",
                "$WarningPreference = 'Continue'",
                "$VerbosePreference = 'Continue'",
                "$DebugPreference = 'Continue'",
                "$InformationPreference = 'Continue'",
                $"& '{escapedSnapshotPath}' *>&1",
                "if ($LASTEXITCODE -ne $null) { exit $LASTEXITCODE }",
                "exit 0");

            return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        private static string CreateExecutionSnapshot(string documentDisplayName, string scriptContent)
        {
            var rootDirectory = GetSnapshotRootDirectory(createIfMissing: true);

            var safeName = MakeSafeFileName(documentDisplayName);
            var fileName = $"{ExecutionSnapshotFilePrefix}{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{Guid.NewGuid():N}_{safeName}.ps1";
            var fullPath = Path.Combine(rootDirectory, fileName);

            File.WriteAllText(fullPath, scriptContent ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fullPath;
        }

        private static string MakeSafeFileName(string documentDisplayName)
        {
            var baseName = string.IsNullOrWhiteSpace(documentDisplayName)
                ? "Untitled"
                : Path.GetFileNameWithoutExtension(documentDisplayName);

            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(baseName) ? "Untitled" : baseName;
        }

        private static void TryDeleteSnapshot(string snapshotPath)
        {
            if (!TryValidateManagedSnapshotPath(snapshotPath, out var normalizedRootDirectory, out var normalizedSnapshotPath))
            {
                return;
            }

            try
            {
                if (File.Exists(normalizedSnapshotPath))
                {
                    File.Delete(normalizedSnapshotPath);
                    AppLogger.Info("ScriptExecution", $"Deleted execution snapshot '{Path.GetFileName(normalizedSnapshotPath)}' from '{normalizedRootDirectory}'.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("ScriptExecution", $"Failed to delete execution snapshot '{normalizedSnapshotPath}'. {ex.Message}");
            }
        }

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
