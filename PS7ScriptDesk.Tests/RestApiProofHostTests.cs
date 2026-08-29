using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PS7ScriptDesk.RestApiProofHost.Api;
using System.Management.Automation;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Hosting;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiProofHostTests
{
    [Fact]
    public async Task Startup_LoadsConfiguredScriptFunctions()
    {
        await using var host = await StartHostAsync();

        Assert.Equal("127.0.0.1", host.BaseAddress.Host);
        Assert.True(host.RequiredFunctionsVerified);
        Assert.Contains(host.Configuration.Endpoints, endpoint =>
            endpoint.EndpointId == "poc-get-systeminfo" &&
            endpoint.Rest.RouteTemplate == "/api/systeminfo");
        Assert.Contains(host.Configuration.Endpoints, endpoint =>
            endpoint.EndpointId == "poc-post-systeminfo" &&
            endpoint.Rest.RouteTemplate == "/api/systeminfo");
    }

    [Fact]
    public void ApiEndpointResolver_ResolvesEnabledEndpointIdsOnlyWithoutFunctionNameFallback()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath("TestApi.ps1");
        configuration.Endpoints =
        [
            new ApiEndpointConfiguration
            {
                EndpointId = "published-status",
                IsEnabled = true,
                PowerShellFunctionName = "Get-SystemInfo"
            },
            new ApiEndpointConfiguration
            {
                EndpointId = "disabled-status",
                IsEnabled = false,
                PowerShellFunctionName = "Get-DisabledStatus"
            }
        ];

        var resolved = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, "PUBLISHED-STATUS");
        var functionNameAttempt = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, "Get-SystemInfo");
        var disabled = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, "disabled-status");
        var missing = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, "missing-status");

        Assert.True(resolved.IsSuccess);
        Assert.Equal("published-status", resolved.Endpoint?.EndpointId);
        Assert.False(functionNameAttempt.IsSuccess);
        Assert.Equal("EndpointNotFound", functionNameAttempt.ErrorCode);
        Assert.False(disabled.IsSuccess);
        Assert.Equal("EndpointNotFound", disabled.ErrorCode);
        Assert.False(missing.IsSuccess);
        Assert.Equal("EndpointNotFound", missing.ErrorCode);
    }

    [Fact]
    public void ApiEndpointResolver_RequiresSelectedTransportWhenProvided()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath("TestApi.ps1");
        var endpoint = new ApiEndpointConfiguration
        {
            EndpointId = "published-status",
            IsEnabled = true,
            Transport = ApiTransport.WebSocket,
            PowerShellFunctionName = "Get-SystemInfo"
        };
        configuration.Endpoints = [endpoint];

        var webSocket = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, "published-status", ApiTransport.WebSocket);
        var rest = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, "published-status", ApiTransport.Rest);

        Assert.True(webSocket.IsSuccess);
        Assert.False(rest.IsSuccess);
        Assert.Equal("EndpointNotFound", rest.ErrorCode);
    }

    [Fact]
    public async Task EndpointDiscovery_ReportsEffectiveTransportsAndTransportSpecificPaths()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/endpoints");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var endpoints = json.RootElement.GetProperty("endpoints").EnumerateArray().ToList();
        Assert.Contains(endpoints, endpoint =>
            endpoint.GetProperty("endpointId").GetString() == "poc-get-systeminfo" &&
            endpoint.GetProperty("transport").GetString() == ApiTransport.Rest.ToString() &&
            endpoint.GetProperty("method").GetString() == "GET" &&
            endpoint.GetProperty("path").GetString() == "/api/systeminfo");
    }

    [Fact]
    public void ApiEndpointParameterBinder_PreservesRequiredOptionalAndScalarConversionBehavior()
    {
        using var bodyDocument = JsonDocument.Parse(
            """
            {
              "name": "SERVER01",
              "count": 7,
              "longValue": "9000000000",
              "ratio": "1.25",
              "enabled": true
            }
            """);
        var values = bodyDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var endpoint = new ApiEndpointConfiguration
        {
            EndpointId = "binding-test",
            PowerShellFunctionName = "Get-SystemInfo",
            ParameterBindings =
            [
                CreateBinding("Name", "name", "string", ApiRequiredBehavior.Required),
                CreateBinding("Count", "count", "int", ApiRequiredBehavior.Required),
                CreateBinding("LongValue", "longValue", "long", ApiRequiredBehavior.Required),
                CreateBinding("Ratio", "ratio", "double", ApiRequiredBehavior.Required),
                CreateBinding("Enabled", "enabled", "bool", ApiRequiredBehavior.Required),
                CreateBinding("OptionalValue", "optionalValue", "string", ApiRequiredBehavior.Optional)
            ]
        };

        var result = ApiEndpointParameterBinder.Shared.Bind(
            endpoint,
            binding => values.TryGetValue(binding.Name, out var value)
                ? ApiParameterBindingValue.Present(value)
                : ApiParameterBindingValue.Missing);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal("SERVER01", result.Parameters["Name"]);
        Assert.Equal(7, result.Parameters["Count"]);
        Assert.Equal(9000000000L, result.Parameters["LongValue"]);
        Assert.Equal(1.25d, result.Parameters["Ratio"]);
        Assert.Equal(true, result.Parameters["Enabled"]);
        Assert.False(result.Parameters.ContainsKey("OptionalValue"));
    }

    [Fact]
    public void ApiEndpointParameterBinder_RejectsMissingRequiredAndInvalidConvertedValues()
    {
        using var bodyDocument = JsonDocument.Parse("""{ "count": "not-an-int" }""");
        var endpoint = new ApiEndpointConfiguration
        {
            EndpointId = "invalid-binding-test",
            PowerShellFunctionName = "Get-SystemInfo",
            ParameterBindings =
            [
                CreateBinding("Name", "name", "string", ApiRequiredBehavior.Required),
                CreateBinding("Count", "count", "int", ApiRequiredBehavior.Required)
            ]
        };

        var missing = ApiEndpointParameterBinder.Shared.Bind(
            endpoint,
            binding => binding.Name == "count"
                ? ApiParameterBindingValue.Present(bodyDocument.RootElement.GetProperty("count").Clone())
                : ApiParameterBindingValue.Missing);
        var invalid = ApiEndpointParameterBinder.Shared.Bind(
            endpoint,
            binding => binding.Name == "name"
                ? ApiParameterBindingValue.Present("SERVER01")
                : ApiParameterBindingValue.Present(bodyDocument.RootElement.GetProperty("count").Clone()));

        Assert.False(missing.IsValid);
        Assert.Equal("MissingParameter", missing.ErrorCode);
        Assert.False(invalid.IsValid);
        Assert.Equal("InvalidParameter", invalid.ErrorCode);
        Assert.Contains("integer", invalid.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestParameterBinder_UsesSharedBinderAndDoesNotAllowClientServerDefinedOverrides()
    {
        var variableName = $"PS7SCRIPT_DESK_PHASE1_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "SERVER-OWNED");
        try
        {
            var endpoint = new ApiEndpointConfiguration
            {
                EndpointId = "server-defined-test",
                PowerShellFunctionName = "Get-SecureServerDefined",
                ParameterBindings =
                [
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ClientValue",
                        Source = ApiParameterSource.Body,
                        Name = "clientValue",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new ApiParameterBindingConfiguration
                    {
                        PowerShellParameterName = "ServerSecret",
                        Source = ApiParameterSource.ServerDefined,
                        Name = "serverSecret",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string",
                        IsSecretSensitive = true,
                        ServerValue = new ApiServerDefinedValue
                        {
                            Kind = ApiServerDefinedValueKind.EnvironmentVariable,
                            Value = variableName
                        }
                    }
                ]
            };
            var context = new DefaultHttpContext { TraceIdentifier = "phase1-rest-binder" };
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                """{ "clientValue": "CLIENT", "serverSecret": "REQUEST-OVERRIDE" }"""));

            var result = await RestParameterBinder.Shared.BindAsync(context, endpoint, CancellationToken.None);

            Assert.True(result.IsValid, result.ErrorMessage);
            Assert.Equal("CLIENT", result.Parameters["ClientValue"]);
            Assert.Equal("SERVER-OWNED", result.Parameters["ServerSecret"]);
            Assert.DoesNotContain("REQUEST-OVERRIDE", result.Parameters.Values.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task GetSystemInfo_ReturnsCleanJsonObject()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/systeminfo?computerName=SERVER01");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal("SERVER01", json.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal("System information requested for SERVER01", json.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task GetSystemInfo_MissingRequiredParameter_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/systeminfo");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid request.", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("https://ps7scriptdesk.local/errors/request-binding-failure", json.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("requestId").GetString()));
        Assert.Contains("computerName", json.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSystemInfo_EmptyRequiredParameter_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/systeminfo?computerName=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("SERVER01; Get-Process")]
    [InlineData("$(Get-Process)")]
    public async Task GetSystemInfo_InjectionLikeString_IsParameterDataOnly(string value)
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/api/systeminfo?computerName={Uri.EscapeDataString(value)}");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal(value, json.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal($"System information requested for {value}", json.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task PostSystemInfo_ReturnsEquivalentJsonObject()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/systeminfo", new { computerName = "SERVER02" });
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal("SERVER02", json.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal("System information requested for SERVER02", json.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task PostSystemInfo_MalformedJson_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsync("/api/systeminfo", new StringContent("{", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSystemInfo_MissingComputerName_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/systeminfo", new { name = "SERVER02" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSystemInfo_EmptyComputerName_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/systeminfo", new { computerName = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PowerShellFailure_ReturnsSanitizedServerError()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/failure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("PowerShell invocation failed.", body, StringComparison.Ordinal);
        Assert.Contains("configured PowerShell operation", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".ps1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Intentional test failure", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Runspace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Management.Automation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessResponse_DoesNotExposeStreamsByDefault()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/phase5b/streams");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stream-success", json.RootElement.GetProperty("Value").GetString());
        Assert.DoesNotContain("phase5b-warning", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonTerminatingError_ReturnsSanitizedProblemWithoutPartialOutput()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/phase5b/nonterminating");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("https://ps7scriptdesk.local/errors/powershell-non-terminating-error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("PowerShell invocation failed.", json.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("requestId").GetString()));
        Assert.DoesNotContain("partial-output-must-not-leak", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phase5b-nonterminating-secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Sensitive", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BindingAndValidationFailures_MapToBadRequestProblemDetails()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var bindingResponse = await client.GetAsync("/api/phase4/delay?requestId=bad&milliseconds=not-an-int");
        using var validationResponse = await client.GetAsync("/api/phase5b/validation?value=10");
        using var bindingJson = await ReadJsonAsync(bindingResponse);
        using var validationJson = await ReadJsonAsync(validationResponse);

        Assert.Equal(HttpStatusCode.BadRequest, bindingResponse.StatusCode);
        Assert.Equal("https://ps7scriptdesk.local/errors/request-binding-failure", bindingJson.RootElement.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);
        Assert.Equal("https://ps7scriptdesk.local/errors/powershell-validation-failure", validationJson.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task MultiplePipelineResults_ReturnJsonArray()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/numbers");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal([1, 2, 3], json.RootElement.EnumerateArray().Select(element => element.GetInt32()).ToArray());
    }

    [Fact]
    public async Task UnknownRoute_ReturnsNotFound()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequestCannotSelectArbitraryFunction()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/systeminfo", new { function = "Invoke-TestFailure", computerName = "SERVER03" });
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal("SERVER03", json.RootElement.GetProperty("ComputerName").GetString());
    }

    [Theory]
    [InlineData("/api/script")]
    [InlineData("/api/invoke")]
    [InlineData("/api/command")]
    public async Task NoEndpointAcceptsArbitraryScriptOrCommand(string route)
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(route, new { script = "Get-Process", command = "Remove-Item" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HostShutdown_DisposesPowerShellResources()
    {
        var host = await StartHostAsync();

        Assert.False(host.IsPowerShellDisposed);
        await host.DisposeAsync();

        Assert.True(host.IsDisposed);
        Assert.True(host.IsPowerShellDisposed);
    }

    [Fact]
    public async Task RepeatedStartStop_WorksCleanly()
    {
        for (var index = 0; index < 2; index++)
        {
            await using var host = await StartHostAsync();
            using var client = host.CreateClient();
            using var response = await client.GetAsync("/api/systeminfo?computerName=SERVER01");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task StartupAndRequestLatency_AreNotPathological()
    {
        var stopwatch = Stopwatch.StartNew();
        await using var host = await StartHostAsync();
        var startupMilliseconds = stopwatch.ElapsedMilliseconds;
        using var client = host.CreateClient();

        stopwatch.Restart();
        using var first = await client.GetAsync("/api/systeminfo?computerName=SERVER01");
        var firstRequestMilliseconds = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        using var second = await client.GetAsync("/api/systeminfo?computerName=SERVER01");
        var secondRequestMilliseconds = stopwatch.ElapsedMilliseconds;

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(startupMilliseconds < 10000, $"Startup took {startupMilliseconds} ms.");
        Assert.True(firstRequestMilliseconds < 5000, $"First request took {firstRequestMilliseconds} ms.");
        Assert.True(secondRequestMilliseconds < 5000, $"Second request took {secondRequestMilliseconds} ms.");
    }

    [Fact]
    public async Task PowerShellInvoker_LoadsAndExecutesConfiguredFunction()
    {
        await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Get-SystemInfo"]);

        var result = await coordinator.InvokeAsync(CreateSystemInfoRequest("SERVER04"), CancellationToken.None);
        var normalized = (Dictionary<string, object?>)NormalizeSuccess(result.Output)!;

        Assert.True(coordinator.RequiredFunctionsVerified);
        Assert.True(
            result.Status == ApiInvocationStatus.Success,
            $"Expected Success but got {result.Status}: {string.Join(" | ", result.Streams.Select(stream => $"{stream.StreamName}:{stream.Message}"))}");
        Assert.Equal("SERVER04", normalized["ComputerName"]);
    }

    [Fact]
    public async Task PowerShellInvoker_RejectsUnconfiguredFunction()
    {
        await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Get-SystemInfo"]);

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Invoke-TestFailure" },
            CancellationToken.None);

        Assert.Equal(ApiInvocationStatus.InvalidFunction, result.Status);
    }

    [Fact]
    public void ResultNormalizer_HandlesScalarsAndNoOutput()
    {
        Assert.Equal("value", NormalizeSuccess([PSObject.AsPSObject("value")]));
        Assert.Equal(42, NormalizeSuccess([PSObject.AsPSObject(42)]));
        Assert.Equal(true, NormalizeSuccess([PSObject.AsPSObject(true)]));
        Assert.Null(NormalizeSuccess(Array.Empty<PSObject>()));
    }

    [Fact]
    public async Task ResultNormalizer_HandlesExplicitNullOutput()
    {
        await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Get-ExplicitNull"]);

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Get-ExplicitNull" },
            CancellationToken.None);

        Assert.True(
            result.Status == ApiInvocationStatus.Success,
            $"Expected Success but got {result.Status}: {string.Join(" | ", result.Streams.Select(stream => $"{stream.StreamName}:{stream.Message}"))}");
        Assert.Null(NormalizeSuccess(result.Output));
    }

    [Fact]
    public void ResultNormalizer_HandlesPsCustomObjectAndMultiplePipelineObjects()
    {
        var customObject = new PSObject();
        customObject.Properties.Add(new PSNoteProperty("ComputerName", "SERVER05"));
        customObject.Properties.Add(new PSNoteProperty("Enabled", true));

        var normalizedObject = (Dictionary<string, object?>)NormalizeSuccess([customObject])!;
        var normalizedArray = (List<object?>)NormalizeSuccess([PSObject.AsPSObject(1), PSObject.AsPSObject(2)])!;

        Assert.Equal("SERVER05", normalizedObject["ComputerName"]);
        Assert.Equal(true, normalizedObject["Enabled"]);
        Assert.Equal([1, 2], normalizedArray.Cast<int>().ToArray());
    }

    [Fact]
    public void ResultNormalizer_HandlesRequiredScalarTypes()
    {
        var dateTime = new DateTime(2026, 8, 21, 12, 30, 0, DateTimeKind.Utc);
        var dateTimeOffset = new DateTimeOffset(dateTime);
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("text", NormalizeSuccess([PSObject.AsPSObject("text")]));
        Assert.Equal("Z", NormalizeSuccess([PSObject.AsPSObject('Z')]));
        Assert.Equal(false, NormalizeSuccess([PSObject.AsPSObject(false)]));
        Assert.Equal((byte)1, NormalizeSuccess([PSObject.AsPSObject((byte)1)]));
        Assert.Equal((sbyte)-1, NormalizeSuccess([PSObject.AsPSObject((sbyte)-1)]));
        Assert.Equal((short)-2, NormalizeSuccess([PSObject.AsPSObject((short)-2)]));
        Assert.Equal((ushort)2, NormalizeSuccess([PSObject.AsPSObject((ushort)2)]));
        Assert.Equal(3, NormalizeSuccess([PSObject.AsPSObject(3)]));
        Assert.Equal((uint)4, NormalizeSuccess([PSObject.AsPSObject((uint)4)]));
        Assert.Equal(5L, NormalizeSuccess([PSObject.AsPSObject(5L)]));
        Assert.Equal((ulong)6, NormalizeSuccess([PSObject.AsPSObject((ulong)6)]));
        Assert.Equal(7.5f, NormalizeSuccess([PSObject.AsPSObject(7.5f)]));
        Assert.Equal(8.25d, NormalizeSuccess([PSObject.AsPSObject(8.25d)]));
        Assert.Equal(9.75m, NormalizeSuccess([PSObject.AsPSObject(9.75m)]));
        Assert.Equal(dateTime, NormalizeSuccess([PSObject.AsPSObject(dateTime)]));
        Assert.Equal(dateTimeOffset, NormalizeSuccess([PSObject.AsPSObject(dateTimeOffset)]));
        Assert.Equal(guid, NormalizeSuccess([PSObject.AsPSObject(guid)]));
        Assert.Equal("Second", NormalizeSuccess([PSObject.AsPSObject(NormalizerTestEnum.Second)]));
        Assert.Null(NormalizeSuccess(new List<PSObject> { null! }));
    }

    [Fact]
    public void ResultNormalizer_HandlesObjectsDictionariesAndCollections()
    {
        var nestedPsObject = new PSObject();
        nestedPsObject.Properties.Add(new PSNoteProperty("ChildName", "nested"));
        var psObject = new PSObject();
        psObject.Properties.Add(new PSNoteProperty("Name", "root"));
        psObject.Properties.Add(new PSNoteProperty("Child", nestedPsObject));
        var normalizedPsObject = (Dictionary<string, object?>)NormalizeSuccess([psObject])!;
        var normalizedChild = (Dictionary<string, object?>)normalizedPsObject["Child"]!;

        Assert.Equal("root", normalizedPsObject["Name"]);
        Assert.Equal("nested", normalizedChild["ChildName"]);

        var dotNetObject = (Dictionary<string, object?>)NormalizeSuccess([PSObject.AsPSObject(new NormalizerNode("parent", new NormalizerNode("child")))])!;
        var dotNetChild = (Dictionary<string, object?>)dotNetObject["Child"]!;
        Assert.Equal("parent", dotNetObject["Name"]);
        Assert.Equal("child", dotNetChild["Name"]);

        var dictionary = new Dictionary<object, object?>
        {
            ["Text"] = "value",
            [5] = "number-key"
        };
        var normalizedDictionary = (Dictionary<string, object?>)NormalizeSuccess([PSObject.AsPSObject(dictionary)])!;
        Assert.Equal("value", normalizedDictionary["Text"]);
        Assert.Equal("number-key", normalizedDictionary["5"]);

        var hashtable = new System.Collections.Hashtable
        {
            ["First"] = 1,
            ["Second"] = new[] { "a", "b" }
        };
        var normalizedHashtable = (Dictionary<string, object?>)NormalizeSuccess([PSObject.AsPSObject(hashtable)])!;
        Assert.Equal(1, normalizedHashtable["First"]);
        Assert.Equal(["a", "b"], ((List<object?>)normalizedHashtable["Second"]!).Cast<string>().ToArray());

        Assert.Equal([1, 2, 3], ((List<object?>)NormalizeSuccess([PSObject.AsPSObject(new[] { 1, 2, 3 })])!).Cast<int>().ToArray());
        Assert.Equal(["x", "y"], ((List<object?>)NormalizeSuccess([PSObject.AsPSObject(new List<string> { "x", "y" })])!).Cast<string>().ToArray());
    }

    [Fact]
    public void ResultNormalizer_PreservesPipelineCardinality()
    {
        Assert.Null(NormalizeSuccess(Array.Empty<PSObject>()));
        Assert.Equal("single", NormalizeSuccess([PSObject.AsPSObject("single")]));
        Assert.Equal(["one", "two"], ((List<object?>)NormalizeSuccess([PSObject.AsPSObject("one"), PSObject.AsPSObject("two")])!).Cast<string>().ToArray());
    }

    [Fact]
    public void ResultNormalizer_EnforcesDepthLimitDeterministically()
    {
        var runtime = CreateRuntime(serializationDepth: 3);
        var atLimit = new Dictionary<string, object?>
        {
            ["Child"] = new Dictionary<string, object?> { ["Value"] = "leaf" }
        };
        var beyondLimit = new Dictionary<string, object?>
        {
            ["Child"] = new Dictionary<string, object?>
            {
                ["Grandchild"] = new Dictionary<string, object?> { ["Value"] = "too deep" }
            }
        };

        Assert.True(NormalizeResult([PSObject.AsPSObject(atLimit)], runtime).IsSuccess);

        var failure = NormalizeResult([PSObject.AsPSObject(beyondLimit)], runtime);
        Assert.False(failure.IsSuccess);
        Assert.Equal(NormalizationFailureKind.DepthExceeded, failure.FailureKind);
    }

    [Fact]
    public void ResultNormalizer_DetectsSelfAndTwoObjectCycles()
    {
        var self = new Dictionary<string, object?>();
        self["Self"] = self;

        var first = new Dictionary<string, object?>();
        var second = new Dictionary<string, object?>();
        first["Second"] = second;
        second["First"] = first;

        var selfFailure = NormalizeResult([PSObject.AsPSObject(self)]);
        var twoObjectFailure = NormalizeResult([PSObject.AsPSObject(first)]);

        Assert.Equal(NormalizationFailureKind.CycleDetected, selfFailure.FailureKind);
        Assert.Equal(NormalizationFailureKind.CycleDetected, twoObjectFailure.FailureKind);
    }

    [Fact]
    public void ResultNormalizer_EnforcesTopLevelAndNestedItemLimits()
    {
        var runtime = CreateRuntime(responseItemLimit: 3);

        Assert.True(NormalizeResult([PSObject.AsPSObject(1), PSObject.AsPSObject(2), PSObject.AsPSObject(3)], runtime).IsSuccess);
        Assert.Equal(
            NormalizationFailureKind.ItemLimitExceeded,
            NormalizeResult([PSObject.AsPSObject(1), PSObject.AsPSObject(2), PSObject.AsPSObject(3), PSObject.AsPSObject(4)], runtime).FailureKind);

        Assert.True(NormalizeResult([PSObject.AsPSObject(new[] { 1, 2, 3 })], runtime).IsSuccess);
        Assert.Equal(
            NormalizationFailureKind.ItemLimitExceeded,
            NormalizeResult([PSObject.AsPSObject(new[] { 1, 2, 3, 4 })], runtime).FailureKind);
    }

    [Fact]
    public void ResultNormalizer_EnforcesUtf8ByteLimitIncludingOversizedString()
    {
        var underLimit = NormalizeResult([PSObject.AsPSObject("ok")], CreateRuntime(responseByteLimit: 16));
        var overLimit = NormalizeResult([PSObject.AsPSObject(new string('x', 64))], CreateRuntime(responseByteLimit: 16));

        Assert.True(underLimit.IsSuccess);
        Assert.True(underLimit.SerializedByteCount > 0);
        Assert.Equal(NormalizationFailureKind.ByteLimitExceeded, overLimit.FailureKind);
    }

    [Fact]
    public async Task ResultNormalizer_RejectsPowerShellFormattingObjects()
    {
        await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Invoke-Phase5FormattingOutput"]);

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Invoke-Phase5FormattingOutput" },
            CancellationToken.None);
        var normalized = NormalizeResult(result.Output);

        Assert.Equal(ApiInvocationStatus.Success, result.Status);
        Assert.False(normalized.IsSuccess);
        Assert.Equal(NormalizationFailureKind.FormattingObjectRejected, normalized.FailureKind);
    }

    [Fact]
    public async Task FormattingOutputEndpoint_ReturnsSanitizedServerError()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/phase5/formatting");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("PowerShell output could not be serialized.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.PowerShell.Commands.Internal.Format", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatEntryData", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultNormalizer_HandlesGetterFailuresSafely()
    {
        var result = NormalizeResult([PSObject.AsPSObject(new ThrowingPropertyObject())]);

        Assert.False(result.IsSuccess);
        Assert.Equal(NormalizationFailureKind.PropertyGetterFailed, result.FailureKind);
        Assert.DoesNotContain("getter boom", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResultNormalizer_RejectsCaseInsensitiveDuplicatePropertyNames()
    {
        var dictionary = new Dictionary<string, object?>
        {
            ["Name"] = "first",
            ["name"] = "second"
        };

        var result = NormalizeResult([PSObject.AsPSObject(dictionary)]);

        Assert.False(result.IsSuccess);
        Assert.Equal(NormalizationFailureKind.DuplicatePropertyName, result.FailureKind);
    }

    [Fact]
    public void ResultNormalizer_Stress_NormalizesModerateNestedPayloadWithoutRetainedState()
    {
        var runtime = CreateRuntime(responseItemLimit: 5000, responseByteLimit: 1024 * 1024, serializationDepth: 6);
        var payload = Enumerable.Range(0, 750)
            .Select(index => PSObject.AsPSObject(new Dictionary<string, object?>
            {
                ["Index"] = index,
                ["Nested"] = new Dictionary<string, object?> { ["Value"] = $"item-{index}" }
            }))
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var first = NormalizeResult(payload, runtime);
        var second = NormalizeResult(payload, runtime);
        stopwatch.Stop();

        Assert.True(first.IsSuccess, first.SafeMessage);
        Assert.True(second.IsSuccess, second.SafeMessage);
        Assert.True(first.SerializedByteCount > 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Normalization sanity check took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task RunspacePoolLifecycle_RepeatedInitializeDispose_WorksCleanly()
    {
        for (var index = 0; index < 2; index++)
        {
            await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Get-SystemInfo"]);

            var result = await coordinator.InvokeAsync(CreateSystemInfoRequest($"SERVER{index}"), CancellationToken.None);

            Assert.Equal(ApiInvocationStatus.Success, result.Status);
            Assert.True(coordinator.RequiredFunctionsVerified);
        }
    }

    [Fact]
    public async Task PowerShellFailure_CapturesErrorStreamSafely()
    {
        await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Invoke-TestFailure"]);

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Invoke-TestFailure" },
            CancellationToken.None);

        Assert.Equal(ApiInvocationStatus.PowerShellTerminatingFailure, result.Status);
        Assert.Contains(result.Streams, stream => stream.StreamName is "Error" or "Exception");
        Assert.DoesNotContain(result.Streams, stream => stream.Message.Contains(ResolveProofScriptPath(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Streams, stream => stream.Message.Contains("Intentional test failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PowerShellStreamPolicy_WarningsSucceedAndRetainedStreamsAreCapped()
    {
        await using var coordinator = await CreateCoordinatorAsync(
            CreateRuntime(retainedStreamEntries: 12),
            ["Invoke-Phase5BStreams"]);

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Invoke-Phase5BStreams" },
            CancellationToken.None);

        Assert.True(
            result.Status == ApiInvocationStatus.Success,
            $"Expected Success but got {result.Status}: {string.Join(" | ", result.Streams.Select(stream => $"{stream.StreamName}:{stream.Message}"))}");
        Assert.True(result.Streams.Count <= 12);
        Assert.Contains(result.Streams, stream => stream.StreamName == "Warning");
    }

    [Fact]
    public async Task PowerShellNonTerminatingError_FailsWithoutPartialOutputOrRawErrorText()
    {
        await using var coordinator = await CreateCoordinatorAsync(allowedFunctions: ["Invoke-Phase5BNonTerminatingError"]);

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Invoke-Phase5BNonTerminatingError" },
            CancellationToken.None);

        Assert.Equal(ApiInvocationStatus.PowerShellNonTerminatingError, result.Status);
        Assert.Empty(result.Output);
        Assert.Contains(result.Streams, stream => stream.StreamName == "Error");
        Assert.DoesNotContain(result.Streams, stream => stream.Message.Contains("phase5b-nonterminating-secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Streams, stream => stream.Message.Contains("C:\\Sensitive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PowerShellParameterBindingAndValidationFailures_AreClassifiedSeparately()
    {
        await using var coordinator = await CreateCoordinatorAsync(
            allowedFunctions: ["Invoke-Phase4Delay", "Invoke-Phase5BValidation"]);

        var binding = await coordinator.InvokeAsync(
            new ApiInvocationRequest
            {
                FunctionName = "Invoke-Phase4Delay",
                Parameters = new Dictionary<string, object?>
                {
                    ["RequestId"] = "binding",
                    ["Milliseconds"] = "not-an-int"
                }
            },
            CancellationToken.None);
        var validation = await coordinator.InvokeAsync(
            new ApiInvocationRequest
            {
                FunctionName = "Invoke-Phase5BValidation",
                Parameters = new Dictionary<string, object?> { ["Value"] = 10 }
            },
            CancellationToken.None);

        Assert.Equal(ApiInvocationStatus.PowerShellParameterBindingFailure, binding.Status);
        Assert.Equal(ApiInvocationStatus.PowerShellValidationFailure, validation.Status);
    }

    [Fact]
    public void ProblemDetailsMapper_MapsQueueTimeoutHostAndNormalizationStatusesDeterministically()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "phase5b-request-id"
        };
        context.Request.Path = "/api/test";

        var queueFull = ApiInvocationProblemDetailsMapper.CreateProblemDetails(
            ApiInvocationResult.Failure(ApiInvocationStatus.QueueFull, "The PowerShell invocation queue is full."),
            context);
        var timeout = ApiInvocationProblemDetailsMapper.CreateProblemDetails(
            ApiInvocationResult.Failure(ApiInvocationStatus.InvocationTimedOut, "The PowerShell invocation timed out."),
            context);
        var host = ApiInvocationProblemDetailsMapper.CreateProblemDetails(
            ApiInvocationResult.Failure(ApiInvocationStatus.HostUnavailable, "The PowerShell host is not available."),
            context);
        var normalization = ApiInvocationProblemDetailsMapper.CreateProblemDetails(
            ApiInvocationResult.Failure(
                ApiInvocationStatus.NormalizationFailure,
                "The configured PowerShell operation returned output that could not be converted safely.",
                normalizationFailureKind: NormalizationFailureKind.CycleDetected),
            context);
        var outputLimit = ApiInvocationProblemDetailsMapper.CreateProblemDetails(
            ApiInvocationResult.Failure(
                ApiInvocationStatus.SerializationOutputLimitFailure,
                "The configured PowerShell operation returned output that exceeded a configured response limit.",
                normalizationFailureKind: NormalizationFailureKind.ByteLimitExceeded),
            context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, queueFull.Status);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, timeout.Status);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, host.Status);
        Assert.Equal(StatusCodes.Status500InternalServerError, normalization.Status);
        Assert.Equal(StatusCodes.Status500InternalServerError, outputLimit.Status);
        Assert.Equal("https://ps7scriptdesk.local/errors/normalization-failure", normalization.Type);
        Assert.Equal("https://ps7scriptdesk.local/errors/serialization-output-limit-failure", outputLimit.Type);
        Assert.Equal("phase5b-request-id", normalization.Extensions["requestId"]);
        Assert.Equal("CycleDetected", normalization.Extensions["failureKind"]);
        Assert.Equal("ByteLimitExceeded", outputLimit.Extensions["failureKind"]);
    }

    [Fact]
    public void ErrorDescriptorMapper_MatchesRestProblemDetailsShape()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "phase1-descriptor-request-id"
        };
        context.Request.Path = "/api/test";

        foreach (var status in Enum.GetValues<ApiInvocationStatus>())
        {
            var descriptor = ApiInvocationErrorDescriptorMapper.Describe(status);
            var problem = ApiInvocationProblemDetailsMapper.CreateProblemDetails(
                ApiInvocationResult.Failure(status, string.Empty),
                context);

            Assert.Equal(descriptor.Type, problem.Type);
            Assert.Equal(descriptor.Title, problem.Title);
            Assert.Equal(descriptor.StatusCode, problem.Status);
            Assert.Equal(descriptor.Detail, problem.Detail);
            Assert.DoesNotContain("secret", descriptor.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stack", descriptor.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RunspacePool_AllowsConcurrentExecution()
    {
        await using var coordinator = await CreateCoordinatorAsync(CreateRuntime(maxConcurrency: 4, queueLimit: 4), ["Invoke-Phase4Delay"]);
        var stopwatch = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, 4)
            .Select(index => coordinator.InvokeAsync(CreateDelayRequest($"overlap-{index}", 500), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        stopwatch.Stop();
        Assert.All(results, AssertInvocationSuccess);
        Assert.True(stopwatch.ElapsedMilliseconds < 1600, $"Expected overlapping runspace execution, but elapsed {stopwatch.ElapsedMilliseconds} ms.");
        Assert.True(coordinator.CreateMetricsSnapshot().MaxObservedActiveInvocationCount > 1);
    }

    [Fact]
    public async Task ConcurrencyBound_DoesNotExceedConfiguredMaximum()
    {
        await using var coordinator = await CreateCoordinatorAsync(CreateRuntime(maxConcurrency: 2, queueLimit: 8), ["Invoke-Phase4Delay"]);

        var tasks = Enumerable.Range(0, 6)
            .Select(index => coordinator.InvokeAsync(CreateDelayRequest($"bounded-{index}", 250), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        var metrics = coordinator.CreateMetricsSnapshot();

        Assert.All(results, AssertInvocationSuccess);
        Assert.True(metrics.MaxObservedActiveInvocationCount <= 2, $"Observed {metrics.MaxObservedActiveInvocationCount} active invocations.");
        Assert.Equal(2, metrics.MaxConcurrency);
    }

    [Fact]
    public async Task QueueCapacity_ReturnsQueueFullWhenExecutionAndQueueAreOccupied()
    {
        await using var coordinator = await CreateCoordinatorAsync(CreateRuntime(maxConcurrency: 1, queueLimit: 0), ["Invoke-Phase4Delay"]);
        var first = coordinator.InvokeAsync(CreateDelayRequest("occupy", 500), CancellationToken.None);
        await WaitForActiveInvocationAsync(coordinator);

        var rejected = await coordinator.InvokeAsync(CreateDelayRequest("rejected", 10), CancellationToken.None);
        var completed = await first;

        Assert.Equal(ApiInvocationStatus.QueueFull, rejected.Status);
        Assert.Equal(ApiInvocationStatus.Success, completed.Status);
        Assert.True(coordinator.CreateMetricsSnapshot().RejectedQueueFullCount >= 1);
    }

    [Fact]
    public async Task QueueWaitTimeout_ReturnsTimedOutWaitWithoutStartingPowerShell()
    {
        await using var coordinator = await CreateCoordinatorAsync(
            CreateRuntime(maxConcurrency: 1, queueLimit: 1, queueWaitTimeout: TimeSpan.FromMilliseconds(100)),
            ["Invoke-Phase4Delay"]);
        var first = coordinator.InvokeAsync(CreateDelayRequest("occupy", 500), CancellationToken.None);
        await WaitForActiveInvocationAsync(coordinator);

        var queued = await coordinator.InvokeAsync(CreateDelayRequest("queued-timeout", 10), CancellationToken.None);
        var completed = await first;

        Assert.Equal(ApiInvocationStatus.QueueWaitTimedOut, queued.Status);
        Assert.Equal(ApiInvocationStatus.Success, completed.Status);
        Assert.True(coordinator.CreateMetricsSnapshot().QueueTimeoutCount >= 1);
    }

    [Fact]
    public async Task InvocationTimeout_StopsPowerShellRebuildsPoolAndAllowsRecovery()
    {
        await using var coordinator = await CreateCoordinatorAsync(
            CreateRuntime(maxConcurrency: 1, queueLimit: 2, defaultTimeout: TimeSpan.FromMilliseconds(150)),
            ["Invoke-Phase4Delay", "Get-SystemInfo"]);

        var timedOut = await coordinator.InvokeAsync(CreateDelayRequest("timeout", 1000), CancellationToken.None);
        var recovery = await coordinator.InvokeAsync(CreateSystemInfoRequest("SERVER-TIMEOUT-RECOVERY"), CancellationToken.None);
        var metrics = coordinator.CreateMetricsSnapshot();

        Assert.Equal(ApiInvocationStatus.InvocationTimedOut, timedOut.Status);
        Assert.True(timedOut.RequiresPoolRebuild);
        Assert.Equal(ApiInvocationStatus.Success, recovery.Status);
        Assert.True(metrics.PoolRebuildCount >= 1);
        Assert.Equal(0, metrics.ActiveInvocationCount);
    }

    [Fact]
    public async Task CallerCancellation_StopsPowerShellRebuildsPoolAndAllowsRecovery()
    {
        await using var coordinator = await CreateCoordinatorAsync(
            CreateRuntime(maxConcurrency: 1, queueLimit: 2, defaultTimeout: TimeSpan.FromSeconds(5)),
            ["Invoke-Phase4Delay", "Get-SystemInfo"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var canceled = await coordinator.InvokeAsync(CreateDelayRequest("cancel", 1000), cancellation.Token);
        var recovery = await coordinator.InvokeAsync(CreateSystemInfoRequest("SERVER-CANCEL-RECOVERY"), CancellationToken.None);

        Assert.Equal(ApiInvocationStatus.CallerCanceled, canceled.Status);
        Assert.True(canceled.RequiresPoolRebuild);
        Assert.Equal(ApiInvocationStatus.Success, recovery.Status);
        Assert.True(coordinator.CreateMetricsSnapshot().PoolRebuildCount >= 1);
    }

    [Fact]
    public async Task CrossRequestState_NormalParametersRemainIndependentAndGlobalStateRequiresPoolRecovery()
    {
        await using var coordinator = await CreateCoordinatorAsync(
            CreateRuntime(maxConcurrency: 1, queueLimit: 2),
            ["Get-SystemInfo", "Set-Phase4GlobalState", "Get-Phase4GlobalState"]);

        var first = await coordinator.InvokeAsync(CreateSystemInfoRequest("SERVER-A"), CancellationToken.None);
        var second = await coordinator.InvokeAsync(CreateSystemInfoRequest("SERVER-B"), CancellationToken.None);
        var firstJson = (Dictionary<string, object?>)NormalizeSuccess(first.Output)!;
        var secondJson = (Dictionary<string, object?>)NormalizeSuccess(second.Output)!;

        Assert.Equal("SERVER-A", firstJson["ComputerName"]);
        Assert.Equal("SERVER-B", secondJson["ComputerName"]);

        await coordinator.InvokeAsync(
            new ApiInvocationRequest
            {
                FunctionName = "Set-Phase4GlobalState",
                Parameters = new Dictionary<string, object?> { ["Value"] = "LEAK-CHECK" }
            },
            CancellationToken.None);
        var persisted = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Get-Phase4GlobalState" },
            CancellationToken.None);
        var persistedJson = (Dictionary<string, object?>)NormalizeSuccess(persisted.Output)!;

        Assert.Equal(ApiInvocationStatus.Success, persisted.Status);
        Assert.Equal("LEAK-CHECK", persistedJson["Value"]);

        await coordinator.RequestPoolRebuildAsync();
        var afterRecovery = await coordinator.InvokeAsync(
            new ApiInvocationRequest { FunctionName = "Get-Phase4GlobalState" },
            CancellationToken.None);

        Assert.Null(NormalizeSuccess(afterRecovery.Output));
    }

    [Fact]
    public async Task ShutdownDuringActivity_CompletesWithinBoundedDuration()
    {
        var coordinator = await CreateCoordinatorAsync(CreateRuntime(maxConcurrency: 1, queueLimit: 2), ["Invoke-Phase4Delay"]);
        var tasks = Enumerable.Range(0, 3)
            .Select(index => coordinator.InvokeAsync(CreateDelayRequest($"shutdown-{index}", 1000), CancellationToken.None))
            .ToArray();
        await WaitForActiveInvocationAsync(coordinator);

        var shutdown = coordinator.DisposeAsync().AsTask();
        var allInvocations = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allInvocations, Task.Delay(TimeSpan.FromSeconds(7)));
        await shutdown;

        Assert.Same(allInvocations, completed);
        Assert.True(tasks.All(task => task.IsCompleted));
        Assert.True(tasks.Select(task => task.Result.Status).All(status =>
            status is ApiInvocationStatus.HostUnavailable or ApiInvocationStatus.CallerCanceled or ApiInvocationStatus.Success));
        Assert.True(coordinator.IsDisposed);
    }

    [Fact]
    public async Task Stress_ManyShortInvocationsKeepCallerSpecificResults()
    {
        await using var coordinator = await CreateCoordinatorAsync(CreateRuntime(maxConcurrency: 4, queueLimit: 100), ["Invoke-Phase4Delay"]);

        var tasks = Enumerable.Range(0, 60)
            .Select(index => coordinator.InvokeAsync(CreateDelayRequest($"stress-{index}", 20), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(results, AssertInvocationSuccess);
        foreach (var index in Enumerable.Range(0, results.Length))
        {
            var normalized = (Dictionary<string, object?>)NormalizeSuccess(results[index].Output)!;
            Assert.Equal($"stress-{index}", normalized["RequestId"]);
        }

        Assert.True(coordinator.CreateMetricsSnapshot().MaxObservedActiveInvocationCount <= 4);
    }

    [Fact]
    public async Task CancellationStress_MixedTimeoutCancelAndSuccessLeavesEngineUsable()
    {
        await using var coordinator = await CreateCoordinatorAsync(CreateRuntime(maxConcurrency: 4, queueLimit: 16), ["Invoke-Phase4Delay", "Get-SystemInfo"]);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        var tasks = new[]
        {
            coordinator.InvokeAsync(CreateDelayRequest("short-1", 20), CancellationToken.None),
            coordinator.InvokeAsync(CreateDelayRequest("timeout-1", 500, TimeSpan.FromMilliseconds(75)), CancellationToken.None),
            coordinator.InvokeAsync(CreateDelayRequest("cancel-1", 500), callerCancellation.Token),
            coordinator.InvokeAsync(CreateDelayRequest("short-2", 20), CancellationToken.None)
        };

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        var recovery = await coordinator.InvokeAsync(CreateSystemInfoRequest("SERVER-MIXED-RECOVERY"), CancellationToken.None);

        Assert.Contains(results, result => result.Status == ApiInvocationStatus.Success);
        Assert.Contains(results, result => result.Status == ApiInvocationStatus.InvocationTimedOut);
        Assert.Contains(results, result => result.Status == ApiInvocationStatus.CallerCanceled);
        Assert.Equal(ApiInvocationStatus.Success, recovery.Status);
        Assert.Equal(0, coordinator.CreateMetricsSnapshot().ActiveInvocationCount);
    }

    private static Task<RunningRestApiProofHost> StartHostAsync()
        => RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions { Url = "http://127.0.0.1:0" });

    private static async Task<PowerShellInvocationCoordinator> CreateCoordinatorAsync(
        ApiRuntimeOptions? runtimeOptions = null,
        string[]? allowedFunctions = null)
    {
        var poolManager = new RunspacePoolManager();
        var invoker = new PowerShellFunctionInvoker();
        var coordinator = new PowerShellInvocationCoordinator(poolManager, invoker);
        await coordinator.InitializeAsync(
            ResolveProofScriptPath(),
            allowedFunctions ?? ["Get-SystemInfo", "Invoke-TestFailure", "Get-Numbers", "Get-ExplicitNull", "Invoke-Phase4Delay", "Invoke-Phase5FormattingOutput", "Invoke-Phase5BStreams", "Invoke-Phase5BNonTerminatingError", "Invoke-Phase5BValidation", "Invoke-Phase5BOversizedOutput"],
            runtimeOptions ?? CreateRuntime(),
            CancellationToken.None);
        return coordinator;
    }

    private static ApiRuntimeOptions CreateRuntime(
        int maxConcurrency = 4,
        int queueLimit = 32,
        TimeSpan? queueWaitTimeout = null,
        TimeSpan? defaultTimeout = null,
        int responseItemLimit = 1000,
        int responseByteLimit = 5 * 1024 * 1024,
        int serializationDepth = 8,
        int retainedStreamEntries = 100)
        => new()
        {
            RunspacePoolMinimum = 1,
            RunspacePoolMaximum = maxConcurrency,
            MaximumConcurrentExecutions = maxConcurrency,
            QueueLimit = queueLimit,
            QueueWaitTimeout = queueWaitTimeout ?? TimeSpan.FromSeconds(10),
            DefaultInvocationTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30),
            ResponseItemLimit = responseItemLimit,
            ResponseByteLimit = responseByteLimit,
            SerializationDepth = serializationDepth,
            MaximumRetainedStreamEntries = retainedStreamEntries
        };

    private static ApiParameterBindingConfiguration CreateBinding(
        string powerShellParameterName,
        string externalName,
        string typeName,
        ApiRequiredBehavior required)
        => new()
        {
            PowerShellParameterName = powerShellParameterName,
            Source = ApiParameterSource.Body,
            Name = externalName,
            Required = required,
            TypeName = typeName
        };

    private static NormalizedApiResult NormalizeResult(IReadOnlyList<PSObject> output, ApiRuntimeOptions? runtimeOptions = null)
        => PowerShellResultNormalizer.Shared.Normalize(
            output,
            runtimeOptions ?? CreateRuntime(),
            RestApiProofHostFactory.JsonOptions);

    private static object? NormalizeSuccess(IReadOnlyList<PSObject> output, ApiRuntimeOptions? runtimeOptions = null)
    {
        var result = NormalizeResult(output, runtimeOptions);
        Assert.True(result.IsSuccess, $"{result.FailureKind}: {result.SafeMessage}");
        return result.Value;
    }

    private static ApiInvocationRequest CreateSystemInfoRequest(string computerName)
        => new()
        {
            FunctionName = "Get-SystemInfo",
            Parameters = new Dictionary<string, object?> { ["ComputerName"] = computerName }
        };

    private static ApiInvocationRequest CreateDelayRequest(string requestId, int milliseconds, TimeSpan? timeout = null)
        => new()
        {
            FunctionName = "Invoke-Phase4Delay",
            Timeout = timeout,
            Parameters = new Dictionary<string, object?>
            {
                ["RequestId"] = requestId,
                ["Milliseconds"] = milliseconds
            }
        };

    private static void AssertInvocationSuccess(ApiInvocationResult result)
        => Assert.True(
            result.Status == ApiInvocationStatus.Success,
            $"Expected Success but got {result.Status}: {string.Join(" | ", result.Streams.Select(stream => $"{stream.StreamName}:{stream.Message}"))}");

    private static async Task WaitForActiveInvocationAsync(PowerShellInvocationCoordinator coordinator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (coordinator.CreateMetricsSnapshot().ActiveInvocationCount > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for an active invocation.");
    }

    private static string ResolveProofScriptPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost", "Scripts", "TestApi.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate the proof host test script.");
    }

    private static async Task<JsonDocument> ReadSuccessJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private enum NormalizerTestEnum
    {
        First,
        Second
    }

    private sealed class NormalizerNode
    {
        public NormalizerNode(string name, NormalizerNode? child = null)
        {
            Name = name;
            Child = child;
        }

        public string Name { get; }
        public NormalizerNode? Child { get; }
    }

    private sealed class ThrowingPropertyObject
    {
        public string Stable => "stable";

        public string Broken => throw new InvalidOperationException("getter boom");
    }
}
