using System.Text.Json;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ApiPublishConfigurationTests
{
    private readonly ApiPublishConfigurationValidator _validator = new();
    private readonly PowerShellApiMetadataService _metadataService = new();

    [Fact]
    public void DefaultConfiguration_UsesSchemaVersionRestAndDeterministicResourceDefaults()
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(@"C:\Scripts\Inventory.ps1");

        Assert.Equal(1, configuration.SchemaVersion);
        Assert.Equal("Inventory.ps1", configuration.SourceScript);
        Assert.Equal(ApiTransport.Rest, configuration.Transport);
        Assert.Equal("Inventory", configuration.Api.Title);
        Assert.Equal("/api", configuration.Api.DefaultRoutePrefix);
        Assert.Equal(1, configuration.Runtime.RunspacePoolMinimum);
        Assert.Equal(4, configuration.Runtime.RunspacePoolMaximum);
        Assert.Equal(4, configuration.Runtime.MaximumConcurrentExecutions);
        Assert.Equal(32, configuration.Runtime.QueueLimit);
        Assert.Equal(TimeSpan.FromSeconds(10), configuration.Runtime.QueueWaitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.Runtime.DefaultInvocationTimeout);
        Assert.Equal(1024 * 1024, configuration.Runtime.RequestBodySizeLimitBytes);
        Assert.Equal(64 * 1024, configuration.Runtime.WebSocketMessageSizeLimitBytes);
        Assert.Equal(1000, configuration.Runtime.ResponseItemLimit);
        Assert.Equal(5 * 1024 * 1024, configuration.Runtime.ResponseByteLimit);
        Assert.Equal(8, configuration.Runtime.SerializationDepth);
        Assert.Equal(100, configuration.Runtime.MaximumRetainedStreamEntries);
    }

    [Theory]
    [InlineData(ApiTransport.WebSocket)]
    [InlineData(ApiTransport.ServerSentEvents)]
    public void StreamingTransports_AreAcceptedBySharedEndpointValidation(ApiTransport transport)
    {
        var configuration = ValidConfiguration();
        configuration.Transport = transport;

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EndpointIdentityAndOptions_RoundTripThroughReadableJson()
    {
        var configuration = ValidConfiguration();
        var endpoint = configuration.Endpoints[0];
        endpoint.EndpointId = "stable-system-info";
        endpoint.Transport = ApiTransport.WebSocket;
        endpoint.DisplayName = "System Info";
        endpoint.TimeoutOverride = TimeSpan.FromSeconds(12);
        endpoint.RequiresAuthentication = false;
        endpoint.AuthorizationPolicy = "FuturePolicy";
        endpoint.OpenApiTags().Add("Inventory");
        configuration.OpenApi.EnableSwaggerUiForPublishedApi = true;

        var json = JsonSerializer.Serialize(configuration, ApiPublishConfigurationStore.CreateSerializerOptions());
        var roundTripped = JsonSerializer.Deserialize<ApiPublishConfiguration>(json, ApiPublishConfigurationStore.CreateSerializerOptions());

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"transport\": \"Rest\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(roundTripped);
        Assert.Equal("stable-system-info", roundTripped!.Endpoints[0].EndpointId);
        Assert.Equal(ApiTransport.WebSocket, roundTripped.Endpoints[0].Transport);
        Assert.Equal(TimeSpan.FromSeconds(12), roundTripped.Endpoints[0].TimeoutOverride);
        Assert.False(roundTripped.Endpoints[0].RequiresAuthentication);
        Assert.True(roundTripped.OpenApi.EnableSwaggerUiForPublishedApi);
    }

    [Fact]
    public void EndpointTransport_FallsBackToRootTransportForLegacyConfigurations()
    {
        var configuration = ValidConfiguration();
        configuration.Transport = ApiTransport.ServerSentEvents;

        Assert.Null(configuration.Endpoints[0].Transport);
        Assert.Equal(ApiTransport.ServerSentEvents, ApiTransportFacts.ResolveEndpointTransport(configuration, configuration.Endpoints[0]));
    }

    [Fact]
    public void EnabledAndDisabledEndpoints_AreModeledAndOnlyEnabledDuplicatesAreRejected()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Get-SystemInfo", ApiHttpMethod.Get, "/api/systeminfo")
            .WithId("disabled-copy")
            .WithBinding("ComputerName", ApiParameterSource.Query, "computerName")
            .Disabled());

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.True(result.IsValid);
        Assert.False(configuration.Endpoints[1].IsEnabled);
    }

    [Fact]
    public void WebSocketAndSseDoNotNeedProtocolSpecificSettings()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints[0].Transport = ApiTransport.WebSocket;

        Assert.NotNull(configuration.Endpoints[0].Rest);
        Assert.True(_validator.Validate(configuration, MetadataForBasicFunction()).IsValid);

        configuration.Endpoints[0].Transport = ApiTransport.ServerSentEvents;

        Assert.True(_validator.Validate(configuration, MetadataForBasicFunction()).IsValid);
    }

    [Fact]
    public void StreamingEndpoints_RejectBindingsThatCannotBeRepresentedByTheirTransport()
    {
        var sseBody = ValidConfiguration();
        sseBody.Endpoints[0].Transport = ApiTransport.ServerSentEvents;
        sseBody.Endpoints[0].ParameterBindings[0].Source = ApiParameterSource.Body;

        var sseRoute = ConfigurationWithRoute("/api/{computerName}");
        sseRoute.Endpoints[0].Transport = ApiTransport.ServerSentEvents;

        var webSocketRoute = ConfigurationWithRoute("/api/{computerName}");
        webSocketRoute.Endpoints[0].Transport = ApiTransport.WebSocket;

        Assert.Contains(_validator.Validate(sseBody, MetadataForBasicFunction()).Errors, error => error.Code == "API078");
        Assert.Contains(_validator.Validate(sseRoute, MetadataForBasicFunction()).Errors, error => error.Code == "API077");
        Assert.Contains(_validator.Validate(webSocketRoute, MetadataForBasicFunction()).Errors, error => error.Code == "API077");
    }

    [Fact]
    public void ValidRoutes_AcceptStaticAndParameterizedTemplates()
    {
        Assert.True(_validator.Validate(ConfigurationWithRoute("/api/systeminfo"), MetadataForBasicFunction()).IsValid);
        Assert.True(_validator.Validate(ConfigurationWithRoute("/api/computers/{computerName}"), MetadataForBasicFunction()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/api/{computerName}/{computerName}")]
    [InlineData("/api/{computerName")]
    [InlineData("/api/computers}")]
    [InlineData("/api/{bad-name}")]
    public void InvalidRoutes_AreRejected(string route)
    {
        var result = _validator.Validate(ConfigurationWithRoute(route), MetadataForBasicFunction());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code.StartsWith("API11", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateMethodAndNormalizedRoute_IsRejected()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Get-SystemInfo", ApiHttpMethod.Get, "api/systeminfo/")
            .WithId("other-id"));

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "API061");
    }

    [Fact]
    public void SameRouteWithDifferentAllowedMethod_IsAccepted()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Get-SystemInfo", ApiHttpMethod.Post, "/api/systeminfo")
            .WithId("post-id")
            .WithBinding("ComputerName", ApiParameterSource.Body, "computerName"));

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RouteTokensAndRouteBindings_MustCorrespond()
    {
        var missingBinding = ConfigurationWithRoute("/api/{computerName}");
        missingBinding.Endpoints[0].ParameterBindings.Clear();
        var extraBinding = ValidConfiguration();
        extraBinding.Endpoints[0].ParameterBindings[0].Source = ApiParameterSource.Route;
        extraBinding.Endpoints[0].ParameterBindings[0].Name = "computerName";
        extraBinding.Endpoints[0].Rest.RouteTemplate = "/api/systeminfo";

        Assert.Contains(_validator.Validate(missingBinding, MetadataForBasicFunction()).Errors, error => error.Code == "API076");
        Assert.Contains(_validator.Validate(extraBinding, MetadataForBasicFunction()).Errors, error => error.Code == "API073");
    }

    [Theory]
    [InlineData("function Get-SystemInfo { param([string]$ComputerName) }", "Get-SystemInfo", true)]
    [InlineData("function Get-SystemInfo { function Inner { param([string]$ComputerName) } }", "Inner", false)]
    [InlineData("filter Get-SystemInfo { param([string]$ComputerName) }", "Get-SystemInfo", false)]
    public void FunctionPublishability_IsValidatedFromPhase1Metadata(string script, string functionName, bool expectedValid)
    {
        var configuration = ValidConfiguration(functionName);
        var result = _validator.Validate(configuration, _metadataService.Analyze(script));

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(result.Errors, error => error.Code == "API092");
        }
    }

    [Fact]
    public void MissingFunctionUnknownParameterAndDuplicateParameterBinding_AreRejected()
    {
        var missingFunction = ValidConfiguration("Missing-Function");
        var unknownParameter = ValidConfiguration();
        unknownParameter.Endpoints[0].ParameterBindings[0].PowerShellParameterName = "Missing";
        var duplicateParameter = ValidConfiguration();
        duplicateParameter.Endpoints[0].ParameterBindings.Add(new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "ComputerName",
            Source = ApiParameterSource.Header,
            Name = "X-Computer"
        });

        Assert.Contains(_validator.Validate(missingFunction, MetadataForBasicFunction()).Errors, error => error.Code == "API091");
        Assert.Contains(_validator.Validate(unknownParameter, MetadataForBasicFunction()).Errors, error => error.Code == "API093");
        Assert.Contains(_validator.Validate(duplicateParameter, MetadataForBasicFunction()).Errors, error => error.Code == "API071");
    }

    [Fact]
    public void MandatoryParameter_RequiresBinding()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints[0].ParameterBindings.Clear();

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "API095");
    }

    [Fact]
    public void SupportedTypes_AreAcceptedAndUnsupportedTypes_AreRejected()
    {
        const string supportedScript = """
            function Invoke-Types {
              param(
                [string]$Name,
                [int]$Count,
                [long]$LongValue,
                [decimal]$Money,
                [double]$Ratio,
                [bool]$Enabled,
                [datetime]$At,
                [datetimeoffset]$Offset,
                [guid]$Id,
                [ConsoleColor]$Color,
                [switch]$Force,
                [string[]]$Tags,
                [Nullable[int]]$Maybe,
                [hashtable]$Bag,
                [pscustomobject]$Object
              )
            }
            """;
        var supported = ApiEndpointConfiguration.CreateRest("Invoke-Types", ApiHttpMethod.Post, "/api/types");
        foreach (var name in new[] { "Name", "Count", "LongValue", "Money", "Ratio", "Enabled", "At", "Offset", "Id", "Color", "Force", "Tags", "Maybe", "Bag", "Object" })
        {
            supported.ParameterBindings.Add(new ApiParameterBindingConfiguration { PowerShellParameterName = name, Source = ApiParameterSource.Body, Name = name });
        }

        var supportedConfiguration = ValidConfiguration("Invoke-Types");
        supportedConfiguration.Endpoints[0] = supported;
        var unsupportedConfiguration = ValidConfiguration("Invoke-Unsupported");
        unsupportedConfiguration.Endpoints[0].ParameterBindings[0].PowerShellParameterName = "File";

        Assert.True(_validator.Validate(supportedConfiguration, _metadataService.Analyze(supportedScript)).IsValid);
        Assert.Contains(_validator.Validate(unsupportedConfiguration, _metadataService.Analyze("function Invoke-Unsupported { param([System.IO.FileInfo]$File) }")).Errors, error => error.Code == "API101");
    }

    [Fact]
    public void IncompleteMetadata_ProducesWarning()
    {
        var configuration = ValidConfiguration();
        var metadata = _metadataService.Analyze("function Get-SystemInfo { param([Parameter(Mandatory=$script:Required)][string]$ComputerName) }");

        var result = _validator.Validate(configuration, metadata);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, warning => warning.Code == "API094");
    }

    [Theory]
    [InlineData(ApiParameterSource.Query)]
    [InlineData(ApiParameterSource.Route)]
    [InlineData(ApiParameterSource.Header)]
    [InlineData(ApiParameterSource.ServerDefined)]
    public void GetEndpoint_AllowsNonBodyBindingSources(ApiParameterSource source)
    {
        var configuration = ValidConfiguration();
        var binding = configuration.Endpoints[0].ParameterBindings[0];
        binding.Source = source;
        binding.Name = source == ApiParameterSource.Route ? "computerName" : "computerName";
        if (source == ApiParameterSource.Route)
        {
            configuration.Endpoints[0].Rest.RouteTemplate = "/api/{computerName}";
        }
        else if (source == ApiParameterSource.ServerDefined)
        {
            binding.Name = string.Empty;
            binding.ServerValue = new ApiServerDefinedValue
            {
                Kind = ApiServerDefinedValueKind.EnvironmentVariable,
                Value = "COMPUTERNAME"
            };
        }

        Assert.True(_validator.Validate(configuration, MetadataForBasicFunction()).IsValid);
    }

    [Fact]
    public void GetEndpoint_RejectsBodyBinding()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints[0].ParameterBindings[0].Source = ApiParameterSource.Body;

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "API074");
    }

    [Fact]
    public void PostEndpoint_AllowsMultipleBodyBindingsButRejectsDuplicateBodyProperty()
    {
        const string script = "function New-Thing { param([Parameter(Mandatory)][string]$FirstName, [int]$Count) }";
        var configuration = ValidConfiguration("New-Thing", ApiHttpMethod.Post);
        configuration.Endpoints[0].ParameterBindings =
        [
            new() { PowerShellParameterName = "FirstName", Source = ApiParameterSource.Body, Name = "firstName" },
            new() { PowerShellParameterName = "Count", Source = ApiParameterSource.Body, Name = "count" }
        ];

        Assert.True(_validator.Validate(configuration, _metadataService.Analyze(script)).IsValid);

        configuration.Endpoints[0].ParameterBindings[1].Name = "firstName";
        var duplicate = _validator.Validate(configuration, _metadataService.Analyze(script));

        Assert.False(duplicate.IsValid);
        Assert.Contains(duplicate.Errors, error => error.Code == "API075");
    }

    [Fact]
    public void PostEndpoint_AllowsRouteAndBodyCombination()
    {
        const string script = "function Set-Computer { param([Parameter(Mandatory)][string]$ComputerName, [Parameter(Mandatory)][pscustomobject]$Patch) }";
        var configuration = ValidConfiguration("Set-Computer", ApiHttpMethod.Post);
        configuration.Endpoints[0].Rest.RouteTemplate = "/api/computers/{computerName}";
        configuration.Endpoints[0].ParameterBindings =
        [
            new() { PowerShellParameterName = "ComputerName", Source = ApiParameterSource.Route, Name = "computerName" },
            new() { PowerShellParameterName = "Patch", Source = ApiParameterSource.Body, Name = "patch" }
        ];

        Assert.True(_validator.Validate(configuration, _metadataService.Analyze(script)).IsValid);
    }

    [Fact]
    public void SecretSensitiveLiteralServerValue_IsRejected()
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints[0].ParameterBindings[0] = new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "ComputerName",
            Source = ApiParameterSource.ServerDefined,
            IsSecretSensitive = true,
            ServerValue = new ApiServerDefinedValue { Kind = ApiServerDefinedValueKind.Literal, Value = "SECRET_SENTINEL_123" }
        };

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "API081");
    }

    [Fact]
    public void ApiKeySecretValue_IsNotRepresentedInDurableModel()
    {
        var configuration = ValidConfiguration();
        configuration.Security.ApiKeyEnvironmentVariableName = "PS7API_TEST_KEY";
        var json = JsonSerializer.Serialize(configuration, ApiPublishConfigurationStore.CreateSerializerOptions());

        Assert.Contains("PS7API_TEST_KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_SENTINEL_123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKeyValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResourceValidation_RejectsUnsafeLimits()
    {
        var configuration = ValidConfiguration();
        configuration.Runtime.RunspacePoolMinimum = 0;
        configuration.Runtime.RunspacePoolMaximum = -1;
        configuration.Runtime.MaximumConcurrentExecutions = 5;
        configuration.Runtime.QueueLimit = -1;
        configuration.Runtime.QueueWaitTimeout = TimeSpan.Zero;
        configuration.Runtime.DefaultInvocationTimeout = TimeSpan.Zero;
        configuration.Runtime.RequestBodySizeLimitBytes = 0;
        configuration.Runtime.WebSocketMessageSizeLimitBytes = 0;
        configuration.Runtime.ResponseItemLimit = 0;
        configuration.Runtime.ResponseByteLimit = 0;
        configuration.Runtime.SerializationDepth = 99;

        var result = _validator.Validate(configuration, MetadataForBasicFunction());

        Assert.Contains(result.Errors, error => error.Code == "API020");
        Assert.Contains(result.Errors, error => error.Code == "API021");
        Assert.Contains(result.Errors, error => error.Code == "API024");
        Assert.Contains(result.Errors, error => error.Code == "API025");
        Assert.Contains(result.Errors, error => error.Code == "API026");
        Assert.Contains(result.Errors, error => error.Code == "API027");
        Assert.Contains(result.Errors, error => error.Code == "API028");
        Assert.Contains(result.Errors, error => error.Code == "API035");
        Assert.Contains(result.Errors, error => error.Code == "API029");
        Assert.Contains(result.Errors, error => error.Code == "API030");
        Assert.Contains(result.Errors, error => error.Code == "API031");
    }

    [Fact]
    public void SecurityValidation_RequiresExplicitLocalNoAuthAndRejectsDeferredModes()
    {
        var localNoAuth = ValidConfiguration();
        localNoAuth.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        var jwt = ValidConfiguration();
        jwt.Security.Mode = ApiSecurityMode.JwtBearer;

        Assert.Contains(_validator.Validate(localNoAuth, MetadataForBasicFunction()).Errors, error => error.Code == "API040");
        localNoAuth.Security.AllowNoAuthenticationForLocalTest = true;
        Assert.True(_validator.Validate(localNoAuth, MetadataForBasicFunction()).IsValid);
        Assert.Contains(_validator.Validate(jwt, MetadataForBasicFunction()).Errors, error => error.Code == "API042");
    }

    private ApiMetadataResult MetadataForBasicFunction()
        => _metadataService.Analyze("function Get-SystemInfo { param([Parameter(Mandatory)][string]$ComputerName) }");

    private static ApiPublishConfiguration ValidConfiguration(string functionName = "Get-SystemInfo", ApiHttpMethod method = ApiHttpMethod.Get)
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(@"C:\Scripts\Inventory.ps1");
        var endpoint = ApiEndpointConfiguration.CreateRest(functionName, method, "/api/systeminfo");
        endpoint.ParameterBindings.Add(new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "ComputerName",
            Source = method == ApiHttpMethod.Get ? ApiParameterSource.Query : ApiParameterSource.Body,
            Name = "computerName"
        });
        configuration.Endpoints.Add(endpoint);
        return configuration;
    }

    private static ApiPublishConfiguration ConfigurationWithRoute(string route)
    {
        var configuration = ValidConfiguration();
        configuration.Endpoints[0].Rest.RouteTemplate = route;
        if (route.Contains("{computerName}", StringComparison.OrdinalIgnoreCase))
        {
            configuration.Endpoints[0].ParameterBindings[0].Source = ApiParameterSource.Route;
            configuration.Endpoints[0].ParameterBindings[0].Name = "computerName";
        }

        return configuration;
    }
}

internal static class ApiEndpointConfigurationTestExtensions
{
    public static ApiEndpointConfiguration WithId(this ApiEndpointConfiguration endpoint, string endpointId)
    {
        endpoint.EndpointId = endpointId;
        return endpoint;
    }

    public static ApiEndpointConfiguration Disabled(this ApiEndpointConfiguration endpoint)
    {
        endpoint.IsEnabled = false;
        return endpoint;
    }

    public static ApiEndpointConfiguration WithBinding(
        this ApiEndpointConfiguration endpoint,
        string parameterName,
        ApiParameterSource source,
        string externalName)
    {
        endpoint.ParameterBindings.Add(new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = parameterName,
            Source = source,
            Name = externalName
        });
        return endpoint;
    }

    public static List<string> OpenApiTags(this ApiEndpointConfiguration endpoint) => endpoint.Rest.Tags;
}
