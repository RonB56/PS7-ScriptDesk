using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.Hosting;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiOpenApiTests
{
    [Fact]
    public async Task OpenApiJson_IncludesConfiguredEndpointsAndExcludesUnconfiguredFunctions()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(OpenApiEndpointMapper.OpenApiJsonRoute);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("3.0.3", json.RootElement.GetProperty("openapi").GetString());
        AssertPathMethod(json, "/api/systeminfo", "get", "getSystemInfo");
        AssertPathMethod(json, "/api/systeminfo", "post", "postSystemInfo");
        AssertPathMethod(json, "/api/phase6/computers/{computerName}", "get", "phase6GetComputer");
        AssertPathMethod(json, "/api/phase6/computers/{computerName}", "post", "phase6SetComputer");
        Assert.False(json.RootElement.GetProperty("paths").TryGetProperty("/api/failure", out _));
        Assert.DoesNotContain("Invoke-Phase6UnconfiguredSecret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("this function must not appear", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PowerShellFunctionName", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".ps1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rbarn", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiJson_DescribesPathQueryPrimitiveAndValidationSchemas()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var json = await ReadOpenApiAsync(client);

        var getOperation = GetOperation(json, "/api/phase6/computers/{computerName}", "get");
        var computerName = FindParameter(getOperation, "computerName", "path");
        var view = FindParameter(getOperation, "view", "query");
        var limit = FindParameter(getOperation, "limit", "query");

        Assert.True(computerName.GetProperty("required").GetBoolean());
        Assert.Equal("string", computerName.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(view.GetProperty("required").GetBoolean());
        Assert.Equal(["Detail", "Summary"], view.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray());
        Assert.False(limit.GetProperty("required").GetBoolean());
        Assert.Equal("integer", limit.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("int32", limit.GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal(1, limit.GetProperty("schema").GetProperty("minimum").GetDecimal());
        Assert.Equal(100, limit.GetProperty("schema").GetProperty("maximum").GetDecimal());
    }

    [Fact]
    public async Task OpenApiJson_DescribesRequestBodySchemaFromActualBodyBindings()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var json = await ReadOpenApiAsync(client);

        var postOperation = GetOperation(json, "/api/phase6/computers/{computerName}", "post");
        var requestBody = postOperation.GetProperty("requestBody");
        var schema = requestBody
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        var properties = schema.GetProperty("properties");

        Assert.True(requestBody.GetProperty("required").GetBoolean());
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Contains("displayName", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("string", properties.GetProperty("displayName").GetProperty("type").GetString());
        Assert.Equal(1, properties.GetProperty("displayName").GetProperty("minLength").GetInt32());
        Assert.Equal(50, properties.GetProperty("displayName").GetProperty("maxLength").GetInt32());
        Assert.Equal("boolean", properties.GetProperty("enabled").GetProperty("type").GetString());
        Assert.DoesNotContain("enabled", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task OpenApiJson_DocumentsDynamicSuccessAndPhase5BProblemDetailsResponses()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();
        using var json = await ReadOpenApiAsync(client);

        var operation = GetOperation(json, "/api/phase6/computers/{computerName}", "get");
        var responses = operation.GetProperty("responses");
        var successSchema = responses
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        var problemRef = responses
            .GetProperty("400")
            .GetProperty("content")
            .GetProperty("application/problem+json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        var problemSchema = json.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProblemDetails");

        Assert.True(successSchema.GetProperty("nullable").GetBoolean());
        Assert.Contains("Dynamic normalized PowerShell JSON result.", successSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("#/components/schemas/ProblemDetails", problemRef);
        foreach (var status in new[] { "400", "429", "500", "503", "504" })
        {
            Assert.True(responses.TryGetProperty(status, out _), $"Missing documented response {status}.");
        }

        Assert.False(responses.TryGetProperty("401", out _));
        Assert.False(responses.TryGetProperty("403", out _));
        Assert.Contains("requestId", problemSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.True(problemSchema.GetProperty("properties").TryGetProperty("failureKind", out _));
    }

    [Fact]
    public async Task SwaggerUi_IsAvailableForLocalProofHost()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(OpenApiEndpointMapper.SwaggerRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Offline OpenAPI viewer", body, StringComparison.Ordinal);
        Assert.Contains(OpenApiEndpointMapper.OpenApiJsonRoute, body, StringComparison.Ordinal);
        Assert.Contains("fetch", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://unpkg.com", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Phase6UnconfiguredSecret", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SwaggerExposureConfiguration_FollowsExistingOpenApiSettings()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath("TestApi.ps1");
        configuration.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        configuration.Security.AllowNoAuthenticationForLocalTest = true;

        configuration.OpenApi.IsEnabled = false;
        Assert.False(OpenApiEndpointMapper.ShouldExposeSwaggerUi(configuration));

        configuration.OpenApi.IsEnabled = true;
        configuration.OpenApi.EnableSwaggerUiForLocalTest = false;
        Assert.False(OpenApiEndpointMapper.ShouldExposeSwaggerUi(configuration));

        configuration.OpenApi.EnableSwaggerUiForLocalTest = true;
        Assert.True(OpenApiEndpointMapper.ShouldExposeSwaggerUi(configuration));

        configuration.Security.Mode = ApiSecurityMode.ApiKey;
        configuration.OpenApi.EnableSwaggerUiForPublishedApi = false;
        Assert.False(OpenApiEndpointMapper.ShouldExposeSwaggerUi(configuration));

        configuration.OpenApi.EnableSwaggerUiForPublishedApi = true;
        Assert.True(OpenApiEndpointMapper.ShouldExposeSwaggerUi(configuration));
    }

    [Fact]
    public async Task OpenApiJson_IsDeterministicForSameConfiguration()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        var first = await client.GetStringAsync(OpenApiEndpointMapper.OpenApiJsonRoute);
        var second = await client.GetStringAsync(OpenApiEndpointMapper.OpenApiJsonRoute);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ExistingAndPhase6ApiRequests_StillUseConfiguredEndpointExecution()
    {
        await using var host = await StartHostAsync();
        using var client = host.CreateClient();

        using var existing = await client.GetAsync("/api/systeminfo?computerName=SERVER01");
        using var phase6Get = await client.GetAsync("/api/phase6/computers/SERVER06?view=Detail&limit=5");
        using var phase6Post = await client.PostAsJsonAsync(
            "/api/phase6/computers/SERVER07",
            new { displayName = "Server Seven", enabled = false });
        using var existingJson = await ReadSuccessJsonAsync(existing);
        using var phase6GetJson = await ReadSuccessJsonAsync(phase6Get);
        using var phase6PostJson = await ReadSuccessJsonAsync(phase6Post);

        Assert.Equal("SERVER01", existingJson.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal("SERVER06", phase6GetJson.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal("Detail", phase6GetJson.RootElement.GetProperty("View").GetString());
        Assert.Equal(5, phase6GetJson.RootElement.GetProperty("Limit").GetInt32());
        Assert.Equal("SERVER07", phase6PostJson.RootElement.GetProperty("ComputerName").GetString());
        Assert.Equal("Server Seven", phase6PostJson.RootElement.GetProperty("DisplayName").GetString());
        Assert.False(phase6PostJson.RootElement.GetProperty("Enabled").GetBoolean());
    }

    private static Task<RunningRestApiProofHost> StartHostAsync()
        => RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions { Url = "http://127.0.0.1:0" });

    private static async Task<JsonDocument> ReadOpenApiAsync(HttpClient client)
    {
        using var response = await client.GetAsync(OpenApiEndpointMapper.OpenApiJsonRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> ReadSuccessJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement GetOperation(JsonDocument document, string path, string method)
        => document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method);

    private static void AssertPathMethod(JsonDocument document, string path, string method, string operationId)
        => Assert.Equal(operationId, GetOperation(document, path, method).GetProperty("operationId").GetString());

    private static JsonElement FindParameter(JsonElement operation, string name, string source)
    {
        foreach (var parameter in operation.GetProperty("parameters").EnumerateArray())
        {
            if (parameter.GetProperty("name").GetString() == name &&
                parameter.GetProperty("in").GetString() == source)
            {
                return parameter;
            }
        }

        throw new InvalidOperationException($"Parameter '{source}:{name}' was not found.");
    }
}
