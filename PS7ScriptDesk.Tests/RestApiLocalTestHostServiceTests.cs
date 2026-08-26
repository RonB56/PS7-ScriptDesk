using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiLocalTestHostServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk Api Local Host {Guid.NewGuid():N}");

    public RestApiLocalTestHostServiceTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task StartAsync_StartsExternalHostAndServesGeneratedApi()
    {
        await using var service = new ApiLocalTestHostService();
        var states = new List<ApiLocalTestHostState>();
        service.StatusChanged += (_, status) => states.Add(status.State);
        var request = CreateRequest("StartAndServe");

        var result = await service.StartAsync(request);

        Assert.True(result.Succeeded, result.DetailedLog);
        Assert.Equal(ApiLocalTestHostState.Running, result.Status.State);
        Assert.True(result.Status.IsRunning);
        Assert.NotNull(result.Status.ProcessId);
        Assert.NotEqual(Environment.ProcessId, result.Status.ProcessId.Value);
        Assert.Equal(IPAddress.Loopback.ToString(), result.Status.BaseUrl?.Host);
        Assert.Equal(new Uri(result.Status.BaseUrl!, "/openapi/v1.json"), result.Status.OpenApiUrl);
        Assert.Equal(new Uri(result.Status.BaseUrl!, "/swagger"), result.Status.SwaggerUrl);
        Assert.Contains(ApiLocalTestHostState.Generating, states);
        Assert.Contains(ApiLocalTestHostState.Preparing, states);
        Assert.Contains(ApiLocalTestHostState.Starting, states);
        Assert.Contains(ApiLocalTestHostState.Running, states);

        using var client = new HttpClient { BaseAddress = result.Status.BaseUrl };
        using var response = await client.GetAsync("/api/phase8/items/ALPHA?mode=detail");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ALPHA", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("detail", json.RootElement.GetProperty("mode").GetString());

        var stopped = await service.StopAsync();

        Assert.Equal(ApiLocalTestHostState.NotRunning, stopped.State);
        await WaitForProcessExitAsync(result.Status.ProcessId.Value);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunningRejectsDuplicateWithoutReplacingProcess()
    {
        await using var service = new ApiLocalTestHostService();
        var request = CreateRequest("DuplicateStart");
        var first = await service.StartAsync(request);
        Assert.True(first.Succeeded, first.DetailedLog);

        var duplicate = await service.StartAsync(request);

        Assert.False(duplicate.Succeeded);
        Assert.Equal(ApiLocalTestHostState.Running, duplicate.Status.State);
        Assert.Equal(first.Status.ProcessId, duplicate.Status.ProcessId);
        await service.StopAsync();
    }

    [Fact]
    public async Task RestartAsync_StopsOldProcessAndStartsReplacement()
    {
        await using var service = new ApiLocalTestHostService();
        var request = CreateRequest("Restart");
        var first = await service.StartAsync(request);
        Assert.True(first.Succeeded, first.DetailedLog);
        var firstProcessId = first.Status.ProcessId!.Value;

        var restarted = await service.RestartAsync(request);

        Assert.True(restarted.Succeeded, restarted.DetailedLog);
        Assert.Equal(ApiLocalTestHostState.Running, restarted.Status.State);
        Assert.NotEqual(firstProcessId, restarted.Status.ProcessId);
        await WaitForProcessExitAsync(firstProcessId);
        await service.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_StopsRunningChildProcess()
    {
        var service = new ApiLocalTestHostService();
        var started = await service.StartAsync(CreateRequest("DisposeStops"));
        Assert.True(started.Succeeded, started.DetailedLog);
        var processId = started.Status.ProcessId!.Value;

        await service.DisposeAsync();

        await WaitForProcessExitAsync(processId);
    }

    [Fact]
    public async Task ChildExit_UpdatesStatusWithoutKillingUnrelatedProcesses()
    {
        await using var service = new ApiLocalTestHostService();
        var started = await service.StartAsync(CreateRequest("ChildExit"));
        Assert.True(started.Succeeded, started.DetailedLog);
        var processId = started.Status.ProcessId!.Value;

        using (var process = Process.GetProcessById(processId))
        {
            process.Kill(entireProcessTree: true);
        }

        await WaitUntilAsync(() => service.CurrentStatus.State == ApiLocalTestHostState.Exited);

        Assert.Equal(ApiLocalTestHostState.Exited, service.CurrentStatus.State);
        Assert.Null(service.CurrentStatus.ProcessId);
        await service.StopAsync();
    }

    [Fact]
    public async Task StartAsync_MissingHostExecutableFailsWithoutProcess()
    {
        await using var service = new ApiLocalTestHostService();
        var missingPath = Path.Combine(_testDirectory, "missing-host.exe");

        var result = await service.StartAsync(CreateRequest("MissingHost", hostExecutablePath: missingPath));

        Assert.False(result.Succeeded);
        Assert.Equal(ApiLocalTestHostState.Failed, result.Status.State);
        Assert.Null(result.Status.ProcessId);
    }

    [Fact]
    public async Task StartAsync_WhenPortIsUnavailableFailsAndDoesNotLeaveProcessRunning()
    {
        await using var service = new ApiLocalTestHostService();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await service.StartAsync(CreateRequest("OccupiedPort", port: port, readinessTimeout: TimeSpan.FromSeconds(5)));

        Assert.False(result.Succeeded);
        Assert.Equal(ApiLocalTestHostState.Failed, result.Status.State);
        Assert.False(service.CurrentStatus.IsRunning);
        if (result.Status.ProcessId is { } processId)
        {
            await WaitForProcessExitAsync(processId);
        }
    }

    [Fact]
    public async Task ProofHostExecutable_WhenPortIsUnavailableExitsWithControlledStartupFailure()
    {
        using var listener = new TcpListener(IPAddress.Parse("127.0.0.1"), 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveProofHostExecutable(),
                WorkingDirectory = ResolveProofHostContentRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--content-root");
        process.StartInfo.ArgumentList.Add(ResolveProofHostContentRoot());
        process.StartInfo.ArgumentList.Add("--config");
        process.StartInfo.ArgumentList.Add(Path.Combine("Config", "TestApi.ps7api.json"));
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(60)));
        if (completed != exitTask)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                "The proof host did not exit after an occupied-port startup failure." +
                Environment.NewLine +
                await standardOutput +
                Environment.NewLine +
                await standardError);
        }

        var outputText = await standardOutput;
        var errorText = await standardError;

        Assert.Equal(1, process.ExitCode);
        Assert.DoesNotContain("Unhandled exception", errorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startup failed", errorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("address already in use", errorText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("listening on", outputText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApiLocalTestLogBuffer_BoundsLinesCharactersAndLineLength()
    {
        var lineBounded = new ApiLocalTestLogBuffer(3, 4096);
        lineBounded.Add("first");
        lineBounded.Add("second");
        lineBounded.Add("third");
        lineBounded.Add("fourth");

        Assert.Equal(["second", "third", "fourth"], lineBounded.Snapshot());

        lineBounded.Add(new string('x', 3000));
        Assert.Equal(2048, lineBounded.Snapshot().Last().Length);

        var characterBounded = new ApiLocalTestLogBuffer(10, 12);
        characterBounded.Add("11111");
        characterBounded.Add("22222");
        characterBounded.Add("33333");

        Assert.Equal(["22222", "33333"], characterBounded.Snapshot());
    }

    private ApiLocalTestHostRequest CreateRequest(
        string scenarioName,
        int? port = null,
        TimeSpan? readinessTimeout = null,
        string? hostExecutablePath = null)
    {
        var scriptPath = WritePhase8Script($"{scenarioName}.ps1");
        return new ApiLocalTestHostRequest(
            scriptPath,
            CreatePhase8Configuration(scriptPath),
            Path.Combine(_testDirectory, scenarioName, "Generated"),
            "Phase8LocalApi",
            port,
            readinessTimeout,
            overwriteExistingGeneratedProject: true,
            hostExecutablePath ?? ResolveProofHostExecutable());
    }

    private string WritePhase8Script(string fileName)
    {
        var path = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(path, Phase8ScriptSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static ApiPublishConfiguration CreatePhase8Configuration(string sourceScriptPath)
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(sourceScriptPath);
        configuration.Api.Title = "Phase 8 Local Test API";
        configuration.OpenApi.Title = "Phase 8 Local Test API";
        configuration.OpenApi.EnableSwaggerUiForLocalTest = true;
        configuration.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        configuration.Security.AllowNoAuthenticationForLocalTest = true;
        configuration.Runtime.RunspacePoolMinimum = 1;
        configuration.Runtime.RunspacePoolMaximum = 2;
        configuration.Runtime.MaximumConcurrentExecutions = 2;
        configuration.Runtime.QueueLimit = 2;
        configuration.Runtime.DefaultInvocationTimeout = TimeSpan.FromSeconds(10);
        configuration.Runtime.ResponseItemLimit = 100;
        configuration.Runtime.ResponseByteLimit = 1024 * 1024;
        configuration.Endpoints =
        [
            new ApiEndpointConfiguration
            {
                EndpointId = "phase8-get-item",
                PowerShellFunctionName = "Get-Phase8Item",
                DisplayName = "Get phase 8 item",
                Rest =
                {
                    Method = ApiHttpMethod.Get,
                    RouteTemplate = "/api/phase8/items/{name}",
                    OperationId = "phase8GetItem",
                    Tags = ["Phase 8"],
                    IncludeInOpenApi = true
                },
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "Name",
                        Source = ApiParameterSource.Route,
                        Name = "name",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "Mode",
                        Source = ApiParameterSource.Query,
                        Name = "mode",
                        Required = ApiRequiredBehavior.Optional,
                        TypeName = "string"
                    }
                ]
            }
        ];
        return configuration;
    }

    private static string ResolveProofHostExecutable()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost", "bin", "Debug", "net10.0", "PS7ScriptDesk.RestApiProofHost.exe"),
                Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost", "bin", "Debug", "net10.0", "win-x64", "PS7ScriptDesk.RestApiProofHost.exe")
            })
            {
                if (IsRunnableHostCandidate(candidate))
                {
                    return candidate;
                }
            }

            current = current.Parent;
        }

        var baseCandidate = Path.Combine(AppContext.BaseDirectory, "PS7ScriptDesk.RestApiProofHost.exe");
        if (IsRunnableHostCandidate(baseCandidate))
        {
            return baseCandidate;
        }

        throw new FileNotFoundException("Could not locate the REST API proof host executable.");
    }

    private static bool IsRunnableHostCandidate(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        return File.Exists(executablePath) &&
               directory is not null &&
               File.Exists(Path.Combine(directory, "PS7ScriptDesk.RestApiProofHost.deps.json")) &&
               File.Exists(Path.Combine(directory, "System.DirectoryServices.dll"));
    }

    private static string ResolveProofHostContentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost");
            if (File.Exists(Path.Combine(candidate, "Config", "TestApi.ps7api.json")) &&
                File.Exists(Path.Combine(candidate, "Scripts", "TestApi.ps1")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the REST API proof host content root.");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Process {processId} did not exit.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The expected local API test host status was not observed.");
    }

    private const string Phase8ScriptSource = """
function Get-Phase8Item {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [string]$Mode = 'summary'
    )

    [pscustomobject]@{
        name = $Name
        mode = $Mode
    }
}
""";
}
