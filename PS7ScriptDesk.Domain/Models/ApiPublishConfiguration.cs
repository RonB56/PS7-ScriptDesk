using System;
using System.Collections.Generic;
using System.IO;

namespace PS7ScriptDesk.Domain.Models;

public enum ApiTransport
{
    Rest,
    WebSocket,
    ServerSentEvents
}

public enum ApiHttpMethod
{
    Get,
    Post,
    Put,
    Patch,
    Delete
}

public enum ApiParameterSource
{
    Route,
    Query,
    Body,
    Header,
    ServerDefined
}

public enum ApiRequiredBehavior
{
    InheritFromPowerShell,
    Required,
    Optional
}

public enum ApiArrayBindingBehavior
{
    RepeatedValues,
    CommaSeparated,
    JsonArray
}

public enum ApiSecurityMode
{
    LocalTestNoAuthentication,
    ApiKey,
    JwtBearer,
    WindowsAuthentication
}

public enum ApiServerDefinedValueKind
{
    Literal,
    EnvironmentVariable,
    CorrelationId,
    AuthenticatedPrincipalName
}

public enum ApiNoOutputBehavior
{
    JsonNull,
    NoContent
}

public sealed class ApiPublishConfiguration
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string SourceScript { get; set; } = string.Empty;
    public ApiTransport Transport { get; set; } = ApiTransport.Rest;
    public ApiDefinitionMetadata Api { get; set; } = new();
    public List<ApiEndpointConfiguration> Endpoints { get; set; } = new();
    public ApiRuntimeOptions Runtime { get; set; } = ApiRuntimeOptions.CreateDefault();
    public ApiSecurityConfiguration Security { get; set; } = new();
    public ApiOpenApiConfiguration OpenApi { get; set; } = new();
    public ApiPublishOutputOptions Output { get; set; } = new();

    public static ApiPublishConfiguration CreateDefaultForScriptPath(string? sourceScriptPath)
    {
        var fileName = string.IsNullOrWhiteSpace(sourceScriptPath)
            ? string.Empty
            : Path.GetFileName(sourceScriptPath);
        var title = string.IsNullOrWhiteSpace(fileName)
            ? "PowerShell API"
            : Path.GetFileNameWithoutExtension(fileName);

        return new ApiPublishConfiguration
        {
            SourceScript = fileName,
            Api =
            {
                Title = string.IsNullOrWhiteSpace(title) ? "PowerShell API" : title,
                DefaultRoutePrefix = "/api"
            },
            OpenApi =
            {
                Title = string.IsNullOrWhiteSpace(title) ? "PowerShell API" : title
            }
        };
    }
}

public sealed class ApiDefinitionMetadata
{
    public string Title { get; set; } = "PowerShell API";
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string ContactName { get; set; } = string.Empty;
    public string ContactUrl { get; set; } = string.Empty;
    public string ProjectUrl { get; set; } = string.Empty;
    public string DefaultRoutePrefix { get; set; } = "/api";
}

public sealed class ApiEndpointConfiguration
{
    public string EndpointId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string PowerShellFunctionName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ApiRestEndpointOptions Rest { get; set; } = new();
    public List<ApiParameterBindingConfiguration> ParameterBindings { get; set; } = new();
    public TimeSpan? TimeoutOverride { get; set; }
    public bool RequiresAuthentication { get; set; } = true;
    public string AuthorizationPolicy { get; set; } = string.Empty;
    public ApiResponseBehavior Response { get; set; } = new();

    public static ApiEndpointConfiguration CreateRest(
        string powerShellFunctionName,
        ApiHttpMethod method,
        string routeTemplate)
    {
        var normalizedName = string.IsNullOrWhiteSpace(powerShellFunctionName)
            ? "endpoint"
            : powerShellFunctionName.Trim();

        return new ApiEndpointConfiguration
        {
            EndpointId = CreateStableEndpointId(normalizedName),
            PowerShellFunctionName = normalizedName,
            DisplayName = normalizedName,
            Rest =
            {
                Method = method,
                RouteTemplate = routeTemplate,
                OperationId = normalizedName
            }
        };
    }

    public static string CreateStableEndpointId(string functionName)
    {
        var value = string.IsNullOrWhiteSpace(functionName) ? "endpoint" : functionName.Trim();
        return $"ps-{value.Replace('_', '-').ToLowerInvariant()}";
    }
}

