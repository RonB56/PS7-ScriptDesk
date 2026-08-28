using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class LiveConsoleRealSessionHardeningTests
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex OscRegex = new(@"\x1B\].*?(\x07|\x1B\\)", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public async Task RealSession_PersistsExpectedStateAndPreservesFullRunScopeIsolation()
    {
        var runtime = TryFindPwshRuntime();
        if (runtime is null)
        {
            return;
        }

        await using var harness = await RealConsoleHarness.StartAsync(runtime);
        var marker = "PSDT_" + Guid.NewGuid().ToString("N");
        var originalDirectory = harness.Service.CurrentWorkingDirectory;
        var locationDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), marker + "_Location")).FullName;

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_global.ps1",
            "$Global:ScriptDeskTest = 12345",
            executeInCurrentScope: false);
        var globalOutput = await harness.ExecuteCommandAndCaptureAsync($"Write-Output ('{marker}_GLOBAL=' + $Global:ScriptDeskTest)");
        Assert.Contains($"{marker}_GLOBAL=12345", globalOutput, StringComparison.Ordinal);

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_local.ps1",
            "$LocalScriptValue = 42",
            executeInCurrentScope: false);
        var localOutput = await harness.ExecuteCommandAndCaptureAsync(
            $"if (Get-Variable -Name LocalScriptValue -Scope Global -ErrorAction SilentlyContinue) {{ '{marker}_LOCAL_LEAK=1' }} else {{ '{marker}_LOCAL_LEAK=0' }}");
        Assert.Contains($"{marker}_LOCAL_LEAK=0", localOutput, StringComparison.Ordinal);

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_selection.ps1",
            "$SelectionScopeValue = 6789; function global:Invoke-ScriptDeskSelectionPersistence { 'selection-function-ok' }",
            executeInCurrentScope: true);
        var selectionOutput = await harness.ExecuteCommandAndCaptureAsync(
            $"Write-Output ('{marker}_SELECTION=' + $SelectionScopeValue); Write-Output ('{marker}_SELECTION_FN=' + (Invoke-ScriptDeskSelectionPersistence))");
        Assert.Contains($"{marker}_SELECTION=6789", selectionOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_SELECTION_FN=selection-function-ok", selectionOutput, StringComparison.Ordinal);

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_module.ps1",
            "Import-Module Microsoft.PowerShell.Archive",
            executeInCurrentScope: false);
        var moduleOutput = await harness.ExecuteCommandAndCaptureAsync(
            $"if (Get-Module Microsoft.PowerShell.Archive) {{ '{marker}_MODULE=loaded' }} else {{ '{marker}_MODULE=missing' }}");
        Assert.Contains($"{marker}_MODULE=loaded", moduleOutput, StringComparison.Ordinal);

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_environment.ps1",
            "$env:PS7SD_PERSISTENCE_TEST = 'environment-ok'",
            executeInCurrentScope: false);
        var environmentOutput = await harness.ExecuteCommandAndCaptureAsync($"Write-Output ('{marker}_ENV=' + $env:PS7SD_PERSISTENCE_TEST)");
        Assert.Contains($"{marker}_ENV=environment-ok", environmentOutput, StringComparison.Ordinal);

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_function.ps1",
            "function global:Invoke-ScriptDeskPersistentRunFunction { 'function-ok' }",
            executeInCurrentScope: false);
        var functionOutput = await harness.ExecuteCommandAndCaptureAsync($"Write-Output ('{marker}_FUNCTION=' + (Invoke-ScriptDeskPersistentRunFunction))");
        Assert.Contains($"{marker}_FUNCTION=function-ok", functionOutput, StringComparison.Ordinal);

        await harness.ExecuteScriptAndWaitAsync(
            marker + "_location.ps1",
            $"Set-Location {ToPowerShellLiteral(locationDirectory)}",
            executeInCurrentScope: false);
        var locationOutput = await harness.ExecuteCommandAndCaptureAsync($"Write-Output ('{marker}_LOCATION=' + (Get-Location).ProviderPath)");
        Assert.Contains($"{marker}_LOCATION={locationDirectory}", locationOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(locationDirectory, harness.Service.CurrentWorkingDirectory, ignoreCase: true);

        if (!string.IsNullOrWhiteSpace(originalDirectory) && Directory.Exists(originalDirectory))
        {
            await harness.ExecuteScriptAndWaitAsync(
                marker + "_restore_location.ps1",
                $"Set-Location {ToPowerShellLiteral(originalDirectory)}",
                executeInCurrentScope: false);
        }
    }

    [Fact]
    public async Task RealSession_RendersNativeAndPowerShellStreamsAndSurvivesSequentialRuns()
    {
        var runtime = TryFindPwshRuntime();
        if (runtime is null)
        {
            return;
        }

        await using var harness = await RealConsoleHarness.StartAsync(runtime);
        var marker = "PSDT_" + Guid.NewGuid().ToString("N");

        var streamOutput = await harness.ExecuteScriptAndCaptureAsync(
            marker + "_streams.ps1",
            string.Join(
                Environment.NewLine,
                $"Write-Output '{marker}_SUCCESS'",
                $"Write-Warning '{marker}_WARNING'",
                $"Write-Verbose '{marker}_VERBOSE' -Verbose",
                "$DebugPreference = 'Continue'",
                $"Write-Debug '{marker}_DEBUG'",
                $"Write-Information '{marker}_INFO' -InformationAction Continue",
                $"& {ToPowerShellLiteral(runtime.ExecutablePath)} -NoLogo -NoProfile -Command \"[Console]::Out.WriteLine('{marker}_NATIVE_STDOUT'); [Console]::Error.WriteLine('{marker}_NATIVE_STDERR')\""),
            executeInCurrentScope: false);

        Assert.Contains($"{marker}_SUCCESS", streamOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_WARNING", streamOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_VERBOSE", streamOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_DEBUG", streamOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_INFO", streamOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_NATIVE_STDOUT", streamOutput, StringComparison.Ordinal);
        Assert.Contains($"{marker}_NATIVE_STDERR", streamOutput, StringComparison.Ordinal);

        for (var index = 0; index < 5; index++)
        {
            var output = await harness.ExecuteScriptAndCaptureAsync(
                marker + "_sequential_" + index + ".ps1",
                $"Write-Output '{marker}_SEQUENTIAL_{index}'",
                executeInCurrentScope: false);
            Assert.Contains($"{marker}_SEQUENTIAL_{index}", output, StringComparison.Ordinal);
        }

        Assert.True(harness.Service.IsSessionRunning);
        Assert.False(harness.Service.IsCommandInProgress);
    }

    [Fact]
    public async Task RealSession_ReadHostAcceptsTerminalInputAndCompletes()
    {
        var runtime = TryFindPwshRuntime();
        if (runtime is null)
        {
            return;
        }

        await using var harness = await RealConsoleHarness.StartAsync(runtime);
        var marker = "PSDT_" + Guid.NewGuid().ToString("N");
        var completion = harness.PrepareForCompletion();

        await harness.Service.ExecuteScriptAsync(
            marker + "_readhost.ps1",
            "$value = Read-Host 'ScriptDeskReadHostPrompt'; Write-Output ('" + marker + "_READHOST=' + $value)",
            harness.OutputRecords.Add,
            executeInCurrentScope: false);

        await WaitUntilAsync(() => harness.Service.IsCommandInProgress, TimeSpan.FromSeconds(5));
        await harness.Service.WriteRawInputAsync("typed-value\r");
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var output = harness.DrainNormalizedOutput();
        Assert.Contains($"{marker}_READHOST=typed-value", output, StringComparison.Ordinal);
        Assert.False(harness.Service.IsCommandInProgress);

        var afterOutput = await harness.ExecuteCommandAndCaptureAsync($"Write-Output '{marker}_AFTER_READHOST=ok'");
        Assert.Contains($"{marker}_AFTER_READHOST=ok", afterOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealSession_InterruptExitRestartAndSnapshotCleanupRemainUsable()
    {
        var runtime = TryFindPwshRuntime();
        if (runtime is null)
        {
            return;
        }

        await using var harness = await RealConsoleHarness.StartAsync(runtime);
        var marker = "PSDT_" + Guid.NewGuid().ToString("N");
        var snapshotRoot = ResolveTerminalSnapshotRoot();
        var baselineSnapshots = Directory.Exists(snapshotRoot)
            ? Directory.EnumerateFiles(snapshotRoot, "*.ps1", SearchOption.TopDirectoryOnly)
                .Where(IsKnownTerminalSnapshot)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await harness.Service.ExecuteScriptAsync(
            marker + "_interrupt.ps1",
            "while ($true) { Start-Sleep -Milliseconds 100 }",
            harness.OutputRecords.Add,
            executeInCurrentScope: false);
        await WaitUntilAsync(() => harness.Service.IsCommandInProgress, TimeSpan.FromSeconds(5));

        var interruptResult = await harness.Service.InterruptOrRestartAsync(harness.OutputRecords.Add).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => !harness.Service.IsCommandInProgress, TimeSpan.FromSeconds(20));
        Assert.True(interruptResult.InterruptAttempted || interruptResult.SessionRestarted);
        Assert.False(harness.Service.IsCommandInProgress);
        Assert.True(harness.Service.IsSessionRunning);

        var afterInterrupt = await harness.ExecuteCommandAndCaptureAsync($"Write-Output '{marker}_AFTER_INTERRUPT=ok'");
        Assert.Contains($"{marker}_AFTER_INTERRUPT=ok", afterInterrupt, StringComparison.Ordinal);

        var sessionTerminated = harness.PrepareForSessionTerminated();
        await harness.Service.WriteRawInputAsync("exit\r");
        await sessionTerminated.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => !harness.Service.IsSessionRunning, TimeSpan.FromSeconds(5));
        Assert.False(harness.Service.IsCommandInProgress);

        await harness.StartReplacementSessionAsync(runtime);
        var afterExit = await harness.ExecuteCommandAndCaptureAsync($"Write-Output '{marker}_AFTER_EXIT_RESTART=ok'");
        Assert.Contains($"{marker}_AFTER_EXIT_RESTART=ok", afterExit, StringComparison.Ordinal);

        var leakedSnapshots = Directory.Exists(snapshotRoot)
            ? Directory.EnumerateFiles(snapshotRoot, "*.ps1", SearchOption.TopDirectoryOnly)
                .Where(IsKnownTerminalSnapshot)
                .Where(path => !baselineSnapshots.Contains(path))
            : Array.Empty<string>();
        Assert.Empty(leakedSnapshots);
    }

    [Fact]
    public void ArchitectureBoundaries_PreserveDebuggerApiAndLegacyExecutionIsolation()
    {
        var root = FindRepositoryRoot();
        var bootstrapper = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "Composition", "AppBootstrapper.cs"));
        var debugger = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "Debugger", "PsesDebugSession.cs"));
        var apiRunspace = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.RestApiProofHost", "PowerShell", "RunspacePoolManager.cs"));
        var apiInvoker = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.RestApiProofHost", "PowerShell", "PowerShellFunctionInvoker.cs"));

        Assert.Contains("new LiveConsoleService()", bootstrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("new ScriptExecutionService()", bootstrapper, StringComparison.Ordinal);
        Assert.Contains("new ProcessStartInfo", debugger, StringComparison.Ordinal);
        Assert.Contains("-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -", debugger, StringComparison.Ordinal);
        Assert.Contains("RunspaceFactory.CreateRunspacePool", apiRunspace, StringComparison.Ordinal);
        Assert.Contains("InitialSessionState.CreateDefault2", apiRunspace, StringComparison.Ordinal);
        Assert.Contains("PowerShell.Create()", apiInvoker, StringComparison.Ordinal);
        Assert.DoesNotContain("ILiveConsoleService", apiRunspace + apiInvoker, StringComparison.Ordinal);
    }

    private static bool IsKnownTerminalSnapshot(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("pss-", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("psd-", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("psi-", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("psh-", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTerminalSnapshotRoot()
    {
        var method = typeof(LiveConsoleService).GetMethod("GetSnapshotRootDirectory", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, new object[] { true }));
    }

    private static PowerShellRuntimeInfo? TryFindPwshRuntime()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH"),
            "pwsh",
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @".cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe")
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ResolveExecutable(candidate!);
            if (resolved is null)
            {
                continue;
            }

            var version = ProbePwshVersion(resolved);
            if (version is null || version.Major < 7)
            {
                continue;
            }

            return new PowerShellRuntimeInfo(
                $"PowerShell {version} Test Runtime",
                "Core",
                version.ToString(),
                version,
                Environment.Is64BitOperatingSystem ? "x64" : "unknown",
                resolved,
                "LiveConsoleRealSessionTest",
                isPowerShell7OrLater: true,
                isWindowsPowerShell: false,
                isPreferred: true,
                isValidated: true);
        }

        return null;
    }

    private static string? ResolveExecutable(string candidate)
    {
        if (Path.IsPathRooted(candidate) && File.Exists(candidate))
        {
            return candidate;
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "where.exe",
                    ArgumentList = { candidate },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static Version? ProbePwshVersion(string executablePath)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executablePath,
                    ArgumentList =
                    {
                        "-NoLogo",
                        "-NoProfile",
                        "-Command",
                        "$PSVersionTable.PSVersion.ToString()"
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return Version.TryParse(output, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ToPowerShellLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string NormalizeTerminalText(string text)
    {
        var normalized = OscRegex.Replace(text, string.Empty);
        normalized = AnsiRegex.Replace(normalized, string.Empty);
        return normalized.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }

    private sealed class RealConsoleHarness : IAsyncDisposable
    {
        private readonly object _syncRoot = new();
        private readonly StringBuilder _rawOutput = new();
        private TaskCompletionSource<bool>? _completion;
        private TaskCompletionSource<bool>? _sessionTerminated;

        private RealConsoleHarness()
        {
            Service = new LiveConsoleService(preferRedirectedTerminalSession: true);
            Service.RawOutputReceived += (_, text) =>
            {
                lock (_syncRoot)
                {
                    _rawOutput.Append(text);
                }
            };
            Service.CommandExecutionCompleted += () =>
            {
                TaskCompletionSource<bool>? completion;
                lock (_syncRoot)
                {
                    completion = _completion;
                    _completion = null;
                }

                completion?.TrySetResult(true);
            };
            Service.SessionTerminated += () =>
            {
                TaskCompletionSource<bool>? sessionTerminated;
                lock (_syncRoot)
                {
                    sessionTerminated = _sessionTerminated;
                    _sessionTerminated = null;
                }

                sessionTerminated?.TrySetResult(true);
            };
        }

        public LiveConsoleService Service { get; }

        public List<ExecutionOutputRecord> OutputRecords { get; } = new();

        public static async Task<RealConsoleHarness> StartAsync(PowerShellRuntimeInfo runtime)
        {
            var harness = new RealConsoleHarness();
            await harness.StartReplacementSessionAsync(runtime);
            var readyMarker = "PSDT_READY_" + Guid.NewGuid().ToString("N");
            var readyOutput = await harness.ExecuteCommandAndCaptureAsync($"Write-Output '{readyMarker}'");
            Assert.Contains(readyMarker, readyOutput, StringComparison.Ordinal);
            harness.DrainNormalizedOutput();
            return harness;
        }

        public async Task StartReplacementSessionAsync(PowerShellRuntimeInfo runtime)
        {
            await Service.StartSessionAsync(runtime, OutputRecords.Add, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await WaitUntilAsync(() => Service.IsSessionRunning, TimeSpan.FromSeconds(5));
            await Task.Delay(300);
        }

        public TaskCompletionSource<bool> PrepareForCompletion()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_syncRoot)
            {
                _completion = completion;
            }

            return completion;
        }

        public TaskCompletionSource<bool> PrepareForSessionTerminated()
        {
            var sessionTerminated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_syncRoot)
            {
                _sessionTerminated = sessionTerminated;
            }

            return sessionTerminated;
        }

        public async Task ExecuteScriptAndWaitAsync(string displayName, string script, bool executeInCurrentScope)
        {
            var completion = PrepareForCompletion();
            await Service.ExecuteScriptAsync(displayName, script, OutputRecords.Add, executeInCurrentScope);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public async Task<string> ExecuteScriptAndCaptureAsync(string displayName, string script, bool executeInCurrentScope)
        {
            DrainNormalizedOutput();
            await ExecuteScriptAndWaitAsync(displayName, script, executeInCurrentScope);
            return DrainNormalizedOutput();
        }

        public async Task<string> ExecuteCommandAndCaptureAsync(string command)
        {
            DrainNormalizedOutput();
            var completion = PrepareForCompletion();
            await Service.ExecuteConsoleCommandAsync(command, OutputRecords.Add);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return DrainNormalizedOutput();
        }

        public async Task WaitForOutputAsync(string expectedText)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_syncRoot)
                {
                    if (NormalizeTerminalText(_rawOutput.ToString()).Contains(expectedText, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                await Task.Delay(50);
            }

            Assert.Contains(expectedText, DrainNormalizedOutput(), StringComparison.Ordinal);
        }

        public string DrainNormalizedOutput()
        {
            lock (_syncRoot)
            {
                var text = _rawOutput.ToString();
                _rawOutput.Clear();
                return NormalizeTerminalText(text);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Service.Dispose();
            }
        }
    }
}
