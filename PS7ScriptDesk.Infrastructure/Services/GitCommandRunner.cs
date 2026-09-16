using System.Diagnostics;
using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public sealed class GitCommandRunner : IGitCommandRunner
{
    private const int DiagnosticPreviewLength = 1000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _environmentGate = new(1, 1);
    private GitEnvironmentInfo? _environment;

    public async Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await _environmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_environment is not null)
            {
                return _environment;
            }

            var stopwatch = Stopwatch.StartNew();
            foreach (var candidate in GetExecutableCandidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await RunProcessAsync(
                    candidate,
                    workingDirectory: Environment.CurrentDirectory,
                    arguments: new[] { "--version" },
                    timeout: TimeSpan.FromSeconds(5),
                    cancellationToken,
                    logOperation: false).ConfigureAwait(false);

                if (!result.Success)
                {
                    continue;
                }

                var version = ParseVersion(result.StandardOutput);
                _environment = new GitEnvironmentInfo(
                    IsAvailable: true,
                    ExecutablePath: candidate,
                    Version: version,
                    Error: null,
                    FailureKind: null);

                DeveloperDiagnostics.LogOperationStop(
                    "Git",
                    "DiscoverEnvironment",
                    "Git executable discovered.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["executablePath"] = candidate,
                        ["version"] = version,
                        ["candidateCount"] = GetExecutableCandidates().Count
                    });
                return _environment;
            }

            _environment = GitEnvironmentInfo.Unavailable(
                "Git could not be found or launched.",
                "NotFound");
            DeveloperDiagnostics.LogOperationStop(
                "Git",
                "DiscoverEnvironment",
                "Git executable was not discovered.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["candidateCount"] = GetExecutableCandidates().Count,
                    ["failureKind"] = _environment.FailureKind
                });
            return _environment;
        }
        finally
        {
            _environmentGate.Release();
        }
    }

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var environment = await GetEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!environment.IsAvailable || string.IsNullOrWhiteSpace(environment.ExecutablePath))
        {
            return GitCommandResult.Failed(
                environment.FailureKind ?? "Unavailable",
                environment.Error ?? "Git is unavailable.",
                TimeSpan.Zero);
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return GitCommandResult.Failed("InvalidWorkingDirectory", "The Git working directory does not exist.", TimeSpan.Zero);
        }

        var normalizedWorkingDirectory = Path.GetFullPath(workingDirectory);
        var repositoryScopedArguments = new List<string>(arguments.Count + 2)
        {
            "-c",
            $"safe.directory={normalizedWorkingDirectory}"
        };
        repositoryScopedArguments.AddRange(arguments);

        return await RunProcessAsync(
            environment.ExecutablePath,
            normalizedWorkingDirectory,
            repositoryScopedArguments,
            timeout ?? DefaultTimeout,
            cancellationToken,
            logOperation: true).ConfigureAwait(false);
    }

    private static async Task<GitCommandResult> RunProcessAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool logOperation)
    {
        var stopwatch = Stopwatch.StartNew();
        if (timeout <= TimeSpan.Zero)
        {
            return new GitCommandResult(false, null, string.Empty, "Git operation timed out.", stopwatch.Elapsed, false, true, "Timeout");
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, workingDirectory, arguments),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return GitCommandResult.Failed("LaunchFailed", "Git could not be started.", stopwatch.Elapsed);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var wasCancelled = false;
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                wasCancelled = cancellationToken.IsCancellationRequested;
                timedOut = !wasCancelled;
                TryKill(process);
            }

            var standardOutput = await stdoutTask.ConfigureAwait(false);
            var standardError = await stderrTask.ConfigureAwait(false);
            stopwatch.Stop();

            int? exitCode = process.HasExited ? process.ExitCode : null;
            var success = !wasCancelled && !timedOut && exitCode == 0;
            var failureKind = wasCancelled
                ? "Cancelled"
                : timedOut
                    ? "Timeout"
                    : success
                        ? null
                        : "GitExitCode";

            var result = new GitCommandResult(
                success,
                exitCode,
                standardOutput,
                standardError,
                stopwatch.Elapsed,
                wasCancelled,
                timedOut,
                failureKind);

            if (logOperation)
            {
                DeveloperDiagnostics.LogInfo(
                    "Git",
                    "Git command completed.",
                    new Dictionary<string, object?>
                    {
                        ["workingDirectory"] = workingDirectory,
                        ["executablePath"] = executablePath,
                        ["arguments"] = arguments.ToArray(),
                        ["argumentCount"] = arguments.Count,
                        ["durationMilliseconds"] = stopwatch.ElapsedMilliseconds,
                        ["exitCode"] = exitCode,
                        ["success"] = success,
                        ["wasCancelled"] = wasCancelled,
                        ["stdoutLength"] = standardOutput.Length,
                        ["stdoutLineCount"] = standardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length,
                        ["timedOut"] = timedOut,
                        ["stderrPreview"] = DeveloperDiagnostics.SanitizePreview(standardError, DiagnosticPreviewLength)
                    });
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new GitCommandResult(false, null, string.Empty, "Git operation canceled.", stopwatch.Elapsed, true, false, "Cancelled");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            stopwatch.Stop();
            if (logOperation)
            {
                DeveloperDiagnostics.LogException("Git", ex, "Git process execution failed.", new Dictionary<string, object?>
                {
                    ["workingDirectory"] = workingDirectory,
                    ["argumentCount"] = arguments.Count,
                    ["durationMilliseconds"] = stopwatch.ElapsedMilliseconds
                });
            }

            return GitCommandResult.Failed("LaunchFailed", "Git could not be launched.", stopwatch.Elapsed);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static IReadOnlyList<string> GetExecutableCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    Add(Path.Combine(directory, "git.exe"));
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "git.exe"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"));
        Add("git.exe");
        return candidates;
    }

    private static string? ParseVersion(string output)
    {
        var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        const string prefix = "git version ";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].Trim()
            : line.Trim();
    }

    private static void TryKill(Process process)
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
}