public sealed class ApiRestEndpointOptions
{
    public ApiHttpMethod Method { get; set; } = ApiHttpMethod.Get;
    public string RouteTemplate { get; set; } = string.Empty;
    public string ConsumesContentType { get; set; } = "application/json";
    public string ProducesContentType { get; set; } = "application/json";
    public int SuccessStatusCode { get; set; } = 200;
    public string OperationId { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public bool IncludeInOpenApi { get; set; } = true;
}

public sealed class ApiParameterBindingConfiguration
{
    public string PowerShellParameterName { get; set; } = string.Empty;
    public ApiParameterSource Source { get; set; } = ApiParameterSource.Query;
    public string Name { get; set; } = string.Empty;
    public ApiRequiredBehavior Required { get; set; } = ApiRequiredBehavior.InheritFromPowerShell;
    public ApiServerDefinedValue? ServerValue { get; set; }
    public bool IsSecretSensitive { get; set; }
    public ApiArrayBindingBehavior ArrayBinding { get; set; } = ApiArrayBindingBehavior.RepeatedValues;
    public string TypeName { get; set; } = string.Empty;
}

public sealed class ApiServerDefinedValue
{
    public ApiServerDefinedValueKind Kind { get; set; } = ApiServerDefinedValueKind.Literal;
    public string Value { get; set; } = string.Empty;
}

public sealed class ApiSecurityConfiguration
{
    public ApiSecurityMode Mode { get; set; } = ApiSecurityMode.ApiKey;
    public bool AllowNoAuthenticationForLocalTest { get; set; }
    public string ApiKeyEnvironmentVariableName { get; set; } = "PS7API_API_KEY";
    public string JwtAuthority { get; set; } = string.Empty;
    public string JwtAudience { get; set; } = string.Empty;
}

public sealed class ApiRuntimeOptions
{
    public int RunspacePoolMinimum { get; set; } = 1;
    public int RunspacePoolMaximum { get; set; } = 4;
    public int MaximumConcurrentExecutions { get; set; } = 4;
    public int QueueLimit { get; set; } = 32;
    public TimeSpan QueueWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultInvocationTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int RequestBodySizeLimitBytes { get; set; } = 1 * 1024 * 1024;
    public int ResponseItemLimit { get; set; } = 1000;
    public int ResponseByteLimit { get; set; } = 5 * 1024 * 1024;
    public int SerializationDepth { get; set; } = 8;
    public int MaximumRetainedStreamEntries { get; set; } = 100;

    public static ApiRuntimeOptions CreateDefault() => new();
}

public sealed class ApiOpenApiConfiguration
{
    public bool IsEnabled { get; set; } = true;
    public string Title { get; set; } = "PowerShell API";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public bool EnableSwaggerUiForLocalTest { get; set; } = true;
    public bool EnableSwaggerUiForPublishedApi { get; set; }
    public bool RequireAuthenticationForPublishedSwagger { get; set; } = true;
}

public sealed class ApiResponseBehavior
{
    public bool ReturnBareJsonResult { get; set; } = true;
    public ApiNoOutputBehavior NoOutputBehavior { get; set; } = ApiNoOutputBehavior.JsonNull;
    public bool IncludeWarningStreamInLocalTest { get; set; } = true;
    public bool TreatNonTerminatingErrorsAsFailure { get; set; } = true;
}

public sealed class ApiPublishOutputOptions
{
    public string OutputDirectory { get; set; } = string.Empty;
    public bool PreserveGeneratedProject { get; set; }
}

public sealed class ApiPublishValidationResult
{
    public List<ApiPublishValidationDiagnostic> Errors { get; } = new();
    public List<ApiPublishValidationDiagnostic> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;

    public void AddError(string code, string message, string? path = null, string? endpointId = null, string? parameterName = null)
        => Errors.Add(new ApiPublishValidationDiagnostic(code, message, path, endpointId, parameterName));

    public void AddWarning(string code, string message, string? path = null, string? endpointId = null, string? parameterName = null)
        => Warnings.Add(new ApiPublishValidationDiagnostic(code, message, path, endpointId, parameterName));
}

public sealed record ApiPublishValidationDiagnostic(
    string Code,
    string Message,
    string? Path = null,
    string? EndpointId = null,
    string? ParameterName = null);
