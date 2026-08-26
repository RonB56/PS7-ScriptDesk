using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.Hosting;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiSecurityHardeningTests : IDisposable
{
    private const string ApiKeyValue = "PHASE11_API_KEY_SECRET_VALUE";
    private const string ServerSecretValue = "PHASE11_SERVER_DEFINED_SECRET_VALUE";
    private const string ProblemSecretValue = "PHASE11_PROBLEM_SECRET_VALUE";
    private readonly string _apiKeyVariableName = $"PS7API_PHASE11_KEY_{Guid.NewGuid():N}";
    private readonly string _serverSecretVariableName = $"PS7API_PHASE11_SERVER_{Guid.NewGuid():N}";
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk Api Security {Guid.NewGuid():N}");

    public RestApiSecurityHardeningTests()
    {
        Directory.CreateDirectory(_testDirectory);
        Environment.SetEnvironmentVariable(_apiKeyVariableName, ApiKeyValue);
        Environment.SetEnvironmentVariable(_serverSecretVariableName, ServerSecretValue);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_apiKeyVariableName, null);
        Environment.SetEnvironmentVariable(_serverSecretVariableName, null);
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
    public async Task ApiKeyAuthentication_ValidKeySucceeds()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = CreateAuthenticatedClient(host);

        using var response = await client.GetAsync("/api/secure/echo?name=AUTHORIZED");
        using var json = await ReadSuccessJsonAsync(response);

        Assert.Equal("AUTHORIZED", json.RootElement.GetProperty("Name").GetString());
        Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("Time").GetString(), out _));
    }

    [Fact]
    public async Task ApiKeyAuthentication_MissingAndInvalidKeysReturn401()
    {
        await using var host = await StartSecurityHostAsync();
        using var missingClient = host.CreateClient();
        using var invalidClient = host.CreateClient();
        invalidClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationService.ApiKeyHeaderName, "wrong-key");

        using var missing = await missingClient.GetAsync("/api/secure/echo?name=NOPE");
        using var invalid = await invalidClient.GetAsync("/api/secure/echo?name=NOPE");
        var missingBody = await missing.Content.ReadAsStringAsync();
        var invalidBody = await invalid.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Contains("WWW-Authenticate", string.Join(";", missing.Headers.Select(header => header.Key)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiKeyValue, missingBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyValue, invalidBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKeySecret_DoesNotAppearInOpenApiProblemDetailsOrDiagnosticsPreview()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = CreateAuthenticatedClient(host);

        using var openApi = await client.GetAsync(OpenApiEndpointMapper.OpenApiJsonRoute);
        var openApiText = await openApi.Content.ReadAsStringAsync();
        using var failure = await client.GetAsync("/api/secure/failure");
        var problemText = await failure.Content.ReadAsStringAsync();
        var diagnosticPreview = DeveloperDiagnostics.SanitizePreview(
            $"X-API-Key: {ApiKeyValue}{Environment.NewLine}Authorization: Bearer {ApiKeyValue}{Environment.NewLine}apiKey={ApiKeyValue}");

        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        Assert.Contains(ApiKeyAuthenticationService.ApiKeySecuritySchemeName, openApiText, StringComparison.Ordinal);
        Assert.Contains(ApiKeyAuthenticationService.ApiKeyHeaderName, openApiText, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyValue, openApiText, StringComparison.Ordinal);
        Assert.DoesNotContain(_apiKeyVariableName, openApiText, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.DoesNotContain(ApiKeyValue, problemText, StringComparison.Ordinal);
        Assert.DoesNotContain(ProblemSecretValue, problemText, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Sensitive", problemText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiKeyValue, diagnosticPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer " + ApiKeyValue, diagnosticPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteExecutionBoundary_RemainsEndpointAllowlisted()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = CreateAuthenticatedClient(host);

        using var builtIn = await client.GetAsync("/api/get-date");
        using var unconfigured = await client.GetAsync("/api/unconfigured-secret");
        using var selectionAttempt = await client.GetAsync("/api/secure/echo?name=DATA&function=Get-Date&command=Get-Process");
        using var selectionJson = await ReadSuccessJsonAsync(selectionAttempt);

        Assert.Equal(HttpStatusCode.NotFound, builtIn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unconfigured.StatusCode);
        Assert.Equal("DATA", selectionJson.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task ServerDefinedParameters_CannotBeOverriddenByRequestBody()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = CreateAuthenticatedClient(host);

        using var response = await client.PostAsJsonAsync(
            "/api/secure/server",
            new { clientValue = "client", serverSecret = "REQUEST_OVERRIDE_SECRET" });
        using var json = await ReadSuccessJsonAsync(response);
        var body = json.RootElement.GetRawText();

        Assert.Equal("client", json.RootElement.GetProperty("ClientValue").GetString());
        Assert.Equal(ServerSecretValue, json.RootElement.GetProperty("ServerSecret").GetString());
        Assert.DoesNotContain("REQUEST_OVERRIDE_SECRET", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedInput_IsRejectedSafelyWhenAuthenticated()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = CreateAuthenticatedClient(host);

        using var badType = await client.GetAsync("/api/secure/int?value=not-an-int");
        using var badJson = await client.PostAsync("/api/secure/server", new StringContent("{", Encoding.UTF8, "application/json"));
        var badTypeBody = await badType.Content.ReadAsStringAsync();
        var badJsonBody = await badJson.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.DoesNotContain(ApiKeyValue, badTypeBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyValue, badJsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestBodySizeLimit_RemainsEffectiveWhenAuthenticated()
    {
        await using var host = await StartSecurityHostAsync(runtime =>
        {
            runtime.RequestBodySizeLimitBytes = 256;
        });
        using var client = CreateAuthenticatedClient(host);
        var oversizedJson = JsonSerializer.Serialize(new { payload = new string('x', 1024) });

        using var response = await client.PostAsync("/api/secure/body", new StringContent(oversizedJson, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Request body too large.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyValue, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueTimeoutAndOutputLimits_RemainEffectiveWhenAuthenticated()
    {
        await using var host = await StartSecurityHostAsync(runtime =>
        {
            runtime.RunspacePoolMaximum = 1;
            runtime.MaximumConcurrentExecutions = 1;
            runtime.QueueLimit = 0;
            runtime.QueueWaitTimeout = TimeSpan.FromMilliseconds(100);
            runtime.ResponseByteLimit = 256;
        });
        using var client = CreateAuthenticatedClient(host);

        var first = client.GetAsync("/api/secure/slow?milliseconds=700");
        await Task.Delay(150);
        using var rejected = await client.GetAsync("/api/secure/slow?milliseconds=10");
        using var completed = await first;
        using var outputLimit = await client.GetAsync("/api/secure/outputlimit");
        var outputLimitBody = await outputLimit.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, outputLimit.StatusCode);
        Assert.Contains("output limit", outputLimitBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiKeyValue, outputLimitBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonTerminatingPowerShellFailure_RemainsSanitizedWhenAuthenticated()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = CreateAuthenticatedClient(host);

        using var response = await client.GetAsync("/api/secure/nonterminating");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("powershell-non-terminating-error", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyValue, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ProblemSecretValue, body, StringComparison.Ordinal);
        Assert.DoesNotContain("partial-output", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiAuthentication_MissingKeyReturns401WithoutExposingSecret()
    {
        await using var host = await StartSecurityHostAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(OpenApiEndpointMapper.OpenApiJsonRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(ApiKeyValue, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_apiKeyVariableName, body, StringComparison.Ordinal);
    }

    private async Task<RunningRestApiProofHost> StartSecurityHostAsync(Action<ApiRuntimeOptions>? configureRuntime = null)
    {
        var contentRoot = CreateSecurityContentRoot(configureRuntime);
        return await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions
        {
            Url = "http://127.0.0.1:0",
            ContentRootPath = contentRoot,
            ConfigurationRelativePath = Path.Combine("Config", "api.ps7api.json")
        });
    }

    private HttpClient CreateAuthenticatedClient(RunningRestApiProofHost host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationService.ApiKeyHeaderName, ApiKeyValue);
        return client;
    }

    private string CreateSecurityContentRoot(Action<ApiRuntimeOptions>? configureRuntime)
    {
        var root = Path.Combine(_testDirectory, Guid.NewGuid().ToString("N"));
        var scriptDirectory = Path.Combine(root, "Scripts");
        var configDirectory = Path.Combine(root, "Config");
        Directory.CreateDirectory(scriptDirectory);
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(scriptDirectory, "SecurityApi.ps1"), SecurityScriptSource, new UTF8Encoding(false));
        var configuration = CreateSecurityConfiguration();
        configureRuntime?.Invoke(configuration.Runtime);
        File.WriteAllText(
            Path.Combine(configDirectory, "api.ps7api.json"),
            JsonSerializer.Serialize(configuration, RestApiProofHostFactory.JsonOptions),
            new UTF8Encoding(false));
        return root;
    }

    private ApiPublishConfiguration CreateSecurityConfiguration()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath("SecurityApi.ps1");
        configuration.Api.Title = "Phase 11 Security API";
        configuration.OpenApi.Title = "Phase 11 Security API";
        configuration.Security.Mode = ApiSecurityMode.ApiKey;
        configuration.Security.ApiKeyEnvironmentVariableName = _apiKeyVariableName;
        configuration.OpenApi.EnableSwaggerUiForPublishedApi = true;
        configuration.OpenApi.RequireAuthenticationForPublishedSwagger = true;
        configuration.Runtime.RunspacePoolMinimum = 1;
        configuration.Runtime.RunspacePoolMaximum = 2;
        configuration.Runtime.MaximumConcurrentExecutions = 2;
        configuration.Runtime.QueueLimit = 2;
        configuration.Runtime.DefaultInvocationTimeout = TimeSpan.FromSeconds(10);
        configuration.Runtime.ResponseByteLimit = 1024 * 1024;
        configuration.Endpoints =
        [
            GetEndpoint("phase11-echo", "Get-SecureEcho", "/api/secure/echo", "name", "Name", "string"),
            GetEndpoint("phase11-int", "Get-SecureInt", "/api/secure/int", "value", "Value", "int"),
            GetEndpoint("phase11-slow", "Invoke-SecureSlow", "/api/secure/slow", "milliseconds", "Milliseconds", "int"),
            GetEndpoint("phase11-failure", "Invoke-SecureFailure", "/api/secure/failure"),
            GetEndpoint("phase11-nonterminating", "Invoke-SecureNonTerminating", "/api/secure/nonterminating"),
            GetEndpoint("phase11-outputlimit", "Get-SecureOversizedOutput", "/api/secure/outputlimit"),
            new ApiEndpointConfiguration
            {
                EndpointId = "phase11-server-defined",
                PowerShellFunctionName = "Get-SecureServerDefined",
                DisplayName = "Secure server-defined parameter",
                RequiresAuthentication = true,
                Rest =
                {
                    Method = ApiHttpMethod.Post,
                    RouteTemplate = "/api/secure/server",
                    OperationId = "phase11ServerDefined",
                    IncludeInOpenApi = true
                },
                ParameterBindings =
                [
                    new()
                    {
                        PowerShellParameterName = "ClientValue",
                        Source = ApiParameterSource.Body,
                        Name = "clientValue",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    },
                    new()
                    {
                        PowerShellParameterName = "ServerSecret",
                        Source = ApiParameterSource.ServerDefined,
                        Required = ApiRequiredBehavior.Required,
                        ServerValue = new ApiServerDefinedValue
                        {
                            Kind = ApiServerDefinedValueKind.EnvironmentVariable,
                            Value = _serverSecretVariableName
                        },
                        IsSecretSensitive = true,
                        TypeName = "string"
                    }
                ]
            },
            new ApiEndpointConfiguration
            {
                EndpointId = "phase11-body",
                PowerShellFunctionName = "Get-SecureBody",
                DisplayName = "Secure body",
                RequiresAuthentication = true,
                Rest =
                {
                    Method = ApiHttpMethod.Post,
                    RouteTemplate = "/api/secure/body",
                    OperationId = "phase11Body",
                    IncludeInOpenApi = false
                },
                ParameterBindings =
                [
                    new()
                    {
                        PowerShellParameterName = "Payload",
                        Source = ApiParameterSource.Body,
                        Name = "payload",
                        Required = ApiRequiredBehavior.Required,
                        TypeName = "string"
                    }
                ]
            }
        ];

        return configuration;
    }

    private static ApiEndpointConfiguration GetEndpoint(
        string endpointId,
        string functionName,
        string route,
        string? externalName = null,
        string? parameterName = null,
        string? typeName = null)
    {
        var endpoint = ApiEndpointConfiguration.CreateRest(functionName, ApiHttpMethod.Get, route);
        endpoint.EndpointId = endpointId;
        endpoint.DisplayName = functionName;
        endpoint.RequiresAuthentication = true;
        endpoint.Rest.OperationId = functionName.Replace("-", string.Empty, StringComparison.Ordinal);
        endpoint.Rest.IncludeInOpenApi = true;
        if (!string.IsNullOrWhiteSpace(externalName) &&
            !string.IsNullOrWhiteSpace(parameterName) &&
            !string.IsNullOrWhiteSpace(typeName))
        {
            endpoint.ParameterBindings.Add(new ApiParameterBindingConfiguration
            {
                PowerShellParameterName = parameterName,
                Source = ApiParameterSource.Query,
                Name = externalName,
                Required = ApiRequiredBehavior.Required,
                TypeName = typeName
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

    private const string SecurityScriptSource = """
function Get-SecureEcho {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    [PSCustomObject]@{
        Name = $Name
        Time = Get-Date
    }
}

function Get-SecureInt {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Value)

    [PSCustomObject]@{
        Value = $Value
    }
}

function Get-SecureBody {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Payload)

    [PSCustomObject]@{
        Length = $Payload.Length
    }
}

function Get-SecureServerDefined {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ClientValue,
        [Parameter(Mandatory)][string]$ServerSecret
    )

    [PSCustomObject]@{
        ClientValue = $ClientValue
        ServerSecret = $ServerSecret
    }
}

function Invoke-SecureSlow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Milliseconds)

    Start-Sleep -Milliseconds $Milliseconds
    [PSCustomObject]@{
        Slept = $Milliseconds
    }
}

function Get-SecureOversizedOutput {
    [CmdletBinding()]
    param()

    1..40 | ForEach-Object {
        [PSCustomObject]@{
            Index = $_
            Value = ('x' * 80)
        }
    }
}

function Invoke-SecureFailure {
    [CmdletBinding()]
    param()

    throw 'PHASE11_PROBLEM_SECRET_VALUE C:\Sensitive\Phase11.ps1'
}

function Invoke-SecureNonTerminating {
    [CmdletBinding()]
    param()

    $exception = [System.InvalidOperationException]::new('PHASE11_PROBLEM_SECRET_VALUE C:\Sensitive\Phase11.ps1')
    $record = [System.Management.Automation.ErrorRecord]::new(
        $exception,
        'Phase11NonTerminating',
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

    'not public'
}
""";
}
