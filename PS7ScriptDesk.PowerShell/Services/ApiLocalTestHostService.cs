using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed class ApiLocalTestHostService : IApiLocalTestHostService
{
    private const int DefaultReadinessTimeoutSeconds = 30;
    private const int MaximumRetainedLogLines = 200;
    private const int MaximumRetainedLogCharacters = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IApiProjectGenerator _projectGenerator;
    private readonly HttpClient _httpClient;
    private readonly ApiLocalTestLogBuffer _logs = new(MaximumRetainedLogLines, MaximumRetainedLogCharacters);
    private Process? _process;
    private bool _stopRequested;
    private bool _disposed;
    private ApiLocalTestHostStatus _status = new();

    public ApiLocalTestHostService()
        : this(new ApiProjectGenerator(), new HttpClient())
    {
    }

    internal ApiLocalTestHostService(IApiProjectGenerator projectGenerator, HttpClient httpClient)
    {
        _projectGenerator = projectGenerator ?? throw new ArgumentNullException(nameof(projectGenerator));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public event EventHandler<ApiLocalTestHostStatus>? StatusChanged;

    public ApiLocalTestHostStatus CurrentStatus => _status;

    public async Task<ApiLocalTestHostStartResult> StartAsync(
        ApiLocalTestHostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                var duplicateStatus = Snapshot(
                    ApiLocalTestHostState.Running,
                    "A local API test host is already running.",
                    _status.BaseUrl,
                    _status.OpenApiUrl,
                    _status.SwaggerUrl,
                    _status.ProjectDirectory,
                    _process.Id);
                return ApiLocalTestHostStartResult.Failure(
                    "A local API test host is already running.",
                    "Stop or restart the existing local API test host before starting another one.",
                    duplicateStatus);
            }

            CleanupExitedProcessUnderGate();
            _logs.Clear();
            var operationId = $"ApiLocalTest-{Guid.NewGuid():N}";
            var stopwatch = Stopwatch.StartNew();
            using var scope = DeveloperDiagnostics.BeginScope(operationId: operationId);
            DeveloperDiagnostics.LogOperationStart(
                "ApiLocalTestHost",
                "Start",
                "Starting local API test host.",
                operationId,
                new Dictionary<string, object?>
                {
                    ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath),
                    ["requestedPort"] = request.Port
                });

            try
            {
                SetStatus(ApiLocalTestHostState.Generating, "Generating local API project.");
                var projectDirectory = ResolveProjectDirectory(request);
                var projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? "LocalPowerShellApi" : request.ProjectName!;
                var generation = await _projectGenerator.GenerateAsync(
                    new ApiProjectGenerationRequest(
                        request.SourceScriptPath,
                        request.Configuration,
                        projectDirectory,
                        projectName,
                        request.OverwriteExistingGeneratedProject),
                    cancellationToken).ConfigureAwait(false);

                if (!generation.Succeeded)
                {
                    var failed = Snapshot(ApiLocalTestHostState.Failed, generation.SummaryMessage, projectDirectory: projectDirectory);
                    DeveloperDiagnostics.LogWarning(
                        "ApiLocalTestHost",
                        "Local API project generation failed.",
                        new Dictionary<string, object?>
                        {
                            ["operationId"] = operationId,
                            ["errorCount"] = generation.ValidationErrors.Count
                        });
                    SetStatus(failed);
                    return ApiLocalTestHostStartResult.Failure(generation.SummaryMessage, generation.DetailedLog, failed);
                }

                SetStatus(ApiLocalTestHostState.Preparing, "Preparing local API host process.", projectDirectory: generation.DestinationDirectory);
                var hostExecutablePath = ResolveHostExecutablePath(request.HostExecutablePath);
                var port = request.Port ?? GetFreeLoopbackPort();
                var baseUrl = new Uri($"http://127.0.0.1:{port}");
                var openApiUrl = new Uri(baseUrl, "/openapi/v1.json");
                var swaggerUrl = ShouldExposeSwaggerUi(request.Configuration)
                    ? new Uri(baseUrl, "/swagger")
                    : null;

                SetStatus(
                    ApiLocalTestHostState.Starting,
                    "Starting local API host process.",
                    baseUrl,
                    openApiUrl,
                    swaggerUrl,
                    generation.DestinationDirectory);

                var process = CreateProcess(hostExecutablePath, generation.DestinationDirectory, port);
                _stopRequested = false;
                _process = process;
                AttachProcessHandlers(process, operationId);
                if (!process.Start())
                {
                    throw new InvalidOperationException("The local API host process could not be started.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                SetStatus(
                    ApiLocalTestHostState.Starting,
                    "Waiting for local API readiness.",
                    baseUrl,
                    openApiUrl,
                    swaggerUrl,
                    generation.DestinationDirectory,
                    process.Id);

                var ready = await WaitForReadinessAsync(
                    process,
                    new Uri(baseUrl, "/healthz"),
                    request.ReadinessTimeout ?? TimeSpan.FromSeconds(DefaultReadinessTimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
                if (!ready)
                {
                    var failed = Snapshot(
                        ApiLocalTestHostState.Failed,
                        "The local API did not become ready before the timeout.",
                        baseUrl,
                        openApiUrl,
                        swaggerUrl,
                        generation.DestinationDirectory,
                        process.HasExited ? null : process.Id,
                        process.HasExited ? process.ExitCode : null);
                    SetStatus(failed);
                    await StopProcessUnderGateAsync(process, CancellationToken.None).ConfigureAwait(false);
                    return ApiLocalTestHostStartResult.Failure(
                        "The local API did not become ready before the timeout.",
                        string.Join(Environment.NewLine, _logs.Snapshot()),
                        failed);
                }

                var running = Snapshot(
                    ApiLocalTestHostState.Running,
                    "Local API test host is running.",
                    baseUrl,
                    openApiUrl,
                    swaggerUrl,
                    generation.DestinationDirectory,
                    process.Id);
                SetStatus(running);
                stopwatch.Stop();
                DeveloperDiagnostics.LogOperationStop(
                    "ApiLocalTestHost",
                    "Start",
                    "Local API test host started.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["port"] = port,
                        ["processId"] = process.Id
                    });

                return ApiLocalTestHostStartResult.Success(running, $"Local API is ready at {baseUrl}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DeveloperDiagnostics.LogWarning(
                    "ApiLocalTestHost",
                    "Local API test host startup was canceled.",
                    new Dictionary<string, object?> { ["operationId"] = operationId });
                await StopProcessUnderGateAsync(_process, CancellationToken.None).ConfigureAwait(false);
                var canceled = Snapshot(ApiLocalTestHostState.NotRunning, "Local API test host startup was canceled.");
                SetStatus(canceled);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DeveloperDiagnostics.LogException(
                    "ApiLocalTestHost",
                    exception,
                    "Local API test host startup failed.",
                    new Dictionary<string, object?> { ["operationId"] = operationId });
                var failed = Snapshot(ApiLocalTestHostState.Failed, exception.Message);
                SetStatus(failed);
                await StopProcessUnderGateAsync(_process, CancellationToken.None).ConfigureAwait(false);
                return ApiLocalTestHostStartResult.Failure(
                    "Local API test host startup failed.",
                    exception.Message,
                    failed);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApiLocalTestHostStartResult> RestartAsync(
        ApiLocalTestHostRequest request,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        return await StartAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiLocalTestHostStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(ApiLocalTestHostState.Stopping, "Stopping local API test host.");
            await StopProcessUnderGateAsync(_process, cancellationToken).ConfigureAwait(false);
            var stopped = Snapshot(ApiLocalTestHostState.NotRunning, "Local API test host is not running.");
            SetStatus(stopped);
            DeveloperDiagnostics.LogInfo("ApiLocalTestHost", "Local API test host stopped.");
            return stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopProcessUnderGateAsync(_process, CancellationToken.None).ConfigureAwait(false);
            _httpClient.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> WaitForReadinessAsync(
        Process process,
        Uri readinessUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var boundedTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(DefaultReadinessTimeoutSeconds) : timeout;
        var deadline = DateTimeOffset.UtcNow + boundedTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                return false;
            }

            try
            {
                using var response = await _httpClient.GetAsync(readinessUri, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static Process CreateProcess(string executablePath, string projectDirectory, int port)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? projectDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("--content-root");
        process.StartInfo.ArgumentList.Add(projectDirectory);
        process.StartInfo.ArgumentList.Add("--config");
        process.StartInfo.ArgumentList.Add(Path.Combine("Config", "api.ps7api.json"));
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return process;
    }

    private void AttachProcessHandlers(Process process, string operationId)
    {
        process.OutputDataReceived += (_, eventArgs) => AddLog("stdout", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AddLog("stderr", eventArgs.Data);
        process.Exited += (_, _) =>
        {
            if (_stopRequested)
            {
                return;
            }

            var exitCode = SafeExitCode(process);
            DeveloperDiagnostics.LogWarning(
                "ApiLocalTestHost",
                "Local API test host exited unexpectedly.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["exitCode"] = exitCode
                });
            SetStatus(Snapshot(
                ApiLocalTestHostState.Exited,
                "The local API test host exited unexpectedly.",
                _status.BaseUrl,
                _status.OpenApiUrl,
                _status.SwaggerUrl,
                _status.ProjectDirectory,
                null,
                exitCode));
        };
    }

    private async Task StopProcessUnderGateAsync(Process? process, CancellationToken cancellationToken)
    {
        _stopRequested = true;
        if (process is null)
        {
            _process = null;
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DeveloperDiagnostics.LogException(
                "ApiLocalTestHost",
                exception,
                "Local API test host process cleanup failed.");
        }
        finally
        {
            process.Dispose();
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }
    }

    private void CleanupExitedProcessUnderGate()
    {
        if (_process is null || !_process.HasExited)
        {
            return;
        }

        _process.Dispose();
        _process = null;
    }

    private static string ResolveProjectDirectory(ApiLocalTestHostRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProjectDirectory))
        {
            return Path.GetFullPath(request.ProjectDirectory);
        }

        return Path.Combine(
            ApplicationBranding.LocalApplicationDataRoot,
            "Temp",
            "ApiLocalTest",
            Guid.NewGuid().ToString("N"),
            "project");
    }

    private static string ResolveHostExecutablePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(fullPath) && string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            throw new FileNotFoundException("The local API host executable was not found.", Path.GetFileName(explicitPath));
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PS7ScriptDesk.RestApiProofHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "RestApi", "PS7ScriptDesk.RestApiProofHost.exe")
        };

        foreach (var candidate in candidates)
        {
            if (IsRunnableHostExecutable(candidate))
            {
                return candidate;
            }
        }

        var developmentCandidate = ResolveDevelopmentHostExecutablePath();
        if (!string.IsNullOrWhiteSpace(developmentCandidate))
        {
            return developmentCandidate;
        }

        throw new FileNotFoundException("The local API host executable was not found.");
    }

    private static string? ResolveDevelopmentHostExecutablePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            foreach (var configurationName in new[] { "Debug", "Release" })
            {
                foreach (var targetFramework in new[] { "net10.0", Path.Combine("net10.0", "win-x64") })
                {
                    var candidate = Path.Combine(
                        current.FullName,
                        "PS7ScriptDesk.RestApiProofHost",
                        "bin",
                        configurationName,
                        targetFramework,
                        "PS7ScriptDesk.RestApiProofHost.exe");
                    if (IsRunnableHostExecutable(candidate))
                    {
                        return candidate;
                    }
                }
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsRunnableHostExecutable(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        return File.Exists(executablePath) &&
               directory is not null &&
               File.Exists(Path.Combine(directory, "PS7ScriptDesk.RestApiProofHost.deps.json")) &&
               File.Exists(Path.Combine(directory, "System.DirectoryServices.dll"));
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool ShouldExposeSwaggerUi(ApiPublishConfiguration configuration)
        => configuration.OpenApi.IsEnabled &&
           (configuration.Security.Mode == ApiSecurityMode.LocalTestNoAuthentication &&
            configuration.Security.AllowNoAuthenticationForLocalTest
               ? configuration.OpenApi.EnableSwaggerUiForLocalTest
               : configuration.OpenApi.EnableSwaggerUiForPublishedApi);

    private void AddLog(string streamName, string? line)
    {
        if (line is null)
        {
            return;
        }

        _logs.Add($"{streamName}: {line}");
        SetStatus(Snapshot(
            _status.State,
            _status.StatusMessage,
            _status.BaseUrl,
            _status.OpenApiUrl,
            _status.SwaggerUrl,
            _status.ProjectDirectory,
            _status.ProcessId,
            _status.ExitCode));
    }

    private void SetStatus(
        ApiLocalTestHostState state,
        string message,
        Uri? baseUrl = null,
        Uri? openApiUrl = null,
        Uri? swaggerUrl = null,
        string? projectDirectory = null,
        int? processId = null,
        int? exitCode = null)
        => SetStatus(Snapshot(state, message, baseUrl, openApiUrl, swaggerUrl, projectDirectory, processId, exitCode));

    private void SetStatus(ApiLocalTestHostStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private ApiLocalTestHostStatus Snapshot(
        ApiLocalTestHostState state,
        string message,
        Uri? baseUrl = null,
        Uri? openApiUrl = null,
        Uri? swaggerUrl = null,
        string? projectDirectory = null,
        int? processId = null,
        int? exitCode = null)
        => new()
        {
            State = state,
            StatusMessage = string.IsNullOrWhiteSpace(message) ? state.ToString() : message,
            BaseUrl = baseUrl,
            OpenApiUrl = openApiUrl,
            SwaggerUrl = swaggerUrl,
            ProjectDirectory = projectDirectory ?? string.Empty,
            ProcessId = processId,
            ExitCode = exitCode,
            Logs = _logs.Snapshot()
        };

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

}

internal sealed class ApiLocalTestLogBuffer
{
    private const int MaximumLineLength = 2048;
    private readonly int _maximumLines;
    private readonly int _maximumCharacters;
    private readonly Queue<string> _lines = new();
    private readonly object _sync = new();
    private int _characters;

    public ApiLocalTestLogBuffer(int maximumLines, int maximumCharacters)
    {
        _maximumLines = Math.Max(1, maximumLines);
        _maximumCharacters = Math.Max(1, maximumCharacters);
    }

    public void Add(string line)
    {
        var value = line.Length <= MaximumLineLength ? line : line[..MaximumLineLength];
        lock (_sync)
        {
            _lines.Enqueue(value);
            _characters += value.Length;
            while (_lines.Count > _maximumLines || _characters > _maximumCharacters)
            {
                _characters -= _lines.Dequeue().Length;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
            _characters = 0;
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
        {
            return _lines.ToArray();
        }
    }
}
