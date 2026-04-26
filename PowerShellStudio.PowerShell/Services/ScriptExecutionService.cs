using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.PowerShell.Services
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
            var rootDirectory = GetSnapshotRootDirectory();
            Directory.CreateDirectory(rootDirectory);

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
            try
            {
                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        public static void CleanupStaleExecutionSnapshots()
        {
            try
            {
                var rootDirectory = GetSnapshotRootDirectory();
                if (!Directory.Exists(rootDirectory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(rootDirectory, "*.ps1", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        if (!IsManagedSnapshotFileName(fileName))
                        {
                            continue;
                        }

                        File.Delete(file);
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        private static string GetSnapshotRootDirectory()
        {
            return Path.Combine(Path.GetTempPath(), "PowerShellStudio", "ExecutionSnapshots");
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
    }
}
