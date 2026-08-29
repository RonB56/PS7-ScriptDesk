using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiProjectGeneratorTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk Api Generator {Guid.NewGuid():N}");

    public RestApiProjectGeneratorTests()
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
    public async Task Generate_WritesInspectableSelfContainedProjectWithoutLocalPathLeakage()
    {
        var sourcePath = WritePhase7Script("Phase7Api.ps1");
        var destination = Path.Combine(_testDirectory, "GeneratedApi");
        var result = await new ApiProjectGenerator().GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, CreatePhase7Configuration(), destination, "Phase7GeneratedApi"));

        Assert.True(result.Succeeded, result.DetailedLog);
        Assert.True(File.Exists(Path.Combine(destination, "Phase7GeneratedApi.csproj")));
        Assert.True(File.Exists(Path.Combine(destination, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Config", "api.ps7api.json")));
        Assert.True(File.Exists(Path.Combine(destination, "Scripts", "Phase7Api.ps1")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "Api", "RestEndpointMapper.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "Api", "ApiEndpointDiscoveryMapper.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "Api", "ApiKeyAuthenticationService.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "PowerShell", "PowerShellInvocationCoordinator.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.Domain", "Models", "ApiStreamingInvocationModels.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "PowerShell", "ApiStreamingInvocation.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "PowerShell", "PowerShellInvocationStreamSink.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "ServerSentEvents", "SseEndpointMapper.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "WebSockets", "WebSocketEndpointMapper.cs")));
        Assert.True(File.Exists(Path.Combine(destination, "Runtime", "PS7ScriptDesk.RestApiProofHost", "WebSockets", "WebSocketProtocol.cs")));

        Assert.Equal(
            await File.ReadAllBytesAsync(sourcePath),
            await File.ReadAllBytesAsync(Path.Combine(destination, "Scripts", "Phase7Api.ps1")));

        var projectText = await File.ReadAllTextAsync(Path.Combine(destination, "Phase7GeneratedApi.csproj"));
        var programText = await File.ReadAllTextAsync(Path.Combine(destination, "Program.cs"));
        Assert.DoesNotContain("ProjectReference", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PowerShellStudio", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MapSseEndpoints", programText, StringComparison.Ordinal);
        Assert.Contains("UseWebSockets", programText, StringComparison.Ordinal);
        Assert.Contains("MapWebSocketEndpoints", programText, StringComparison.Ordinal);

        var allGeneratedText = string.Join(
            "\n",
            Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
        Assert.DoesNotContain(_testDirectory, allGeneratedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, allGeneratedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Phase7Secret", allGeneratedText, StringComparison.OrdinalIgnoreCase);

        using var configDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "Config", "api.ps7api.json")));
        Assert.Equal("Phase7Api.ps1", configDocument.RootElement.GetProperty("sourceScript").GetString());
        Assert.Equal(string.Empty, configDocument.RootElement.GetProperty("output").GetProperty("outputDirectory").GetString());
        Assert.Contains(
            configDocument.RootElement.GetProperty("endpoints").EnumerateArray(),
            endpoint => endpoint.GetProperty("transport").GetString() == ApiTransport.ServerSentEvents.ToString());
        Assert.Contains(
            configDocument.RootElement.GetProperty("endpoints").EnumerateArray(),
            endpoint => endpoint.GetProperty("transport").GetString() == ApiTransport.WebSocket.ToString());
    }

    [Fact]
    public async Task Generate_IsDeterministicForIdenticalInputs()
    {
        var sourcePath = WritePhase7Script("Phase7Api.ps1");
        var configuration = CreatePhase7Configuration();
        var first = Path.Combine(_testDirectory, "First");
        var second = Path.Combine(_testDirectory, "Second");

        var firstResult = await new ApiProjectGenerator().GenerateAsync(new ApiProjectGenerationRequest(sourcePath, configuration, first, "Phase7GeneratedApi"));
        var secondResult = await new ApiProjectGenerator().GenerateAsync(new ApiProjectGenerationRequest(sourcePath, configuration, second, "Phase7GeneratedApi"));

        Assert.True(firstResult.Succeeded, firstResult.DetailedLog);
        Assert.True(secondResult.Succeeded, secondResult.DetailedLog);
        Assert.Equal(ReadGeneratedTree(first), ReadGeneratedTree(second));
    }

    [Fact]
    public async Task Generate_InvalidConfigurationFailsBeforeWritingDestination()
    {
        var sourcePath = WritePhase7Script("Phase7Api.ps1");
        var configuration = CreatePhase7Configuration();
        configuration.Endpoints[0].ParameterBindings.Clear();
        var destination = Path.Combine(_testDirectory, "InvalidGeneratedApi");

        var result = await new ApiProjectGenerator().GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, configuration, destination, "Phase7GeneratedApi"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.ValidationErrors, error => error.Code == "API076");
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Generate_RefusesNonGeneratedExistingDestination()
    {
        var sourcePath = WritePhase7Script("Phase7Api.ps1");
        var destination = Path.Combine(_testDirectory, "Existing");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "do not overwrite");

        var result = await new ApiProjectGenerator().GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, CreatePhase7Configuration(), destination, "Phase7GeneratedApi", overwriteExistingGeneratedProject: true));

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(destination, "keep.txt")));
        Assert.Equal("do not overwrite", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
    }

    [Fact]
    public async Task Generate_OverwritesOnlyMarkedGeneratedDestination()
    {
        var sourcePath = WritePhase7Script("Phase7Api.ps1");
        var destination = Path.Combine(_testDirectory, "Overwrite");
        var generator = new ApiProjectGenerator();
        var first = await generator.GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, CreatePhase7Configuration(), destination, "Phase7GeneratedApi"));
        Assert.True(first.Succeeded, first.DetailedLog);
        await File.WriteAllTextAsync(Path.Combine(destination, "stale.txt"), "stale");

        var second = await generator.GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, CreatePhase7Configuration(), destination, "Phase7GeneratedApi", overwriteExistingGeneratedProject: true));

        Assert.True(second.Succeeded, second.DetailedLog);
        Assert.False(File.Exists(Path.Combine(destination, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "Phase7GeneratedApi.csproj")));
    }

    [Fact]
    public async Task GeneratedProject_BuildsAndRunsConfiguredApiIndependently()
    {
        var sourcePath = WritePhase7Script("Phase7Api.ps1");
        var destination = Path.Combine(_testDirectory, "Runnable");
        var generation = await new ApiProjectGenerator().GenerateAsync(
            new ApiProjectGenerationRequest(sourcePath, CreatePhase7Configuration(), destination, "Phase7GeneratedApi"));
        Assert.True(generation.Succeeded, generation.DetailedLog);

        var build = await RunProcessAsync("dotnet", $"build \"{generation.ProjectFilePath}\"", destination, TimeSpan.FromMinutes(2));
        Assert.True(build.ExitCode == 0, build.StandardOutput + Environment.NewLine + build.StandardError);

        var port = GetFreeTcpPort();
        using var process = StartGeneratedApi(destination, generation.ProjectFilePath, port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitForOpenApiAsync(client);

            using var get = await client.GetAsync("/api/phase7/computers/LIVE01?view=Detail");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var getJson = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.Equal("LIVE01", getJson.RootElement.GetProperty("computerName").GetString());
            Assert.Equal("Detail", getJson.RootElement.GetProperty("view").GetString());

            using var post = await client.PostAsJsonAsync("/api/phase7/computers/LIVE02", new { displayName = "Live Two", enabled = false });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            using var postJson = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            Assert.Equal("LIVE02", postJson.RootElement.GetProperty("computerName").GetString());
            Assert.False(postJson.RootElement.GetProperty("enabled").GetBoolean());

            using var openApi = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
            var openApiText = await openApi.Content.ReadAsStringAsync();
            Assert.Contains("\"openapi\": \"3.0.3\"", openApiText, StringComparison.Ordinal);
            Assert.Contains("/api/phase7/computers/{computerName}", openApiText, StringComparison.Ordinal);
            Assert.DoesNotContain("Invoke-Phase7Secret", openApiText, StringComparison.Ordinal);

            using var swagger = await client.GetAsync("/swagger");
            Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
            var swaggerText = await swagger.Content.ReadAsStringAsync();
            Assert.Contains("Offline OpenAPI viewer", swaggerText, StringComparison.Ordinal);
            Assert.DoesNotContain("https://unpkg.com", swaggerText, StringComparison.OrdinalIgnoreCase);

            using var discovery = await client.GetAsync("/api/endpoints");
            Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
            var discoveryText = await discovery.Content.ReadAsStringAsync();
            Assert.Contains("phase7-sse-get-computer", discoveryText, StringComparison.Ordinal);
            Assert.Contains("/sse/phase7-sse-get-computer", discoveryText, StringComparison.Ordinal);
            Assert.Contains("phase7-ws-get-computer", discoveryText, StringComparison.Ordinal);
            Assert.Contains("/ws/phase7-ws-get-computer", discoveryText, StringComparison.Ordinal);

            using var sse = await client.GetAsync("/sse/phase7-sse-get-computer?computerName=SSE01&view=Detail", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, sse.StatusCode);
            Assert.Equal("text/event-stream", sse.Content.Headers.ContentType?.MediaType);
            var sseEvents = await ReadSseEventsUntilTerminalAsync(sse);
            Assert.Equal("InvocationCompleted", sseEvents[^1].EventType);
            Assert.Equal("success", sseEvents[^1].Data.GetProperty("statusCode").GetString());
            var sseOutput = Assert.Single(sseEvents, item => item.EventType == "Output");
            Assert.Equal("SSE01", sseOutput.Data.GetProperty("payload").GetProperty("computerName").GetString());
            Assert.Equal("Detail", sseOutput.Data.GetProperty("payload").GetProperty("view").GetString());

            using var failure = await client.GetAsync("/api/phase7/failure/BROKEN");
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            var problem = await failure.Content.ReadAsStringAsync();
            Assert.Contains("requestId", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("private failure", problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    private string WritePhase7Script(string fileName)
    {
        var path = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(path, Phase7ScriptSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static ApiPublishConfiguration CreatePhase7Configuration()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath("Phase7Api.ps1");
        configuration.Api.Title = "Phase 7 Generated API";
        configuration.OpenApi.Title = "Phase 7 Generated API";
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
                EndpointId = "phase7-get-computer",
                Transport = ApiTransport.Rest,
                PowerShellFunctionName = "Get-Phase7Computer",
                DisplayName = "Get phase 7 computer",
                Rest =
                {
                    Method = ApiHttpMethod.Get,
                    RouteTemplate = "/api/phase7/computers/{computerName}",
                    OperationId = "phase7GetComputer",
                    Tags = ["Phase 7"],
                    IncludeInOpenApi = true
                },
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ComputerName",
                        Source = ApiParameterSource.Route,
                        Name = "computerName",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "View",
                        Source = ApiParameterSource.Query,
                        Name = "view",
                        Required = ApiRequiredBehavior.Optional,
                        TypeName = "string"
                    }
                ]
            },
            new ApiEndpointConfiguration
            {
                EndpointId = "phase7-set-computer",
                Transport = ApiTransport.Rest,
                PowerShellFunctionName = "Set-Phase7Computer",
                DisplayName = "Set phase 7 computer",
                Rest =
                {
                    Method = ApiHttpMethod.Post,
                    RouteTemplate = "/api/phase7/computers/{computerName}",
                    OperationId = "phase7SetComputer",
                    Tags = ["Phase 7"],
                    IncludeInOpenApi = true
                },
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ComputerName",
                        Source = ApiParameterSource.Route,
                        Name = "computerName",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "DisplayName",
                        Source = ApiParameterSource.Body,
                        Name = "displayName",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "Enabled",
                        Source = ApiParameterSource.Body,
                        Name = "enabled",
                        Required = ApiRequiredBehavior.Optional,
                        TypeName = "bool"
                    }
                ]
            },
            new ApiEndpointConfiguration
            {
                EndpointId = "phase7-failure",
                Transport = ApiTransport.Rest,
                PowerShellFunctionName = "Invoke-Phase7Failure",
                DisplayName = "Phase 7 failure",
                Rest =
                {
                    Method = ApiHttpMethod.Get,
                    RouteTemplate = "/api/phase7/failure/{computerName}",
                    OperationId = "phase7Failure",
                    Tags = ["Phase 7"],
                    IncludeInOpenApi = false
                },
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ComputerName",
                        Source = ApiParameterSource.Route,
                        Name = "computerName",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    }
                ]
            },
            new ApiEndpointConfiguration
            {
                EndpointId = "phase7-sse-get-computer",
                Transport = ApiTransport.ServerSentEvents,
                PowerShellFunctionName = "Get-Phase7Computer",
                DisplayName = "Stream phase 7 computer",
                Rest =
                {
                    Method = ApiHttpMethod.Get,
                    RouteTemplate = "/api/phase7/sse/computers",
                    OperationId = "phase7SseGetComputer",
                    Tags = ["Phase 7"],
                    IncludeInOpenApi = false
                },
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ComputerName",
                        Source = ApiParameterSource.Query,
                        Name = "computerName",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "View",
                        Source = ApiParameterSource.Query,
                        Name = "view",
                        Required = ApiRequiredBehavior.Optional,
                        TypeName = "string"
                    }
                ]
            },
            new ApiEndpointConfiguration
            {
                EndpointId = "phase7-ws-get-computer",
                Transport = ApiTransport.WebSocket,
                PowerShellFunctionName = "Get-Phase7Computer",
                DisplayName = "WebSocket phase 7 computer",
                Rest =
                {
                    Method = ApiHttpMethod.Get,
                    RouteTemplate = "/api/phase7/ws/computers",
                    OperationId = "phase7WsGetComputer",
                    Tags = ["Phase 7"],
                    IncludeInOpenApi = false
                },
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ComputerName",
                        Source = ApiParameterSource.Query,
                        Name = "computerName",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "View",
                        Source = ApiParameterSource.Query,
                        Name = "view",
                        Required = ApiRequiredBehavior.Optional,
                        TypeName = "string"
                    }
                ]
            }
        ];

        return configuration;
    }

    private static SortedDictionary<string, string> ReadGeneratedTree(string root)
        => new(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                    File.ReadAllText,
                    StringComparer.Ordinal),
            StringComparer.Ordinal);

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

    private static async Task WaitForOpenApiAsync(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/openapi/v1.json");
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

    private static async Task<List<SseEvent>> ReadSseEventsUntilTerminalAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = new List<SseEvent>();
        while (true)
        {
            var item = await ReadNextSseEventAsync(reader);
            events.Add(item);
            if (item.Data.GetProperty("isTerminal").GetBoolean())
            {
                return events;
            }
        }
    }

    private static async Task<SseEvent> ReadNextSseEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? eventType = null;
        long? id = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new EndOfStreamException("The SSE stream ended before a complete event was received.");
            }

            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(data.ToString());
                return new SseEvent(eventType, id ?? 0, document.RootElement.Clone());
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line["event: ".Length..];
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = long.Parse(line["id: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line["data: ".Length..]);
            }
        }
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

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

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

        return new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
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

    private const string Phase7ScriptSource = """
function Get-Phase7Computer {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$ComputerName,

        [ValidateSet('Summary', 'Detail')]
        [string]$View = 'Summary'
    )

    [pscustomobject]@{
        computerName = $ComputerName
        view = $View
        source = 'generated'
    }
}

function Set-Phase7Computer {
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName,

        [Parameter(Mandatory)]
        [ValidateLength(1, 40)]
        [string]$DisplayName,

        [bool]$Enabled = $true
    )

    [pscustomobject]@{
        computerName = $ComputerName
        displayName = $DisplayName
        enabled = $Enabled
    }
}

function Invoke-Phase7Failure {
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName
    )

    throw "private failure: $ComputerName"
}

function Invoke-Phase7Secret {
    'must not be public'
}
""";

    private sealed record SseEvent(string? EventType, long Id, JsonElement Data);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
