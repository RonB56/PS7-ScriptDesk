using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.RestApiProofHost.Hosting;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiRunspacePolicyTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk Api Runspace Policy {Guid.NewGuid():N}");

    public RestApiRunspacePolicyTests()
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
    public async Task TrustedFunction_UsingGetDateInCustomObject_Succeeds()
    {
        await using var host = await StartPolicyHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/computer/status?computerName=TEST-PC");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal("TEST-PC", json.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal("Online", json.RootElement.GetProperty("Status").GetString());
        Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("Time").GetString(), out _));
    }

    [Fact]
    public async Task TrustedFunction_UsingModuleQualifiedGetDate_Succeeds()
    {
        await using var host = await StartPolicyHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/computer/qualified?computerName=TEST-PC");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal("TEST-PC", json.RootElement.GetProperty("ComputerName").GetString());
        Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("Time").GetString(), out _));
    }

    [Fact]
    public async Task TrustedFunction_ReturningBareDateTime_NormalizesAsJsonDateTime()
    {
        await using var host = await StartPolicyHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/date");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal(JsonValueKind.String, json.RootElement.ValueKind);
        Assert.True(DateTimeOffset.TryParse(json.RootElement.GetString(), out _));
    }

    [Fact]
    public async Task TrustedFunction_UsingAdditionalInboxUtilityCmdlets_Succeeds()
    {
        await using var host = await StartPolicyHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/utility/summary");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal(6, json.RootElement.GetProperty("Sum").GetInt32());
        Assert.Equal("{\"value\":\"alpha\"}", json.RootElement.GetProperty("Json").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("Random").GetInt32());
    }

    [Fact]
    public async Task RemoteCaller_CannotInvokeBuiltInOrUnconfiguredFunctionsDirectly()
    {
        await using var host = await StartPolicyHostAsync();
        using var client = host.CreateClient();

        using var noGetDateRoute = await client.GetAsync("/api/get-date");
        using var noSecretRoute = await client.GetAsync("/api/unconfigured-secret");
        using var selectionAttempt = await client.GetAsync("/api/computer/status?computerName=TEST-PC&function=Get-UnconfiguredSecret");

        Assert.Equal(HttpStatusCode.NotFound, noGetDateRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noSecretRoute.StatusCode);
        using var json = await ReadSuccessJsonAsync(selectionAttempt);
        Assert.Equal("TEST-PC", json.RootElement.GetProperty("ComputerName").GetString());
    }

    [Fact]
    public async Task Coordinator_RejectsDirectBuiltInOrUnconfiguredFunctionSelection()
    {
        await using var host = await StartPolicyHostAsync();
        var coordinator = await CreateCoordinatorAsync();

        var getDate = await coordinator.InvokeAsync(new ApiInvocationRequest { FunctionName = "Get-Date" }, CancellationToken.None);
        var secret = await coordinator.InvokeAsync(new ApiInvocationRequest { FunctionName = "Get-UnconfiguredSecret" }, CancellationToken.None);

        Assert.Equal(ApiInvocationStatus.InvalidFunction, getDate.Status);
        Assert.Equal(ApiInvocationStatus.InvalidFunction, secret.Status);
    }

    [Fact]
    public async Task GenuineNonTerminatingError_StillReturnsSanitizedProblem()
    {
        await using var host = await StartPolicyHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/error/nonterminating");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("https://ps7scriptdesk.local/errors/powershell-non-terminating-error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("PowerShell invocation failed.", json.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("policy-secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Sensitive", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partial-output", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneratedRuntime_UsesSameBuiltInRunspacePolicy()
    {
        var contentRoot = CreatePolicyContentRoot("Generated");
        var sourcePath = Path.Combine(contentRoot, "Scripts", "PolicyApi.ps1");
        var destination = Path.Combine(_testDirectory, "GeneratedProject");
        var generation = await new ApiProjectGenerator().GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, CreatePolicyConfiguration(), destination, "PolicyGeneratedApi"));
        Assert.True(generation.Succeeded, generation.DetailedLog);

        var build = await RunProcessAsync("dotnet", ["build", generation.ProjectFilePath], destination, TimeSpan.FromMinutes(2));
        Assert.True(build.ExitCode == 0, build.StandardOutput + Environment.NewLine + build.StandardError);

        var port = GetFreeTcpPort();
        using var process = StartGeneratedApi(destination, generation.ProjectFilePath, port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitForHealthAsync(client, process);

            using var response = await client.GetAsync("/api/computer/status?computerName=GENERATED-PC");
            using var json = await ReadSuccessJsonAsync(response);

            Assert.Equal("GENERATED-PC", json.RootElement.GetProperty("ComputerName").GetString());
            Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("Time").GetString(), out _));
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    private async Task<RunningRestApiProofHost> StartPolicyHostAsync()
    {
        var contentRoot = CreatePolicyContentRoot($"Host-{Guid.NewGuid():N}");
        return await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions
        {
            Url = "http://127.0.0.1:0",
            ContentRootPath = contentRoot,
            ConfigurationRelativePath = Path.Combine("Config", "api.ps7api.json")
        });
    }

    private async Task<PowerShellInvocationCoordinator> CreateCoordinatorAsync()
    {
        var contentRoot = CreatePolicyContentRoot($"Coordinator-{Guid.NewGuid():N}");
        var poolManager = new RunspacePoolManager();
        var invoker = new PowerShellFunctionInvoker();
        var coordinator = new PowerShellInvocationCoordinator(poolManager, invoker);
        await coordinator.InitializeAsync(
            Path.Combine(contentRoot, "Scripts", "PolicyApi.ps1"),
            CreatePolicyConfiguration().Endpoints.Select(endpoint => endpoint.PowerShellFunctionName),
            ApiRuntimeOptions.CreateDefault(),
            CancellationToken.None);
        return coordinator;
    }

    private string CreatePolicyContentRoot(string name)
    {
        var root = Path.Combine(_testDirectory, name);
        var scriptDirectory = Path.Combine(root, "Scripts");
        var configDirectory = Path.Combine(root, "Config");
        Directory.CreateDirectory(scriptDirectory);
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(scriptDirectory, "PolicyApi.ps1"), PolicyScriptSource, new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(configDirectory, "api.ps7api.json"),
            JsonSerializer.Serialize(CreatePolicyConfiguration(), RestApiProofHostFactory.JsonOptions),
            new UTF8Encoding(false));
        return root;
    }

    private static ApiPublishConfiguration CreatePolicyConfiguration()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath("PolicyApi.ps1");
        configuration.Api.Title = "Runspace Policy API";
        configuration.OpenApi.Title = "Runspace Policy API";
        configuration.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        configuration.Security.AllowNoAuthenticationForLocalTest = true;
        configuration.Runtime.RunspacePoolMinimum = 1;
        configuration.Runtime.RunspacePoolMaximum = 2;
        configuration.Runtime.MaximumConcurrentExecutions = 2;
        configuration.Runtime.QueueLimit = 4;
        configuration.Runtime.DefaultInvocationTimeout = TimeSpan.FromSeconds(10);
        configuration.Endpoints =
        [
            CreateGetEndpoint("policy-computer-status", "Get-ComputerStatus", "/api/computer/status", "computerName", "ComputerName"),
            CreateGetEndpoint("policy-computer-qualified", "Get-QualifiedComputerStatus", "/api/computer/qualified", "computerName", "ComputerName"),
            CreateGetEndpoint("policy-bare-date", "Get-CurrentDate", "/api/date"),
            CreateGetEndpoint("policy-utility-summary", "Get-UtilitySummary", "/api/utility/summary"),
            CreateGetEndpoint("policy-nonterminating", "Invoke-PolicyNonTerminatingError", "/api/error/nonterminating")
        ];
        return configuration;
    }

    private static ApiEndpointConfiguration CreateGetEndpoint(
        string endpointId,
        string functionName,
        string route,
        string? httpParameterName = null,
        string? powerShellParameterName = null)
    {
        var endpoint = ApiEndpointConfiguration.CreateRest(functionName, ApiHttpMethod.Get, route);
        endpoint.EndpointId = endpointId;
        endpoint.RequiresAuthentication = false;
        endpoint.Rest.OperationId = functionName.Replace("-", string.Empty, StringComparison.Ordinal);
        endpoint.Rest.IncludeInOpenApi = true;
        if (!string.IsNullOrWhiteSpace(httpParameterName) && !string.IsNullOrWhiteSpace(powerShellParameterName))
        {
            endpoint.ParameterBindings.Add(new ApiParameterBindingConfiguration
            {
                PowerShellParameterName = powerShellParameterName,
                Source = ApiParameterSource.Query,
                Name = httpParameterName,
                Required = ApiRequiredBehavior.Required,
                TypeName = "string"
            });
        }

        return endpoint;
    }

    private static async Task<JsonDocument> ReadSuccessJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(body);
    }

    private static Process StartGeneratedApi(string workingDirectory, string projectFilePath, int port)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(projectFilePath);
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--urls");
        process.StartInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        Assert.True(process.Start());
        return process;
    }

    private static async Task WaitForHealthAsync(HttpClient client, Process process)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Generated API exited before readiness. ExitCode={process.ExitCode}{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }

            try
            {
                using var response = await client.GetAsync("/healthz");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                lastException = exception;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Generated API did not become ready.", lastException);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout));
        if (completed != exitTask)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Process '{fileName}' did not exit within {timeout}.");
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int GetFreeTcpPort()
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

    private const string PolicyScriptSource = """
function Get-ComputerStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName
    )

    [PSCustomObject]@{
        ComputerName = $ComputerName
        Status       = "Online"
        Time         = Get-Date
    }
}

function Get-QualifiedComputerStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName
    )

    [PSCustomObject]@{
        ComputerName = $ComputerName
        Time         = $(Microsoft.PowerShell.Utility\Get-Date)
    }
}

function Get-CurrentDate {
    [CmdletBinding()]
    param()

    Get-Date
}

function Get-UtilitySummary {
    [CmdletBinding()]
    param()

    $summary = 1, 2, 3 | Measure-Object -Sum
    $json = @{ value = "alpha" } | ConvertTo-Json -Compress
    $random = Get-Random -Minimum 1 -Maximum 2

    [PSCustomObject]@{
        Sum    = [int]$summary.Sum
        Json   = $json
        Random = $random
    }
}

function Invoke-PolicyNonTerminatingError {
    [CmdletBinding()]
    param()

    $exception = [System.InvalidOperationException]::new('policy-secret C:\Sensitive\Policy.ps1')
    $record = [System.Management.Automation.ErrorRecord]::new(
        $exception,
        'PolicyNonTerminatingError',
        [System.Management.Automation.ErrorCategory]::InvalidOperation,
        $null)
    $PSCmdlet.WriteError($record)

    [PSCustomObject]@{
        Value = 'partial-output'
    }
}

function Get-UnconfiguredSecret {
    [CmdletBinding()]
    param()

    "secret"
}
""";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
