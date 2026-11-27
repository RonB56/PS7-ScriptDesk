using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class ApiPublishConfigurationReconcilerTests
{
    private readonly ApiPublishConfigurationReconciler _reconciler = new();

    [Fact]
    public void Bug02T01_CompatibleSettingsSurviveFunctionBodyChangesAndMultipleRoutes()
    {
        var config = Configuration(ApiEndpoint("Get-Widget", "/custom/one"), ApiEndpoint("get-widget", "/custom/two"));
        config.Endpoints[0].IsEnabled = true;
        config.Endpoints[0].Rest.Method = ApiHttpMethod.Post;
        config.Endpoints[0].Rest.OperationId = "customOperation";
        config.Endpoints[0].RequiresAuthentication = false;
        config.Endpoints[0].AuthenticationRequirementIsDefaulted = false;
        config.Endpoints[0].ParameterBindings.Add(Binding("Name", "widgetName", "string"));
        config.Endpoints[1].ParameterBindings.Add(Binding("Name", "name", "System.String"));

        var result = _reconciler.Reconcile(config, Metadata(Function("GET-WIDGET", Parameter("Name", "string"))));

        Assert.Equal(2, result.Configuration.Endpoints.Count);
        Assert.Equal("/custom/one", result.Configuration.Endpoints[0].Rest.RouteTemplate);
        Assert.Equal("customOperation", result.Configuration.Endpoints[0].Rest.OperationId);
        Assert.True(result.Configuration.Endpoints[0].IsEnabled);
        Assert.False(result.Configuration.Endpoints[0].RequiresAuthentication);
        Assert.Equal("widgetName", result.Configuration.Endpoints[0].ParameterBindings[0].Name);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Bug02T02_NewFunctionIsAddedDisabledAndProtectedInApiKeyMode()
    {
        var config = Configuration(ApiEndpoint("Get-Existing", "/api/existing"));
        config.Security.Mode = ApiSecurityMode.ApiKey;

        var result = _reconciler.Reconcile(config, Metadata(Function("Get-Existing"), Function("Get-New")));

        var added = Assert.Single(result.Configuration.Endpoints, endpoint => endpoint.PowerShellFunctionName == "Get-New");
        Assert.False(added.IsEnabled);
        Assert.True(added.RequiresAuthentication);
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.NewFunction);
    }

    [Fact]
    public void Bug02T10_AuthenticationIntentPolicyProtectsDefaultDerivedEndpoints()
    {
        var existing = ApiEndpoint("Get-Existing", "/api/existing");
        existing.RequiresAuthentication = false;
        var config = Configuration(existing);
        config.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;

        var result = _reconciler.Reconcile(config, Metadata(Function("Get-Existing"), Function("Get-New")));
        var added = Assert.Single(result.Configuration.Endpoints, endpoint => endpoint.PowerShellFunctionName == "Get-New");
        result.Configuration.Security.Mode = ApiSecurityMode.ApiKey;
        ApiAuthenticationIntentPolicy.Apply(result.Configuration);
        Assert.True(added.RequiresAuthentication);
        Assert.True(existing.RequiresAuthentication); // unmarked legacy false is protected conservatively
    }

    [Fact]
    public void Bug02R1_AuthenticationDefaultIntentSurvivesConfigurationSerialization()
    {
        var config = Configuration();
        config.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        var added = Assert.Single(_reconciler.Reconcile(config, Metadata(Function("Get-New"))).Configuration.Endpoints);
        Assert.False(added.RequiresAuthentication);
        Assert.True(added.AuthenticationRequirementIsDefaulted);

        var serialized = System.Text.Json.JsonSerializer.Serialize(config);
        var reopened = System.Text.Json.JsonSerializer.Deserialize<ApiPublishConfiguration>(serialized)!;
        var reopenedEndpoint = Assert.Single(reopened.Endpoints);
        Assert.True(reopenedEndpoint.AuthenticationRequirementIsDefaulted);

        reopened.Security.Mode = ApiSecurityMode.ApiKey;
        ApiAuthenticationIntentPolicy.Apply(reopened);

        Assert.True(reopenedEndpoint.RequiresAuthentication);
    }

    [Fact]
    public void Bug02R1_InvalidServerDefinedMandatoryBindingIsReported()
    {
        var endpoint = ApiEndpoint("Get-Widget", "/api/widget");
        endpoint.ParameterBindings.Add(new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "Name",
            Source = ApiParameterSource.ServerDefined,
            Required = ApiRequiredBehavior.Optional,
            TypeName = "string"
        });
        var result = _reconciler.Reconcile(Configuration(endpoint), Metadata(Function("Get-Widget", Parameter("Name", "string", ApiParameterMandatoryState.Mandatory))));

        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.InvalidMandatoryBinding);
        Assert.True(result.RequiresExplicitResolution);
    }

    [Fact]
    public void Bug02R1_ValidatorRejectsStaleParameterTypeMapping()
    {
        var endpoint = ApiEndpoint("Get-Widget", "/api/widget");
        endpoint.ParameterBindings.Add(Binding("Count", "count", "string"));
        var config = Configuration(endpoint);
        var metadata = Metadata(Function("Get-Widget", Parameter("Count", "int")));

        var validation = new ApiPublishConfigurationValidator().Validate(config, metadata);

        Assert.Contains(validation.Errors, error => error.Code == "API104");
    }

    [Fact]
    public void Bug02T03_T04_RemovedAndRenamedFunctionsAreExcludedWithFindings()
    {
        var config = Configuration(ApiEndpoint("Get-Old", "/old"));

        var result = _reconciler.Reconcile(config, Metadata(Function("Get-Renamed")));

        Assert.DoesNotContain(result.Configuration.Endpoints, endpoint => endpoint.PowerShellFunctionName == "Get-Old");
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.RemovedFunction && finding.FunctionName == "Get-Old");
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.NewFunction && finding.FunctionName == "Get-Renamed");
        Assert.True(result.RequiresExplicitResolution);
    }

    [Fact]
    public void Bug02T05_T06_AddedParameterAppearsAndRemovedBindingIsExcluded()
    {
        var endpoint = ApiEndpoint("Get-Widget", "/api/widget");
        endpoint.ParameterBindings.Add(Binding("Old", "old", "string"));
        var config = Configuration(endpoint);

        var result = _reconciler.Reconcile(config, Metadata(Function("Get-Widget", Parameter("Name", "string"), Parameter("Count", "int"))));

        Assert.DoesNotContain(endpoint.ParameterBindings, binding => binding.PowerShellParameterName == "Old");
        Assert.Contains(endpoint.ParameterBindings, binding => binding.PowerShellParameterName == "Name");
        Assert.Contains(endpoint.ParameterBindings, binding => binding.PowerShellParameterName == "Count");
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.RemovedParameter && finding.ParameterName == "Old");
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.AddedParameter && finding.ParameterName == "Count");
    }

    [Fact]
    public void Bug02T07_T08_ChangedTypeAndMandatoryStateAreRefreshedAndReported()
    {
        var endpoint = ApiEndpoint("Get-Widget", "/api/widget");
        endpoint.ParameterBindings.Add(Binding("Count", "count", "string", ApiRequiredBehavior.Optional));
        var config = Configuration(endpoint);

        var result = _reconciler.Reconcile(config, Metadata(Function("Get-Widget", Parameter("Count", "int", ApiParameterMandatoryState.Mandatory))));

        var mapping = Assert.Single(endpoint.ParameterBindings);
        Assert.Equal("int", mapping.TypeName);
        Assert.Equal(ApiRequiredBehavior.Required, mapping.Required);
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.ChangedParameterType);
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.ChangedMandatoryState);
    }

    [Fact]
    public void Bug02_InvalidParseDoesNotMutateSavedInMemoryConfiguration()
    {
        var endpoint = ApiEndpoint("Get-Widget", "/custom/widget");
        endpoint.ParameterBindings.Add(Binding("Old", "old", "string"));
        var config = Configuration(endpoint);

        var result = _reconciler.Reconcile(config, new ApiMetadataResult(false, null,
            [new ApiSyntaxError("ParserError", "bad script", null)], [], []));

        Assert.Same(config, result.Configuration);
        Assert.Single(config.Endpoints);
        Assert.Equal("/custom/widget", config.Endpoints[0].Rest.RouteTemplate);
        Assert.Equal("Old", config.Endpoints[0].ParameterBindings[0].PowerShellParameterName);
        Assert.False(result.MetadataIsValid);
        Assert.True(result.RequiresExplicitResolution);
    }

    [Fact]
    public void Bug02_ReconciliationIsIdempotent()
    {
        var config = Configuration();
        var metadata = Metadata(Function("Get-One", Parameter("Name", "string")));

        _reconciler.Reconcile(config, metadata);
        var second = _reconciler.Reconcile(config, metadata);

        Assert.Single(config.Endpoints);
        Assert.Single(config.Endpoints[0].ParameterBindings);
        Assert.DoesNotContain(second.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.NewFunction);
    }

    [Fact]
    public void Bug02T14_ThreeObsoleteEndpointsReconcileToSixCurrentFunctions()
    {
        var config = Configuration(ApiEndpoint("Old-A", "/a"), ApiEndpoint("Old-B", "/b"), ApiEndpoint("Old-C", "/c"));
        config.Endpoints[0].IsEnabled = true;
        var functions = Enumerable.Range(1, 6).Select(index => Function($"Get-Current{index}")).ToArray();

        var result = _reconciler.Reconcile(config, Metadata(functions));

        Assert.Equal(6, result.Configuration.Endpoints.Count);
        Assert.All(result.Configuration.Endpoints, endpoint => Assert.False(endpoint.IsEnabled));
        Assert.Equal(3, result.Findings.Count(finding => finding.Kind == ApiPublishReconciliationFindingKind.RemovedFunction));
        Assert.Equal(6, result.Findings.Count(finding => finding.Kind == ApiPublishReconciliationFindingKind.NewFunction));
    }

    private static ApiPublishConfiguration Configuration(params ApiEndpointConfiguration[] endpoints)
    {
        var config = ApiPublishConfiguration.CreateDefaultForScriptPath("Widget.ps1");
        config.Security.Mode = ApiSecurityMode.ApiKey;
        config.Endpoints.AddRange(endpoints);
        return config;
    }

    private static ApiEndpointConfiguration ApiEndpoint(string functionName, string route)
        => ApiEndpointConfiguration.CreateRest(functionName, ApiHttpMethod.Get, route);

    private static ApiParameterBindingConfiguration Binding(
        string parameterName,
        string apiName,
        string typeName,
        ApiRequiredBehavior required = ApiRequiredBehavior.Required)
        => new()
        {
            PowerShellParameterName = parameterName,
            Name = apiName,
            Source = ApiParameterSource.Query,
            Required = required,
            TypeName = typeName
        };

    private static ApiMetadataResult Metadata(params ApiFunctionMetadata[] functions)
        => new(true, null, [], functions, []);

    private static ApiFunctionMetadata Function(string name, params ApiParameterMetadata[] parameters)
        => new(name, ApiFunctionConstructKind.Function, true, true, null, true, Extent(), parameters, null, [], []);

    private static ApiParameterMetadata Parameter(
        string name,
        string? type,
        ApiParameterMandatoryState mandatory = ApiParameterMandatoryState.NotMandatory)
        => new(name, type, type is not null, false, false, null, mandatory, null, [], [], Extent(), true, []);

    private static ApiSourceExtent Extent() => new(1, 1, 1, 1, 0, 1, string.Empty);
}
