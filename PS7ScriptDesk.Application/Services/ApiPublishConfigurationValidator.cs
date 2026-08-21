using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class ApiPublishConfigurationValidator : IApiPublishConfigurationValidator
{
    private static readonly Regex RouteParameterNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SupportedScalarTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string",
        "System.String",
        "int",
        "int32",
        "System.Int32",
        "long",
        "int64",
        "System.Int64",
        "decimal",
        "System.Decimal",
        "double",
        "System.Double",
        "bool",
        "boolean",
        "System.Boolean",
        "datetime",
        "System.DateTime",
        "datetimeoffset",
        "System.DateTimeOffset",
        "guid",
        "System.Guid",
        "switch",
        "System.Management.Automation.SwitchParameter",
        "hashtable",
        "System.Collections.Hashtable",
        "pscustomobject",
        "System.Management.Automation.PSObject",
        "System.Management.Automation.PSCustomObject",
        "ConsoleColor",
        "System.ConsoleColor"
    };

    private static readonly HashSet<string> UnsupportedKnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "securestring",
        "System.Security.SecureString",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.Stream",
        "System.Diagnostics.Process",
        "scriptblock",
        "System.Management.Automation.ScriptBlock"
    };

    public ApiPublishValidationResult Validate(ApiPublishConfiguration configuration, ApiMetadataResult? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var result = new ApiPublishValidationResult();

        ValidateRoot(configuration, result);
        ValidateRuntime(configuration.Runtime, result);
        ValidateSecurity(configuration.Security, result);

        if (configuration.Transport == ApiTransport.Rest)
        {
            ValidateRestEndpoints(configuration, metadata, result);
        }

        return result;
    }

    private static void ValidateRoot(ApiPublishConfiguration configuration, ApiPublishValidationResult result)
    {
        if (configuration.SchemaVersion <= 0)
        {
            result.AddError("API001", "The API publish configuration is missing a valid schema version.", "$.schemaVersion");
        }
        else if (configuration.SchemaVersion != ApiPublishConfiguration.CurrentSchemaVersion)
        {
            result.AddError("API002", $"Schema version {configuration.SchemaVersion.ToString(CultureInfo.InvariantCulture)} is not supported by this version of PS7 ScriptDesk.", "$.schemaVersion");
        }

        if (configuration.Transport is ApiTransport.WebSocket or ApiTransport.ServerSentEvents)
        {
            result.AddError("API003", $"{configuration.Transport} transport is reserved for a future phase and is not supported by REST V1.", "$.transport");
        }

        if (configuration.Transport != ApiTransport.Rest)
        {
            result.AddError("API004", "Only REST transport can be validated for Phase 2.", "$.transport");
        }

        if (string.IsNullOrWhiteSpace(configuration.SourceScript))
        {
            result.AddWarning("API005", "The configuration does not identify a source script. Unsaved scripts cannot be persisted as companion API files.", "$.sourceScript");
        }

        if (string.IsNullOrWhiteSpace(configuration.Api.Title))
        {
            result.AddError("API006", "API title is required.", "$.api.title");
        }
    }

    private static void ValidateRuntime(ApiRuntimeOptions runtime, ApiPublishValidationResult result)
    {
        if (runtime.RunspacePoolMinimum <= 0)
            result.AddError("API020", "Runspace pool minimum must be greater than zero.", "$.runtime.runspacePoolMinimum");
        if (runtime.RunspacePoolMaximum <= 0)
            result.AddError("API021", "Runspace pool maximum must be greater than zero.", "$.runtime.runspacePoolMaximum");
        if (runtime.RunspacePoolMaximum < runtime.RunspacePoolMinimum)
            result.AddError("API022", "Runspace pool maximum must be greater than or equal to the minimum.", "$.runtime.runspacePoolMaximum");
        if (runtime.MaximumConcurrentExecutions <= 0)
            result.AddError("API023", "Maximum concurrent executions must be greater than zero.", "$.runtime.maximumConcurrentExecutions");
        if (runtime.MaximumConcurrentExecutions > runtime.RunspacePoolMaximum)
            result.AddError("API024", "Maximum concurrent executions must not exceed the runspace pool maximum in REST V1.", "$.runtime.maximumConcurrentExecutions");
        if (runtime.QueueLimit < 0)
            result.AddError("API025", "Queue limit cannot be negative.", "$.runtime.queueLimit");
        if (runtime.QueueWaitTimeout <= TimeSpan.Zero)
            result.AddError("API026", "Queue wait timeout must be greater than zero.", "$.runtime.queueWaitTimeout");
        if (runtime.DefaultInvocationTimeout <= TimeSpan.Zero)
            result.AddError("API027", "Default invocation timeout must be greater than zero.", "$.runtime.defaultInvocationTimeout");
        if (runtime.RequestBodySizeLimitBytes <= 0)
            result.AddError("API028", "Request body size limit must be greater than zero.", "$.runtime.requestBodySizeLimitBytes");
        if (runtime.ResponseItemLimit <= 0)
            result.AddError("API029", "Response item limit must be greater than zero.", "$.runtime.responseItemLimit");
        if (runtime.ResponseByteLimit <= 0)
            result.AddError("API030", "Response byte limit must be greater than zero.", "$.runtime.responseByteLimit");
        if (runtime.SerializationDepth is <= 0 or > 32)
            result.AddError("API031", "Serialization depth must be between 1 and 32 for REST V1.", "$.runtime.serializationDepth");
        if (runtime.MaximumRetainedStreamEntries < 0)
            result.AddError("API032", "Maximum retained stream entries cannot be negative.", "$.runtime.maximumRetainedStreamEntries");

        if (runtime.RequestBodySizeLimitBytes > 10 * 1024 * 1024)
            result.AddWarning("API033", "Request body size limit is unusually high for a generated PowerShell API.", "$.runtime.requestBodySizeLimitBytes");
        if (runtime.DefaultInvocationTimeout > TimeSpan.FromMinutes(5))
            result.AddWarning("API034", "Default invocation timeout is unusually long for REST V1.", "$.runtime.defaultInvocationTimeout");
    }

    private static void ValidateSecurity(ApiSecurityConfiguration security, ApiPublishValidationResult result)
    {
        switch (security.Mode)
        {
            case ApiSecurityMode.LocalTestNoAuthentication:
                if (!security.AllowNoAuthenticationForLocalTest)
                {
                    result.AddError("API040", "No-auth mode is permitted only when explicitly marked as local-test behavior.", "$.security.allowNoAuthenticationForLocalTest");
                }

                break;
            case ApiSecurityMode.ApiKey:
                if (string.IsNullOrWhiteSpace(security.ApiKeyEnvironmentVariableName))
                {
                    result.AddError("API041", "API key authentication requires an environment-variable name, not a plaintext key.", "$.security.apiKeyEnvironmentVariableName");
                }

                break;
            case ApiSecurityMode.JwtBearer:
            case ApiSecurityMode.WindowsAuthentication:
                result.AddError("API042", $"{security.Mode} authentication is reserved for a future phase and is not supported by REST V1.", "$.security.mode");
                break;
            default:
                result.AddError("API043", "Unknown authentication mode.", "$.security.mode");
                break;
        }
    }

    private static void ValidateRestEndpoints(ApiPublishConfiguration configuration, ApiMetadataResult? metadata, ApiPublishValidationResult result)
    {
        var endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var methodRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var endpointIndex = 0; endpointIndex < configuration.Endpoints.Count; endpointIndex++)
        {
            var endpoint = configuration.Endpoints[endpointIndex];
            var endpointPath = $"$.endpoints[{endpointIndex}]";
            var endpointId = string.IsNullOrWhiteSpace(endpoint.EndpointId) ? null : endpoint.EndpointId;

            if (string.IsNullOrWhiteSpace(endpoint.EndpointId))
            {
                result.AddError("API050", "Endpoint ID is required and must remain stable across display-name or route changes.", $"{endpointPath}.endpointId");
            }
            else if (!endpointIds.Add(endpoint.EndpointId))
            {
                result.AddError("API051", "Endpoint ID values must be unique.", $"{endpointPath}.endpointId", endpoint.EndpointId);
            }

            if (string.IsNullOrWhiteSpace(endpoint.PowerShellFunctionName))
            {
                result.AddError("API052", "Endpoint must name a PowerShell function.", $"{endpointPath}.powerShellFunctionName", endpointId);
            }

            if (endpoint.TimeoutOverride.HasValue && endpoint.TimeoutOverride <= TimeSpan.Zero)
            {
                result.AddError("API053", "Endpoint timeout override must be greater than zero.", $"{endpointPath}.timeoutOverride", endpointId);
            }

            ValidateRestOptions(endpoint, endpointPath, endpointId, methodRoutes, result, out var routeTokens);
            ValidateBindings(endpoint, endpointPath, endpointId, routeTokens, result);
            ValidateFunctionMetadata(endpoint, endpointPath, endpointId, metadata, result);
        }
    }

    private static void ValidateRestOptions(
        ApiEndpointConfiguration endpoint,
        string endpointPath,
        string? endpointId,
        HashSet<string> methodRoutes,
        ApiPublishValidationResult result,
        out IReadOnlySet<string> routeTokens)
    {
        routeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (endpoint.Rest.Method is not (ApiHttpMethod.Get or ApiHttpMethod.Post))
        {
            result.AddError("API060", "REST V1 supports only GET and POST endpoints.", $"{endpointPath}.rest.method", endpointId);
        }

        var routeResult = ValidateRoute(endpoint.Rest.RouteTemplate);
        routeTokens = routeResult.Tokens;
        foreach (var diagnostic in routeResult.Errors)
        {
            result.AddError(diagnostic.Code, diagnostic.Message, $"{endpointPath}.rest.routeTemplate", endpointId);
        }

        if (routeResult.NormalizedRoute is not null && endpoint.IsEnabled)
        {
            var methodRouteKey = $"{endpoint.Rest.Method}:{routeResult.NormalizedRoute}";
            if (!methodRoutes.Add(methodRouteKey))
            {
                result.AddError("API061", "Duplicate enabled REST endpoints cannot use the same method and normalized route.", $"{endpointPath}.rest.routeTemplate", endpointId);
            }
        }

        if (endpoint.Rest.SuccessStatusCode is < 200 or > 299)
        {
            result.AddError("API062", "REST success status code must be a 2xx value.", $"{endpointPath}.rest.successStatusCode", endpointId);
        }
    }

    private static void ValidateBindings(
        ApiEndpointConfiguration endpoint,
        string endpointPath,
        string? endpointId,
        IReadOnlySet<string> routeTokens,
        ApiPublishValidationResult result)
    {
        var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var externalNamesBySource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routeBindingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var bindingIndex = 0; bindingIndex < endpoint.ParameterBindings.Count; bindingIndex++)
        {
            var binding = endpoint.ParameterBindings[bindingIndex];
            var bindingPath = $"{endpointPath}.parameterBindings[{bindingIndex}]";
            var parameterName = string.IsNullOrWhiteSpace(binding.PowerShellParameterName) ? null : binding.PowerShellParameterName;

            if (string.IsNullOrWhiteSpace(binding.PowerShellParameterName))
            {
                result.AddError("API070", "Parameter binding must name a PowerShell parameter.", $"{bindingPath}.powerShellParameterName", endpointId);
            }
            else if (!parameters.Add(binding.PowerShellParameterName))
            {
                result.AddError("API071", "Duplicate bindings for the same PowerShell parameter are not allowed.", $"{bindingPath}.powerShellParameterName", endpointId, binding.PowerShellParameterName);
            }

            if (binding.Source != ApiParameterSource.ServerDefined && string.IsNullOrWhiteSpace(binding.Name))
            {
                result.AddError("API072", "External parameter name is required for route, query, body, and header bindings.", $"{bindingPath}.name", endpointId, parameterName);
            }

            if (binding.Source == ApiParameterSource.Route)
            {
                if (!routeTokens.Contains(binding.Name))
                {
                    result.AddError("API073", "Route-bound parameter has no matching route token.", $"{bindingPath}.name", endpointId, parameterName);
                }

                routeBindingNames.Add(binding.Name);
            }

            if (endpoint.Rest.Method == ApiHttpMethod.Get && binding.Source == ApiParameterSource.Body)
            {
                result.AddError("API074", "GET endpoints cannot use JSON body parameter binding in REST V1.", $"{bindingPath}.source", endpointId, parameterName);
            }

            if (binding.Source == ApiParameterSource.ServerDefined)
            {
                ValidateServerDefinedValue(binding, bindingPath, endpointId, parameterName, result);
            }
            else
            {
                var externalKey = $"{binding.Source}:{binding.Name}";
                if (!externalNamesBySource.Add(externalKey))
                {
                    result.AddError("API075", "External parameter names must be unique within the same binding source.", $"{bindingPath}.name", endpointId, parameterName);
                }
            }
        }

        foreach (var routeToken in routeTokens.Where(routeToken => !routeBindingNames.Contains(routeToken)))
        {
            result.AddError("API076", $"Route token '{routeToken}' does not have a matching route parameter binding.", $"{endpointPath}.parameterBindings", endpointId, routeToken);
        }
    }

    private static void ValidateServerDefinedValue(
        ApiParameterBindingConfiguration binding,
        string bindingPath,
        string? endpointId,
        string? parameterName,
        ApiPublishValidationResult result)
    {
        if (binding.ServerValue is null)
        {
            result.AddError("API080", "Server-defined parameter binding requires a server value configuration.", $"{bindingPath}.serverValue", endpointId, parameterName);
            return;
        }

        if (binding.IsSecretSensitive && binding.ServerValue.Kind == ApiServerDefinedValueKind.Literal)
        {
            result.AddError("API081", "Secret-sensitive server-defined values cannot be persisted as plaintext literals. Use an environment-variable reference.", $"{bindingPath}.serverValue", endpointId, parameterName);
        }

        if (binding.ServerValue.Kind == ApiServerDefinedValueKind.EnvironmentVariable &&
            string.IsNullOrWhiteSpace(binding.ServerValue.Value))
        {
            result.AddError("API082", "Environment-variable server values require a variable name.", $"{bindingPath}.serverValue.value", endpointId, parameterName);
        }
    }

    private static void ValidateFunctionMetadata(
        ApiEndpointConfiguration endpoint,
        string endpointPath,
        string? endpointId,
        ApiMetadataResult? metadata,
        ApiPublishValidationResult result)
    {
        if (metadata is null)
        {
            result.AddWarning("API090", "PowerShell metadata was not supplied, so function and parameter bindings could not be fully validated.", endpointPath, endpointId);
            return;
        }

        var function = metadata.Functions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, endpoint.PowerShellFunctionName, StringComparison.OrdinalIgnoreCase));
        if (function is null)
        {
            result.AddError("API091", $"Function '{endpoint.PowerShellFunctionName}' was not found in static PowerShell metadata.", $"{endpointPath}.powerShellFunctionName", endpointId);
            return;
        }

        if (!function.IsPublishable)
        {
            result.AddError("API092", $"Function '{function.Name}' is not publishable in REST V1.", $"{endpointPath}.powerShellFunctionName", endpointId);
        }

        var metadataParameters = function.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in endpoint.ParameterBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.PowerShellParameterName))
            {
                continue;
            }

            if (!metadataParameters.TryGetValue(binding.PowerShellParameterName, out var parameter))
            {
                result.AddError("API093", $"Parameter '{binding.PowerShellParameterName}' does not exist on function '{function.Name}'.", $"{endpointPath}.parameterBindings", endpointId, binding.PowerShellParameterName);
                continue;
            }

            ValidateParameterType(parameter, endpointPath, endpointId, result);
        }

        foreach (var parameter in function.Parameters)
        {
            if (!parameter.IsMetadataComplete || parameter.MandatoryState == ApiParameterMandatoryState.Unknown)
            {
                result.AddWarning("API094", $"Parameter '{parameter.Name}' contains incomplete static metadata.", $"{endpointPath}.parameterBindings", endpointId, parameter.Name);
            }

            if (parameter.MandatoryState == ApiParameterMandatoryState.Mandatory &&
                !endpoint.ParameterBindings.Any(binding => string.Equals(binding.PowerShellParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                result.AddError("API095", $"Mandatory parameter '{parameter.Name}' requires a viable API binding.", $"{endpointPath}.parameterBindings", endpointId, parameter.Name);
            }
        }
    }

    private static void ValidateParameterType(
        ApiParameterMetadata parameter,
        string endpointPath,
        string? endpointId,
        ApiPublishValidationResult result)
    {
        var typeName = NormalizeTypeName(parameter.DeclaredTypeName);
        if (string.IsNullOrWhiteSpace(typeName))
        {
            result.AddWarning("API100", $"Parameter '{parameter.Name}' has no explicit type metadata and will be treated conservatively as a string-compatible value.", $"{endpointPath}.parameterBindings", endpointId, parameter.Name);
            return;
        }

        var scalarType = UnwrapType(typeName);
        if (UnsupportedKnownTypes.Contains(scalarType))
        {
            result.AddError("API101", $"Parameter '{parameter.Name}' uses unsupported REST V1 type '{parameter.DeclaredTypeName}'.", $"{endpointPath}.parameterBindings", endpointId, parameter.Name);
            return;
        }

        if (SupportedScalarTypes.Contains(scalarType))
        {
            return;
        }

        if (LooksLikeEnumType(scalarType))
        {
            result.AddWarning("API102", $"Parameter '{parameter.Name}' uses enum-like type '{parameter.DeclaredTypeName}'. REST V1 can represent enum values, but OpenAPI generation may need a later runtime metadata pass for the allowed names.", $"{endpointPath}.parameterBindings", endpointId, parameter.Name);
            return;
        }

        result.AddError("API103", $"Parameter '{parameter.Name}' uses unsupported REST V1 type '{parameter.DeclaredTypeName}'.", $"{endpointPath}.parameterBindings", endpointId, parameter.Name);
    }

    private static RouteValidationData ValidateRoute(string? route)
    {
        var errors = new List<ApiPublishValidationDiagnostic>();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(route))
        {
            errors.Add(new ApiPublishValidationDiagnostic("API110", "REST route template is required."));
            return new RouteValidationData(null, tokens, errors);
        }

        var normalized = NormalizeRoute(route);
        var depth = 0;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] == '{')
            {
                if (depth != 0)
                {
                    errors.Add(new ApiPublishValidationDiagnostic("API111", "REST route template contains nested or malformed braces."));
                    break;
                }

                var close = normalized.IndexOf('}', index + 1);
                if (close < 0)
                {
                    errors.Add(new ApiPublishValidationDiagnostic("API112", "REST route template has an opening brace without a closing brace."));
                    break;
                }

                var token = normalized[(index + 1)..close].Trim();
                if (!RouteParameterNamePattern.IsMatch(token))
                {
                    errors.Add(new ApiPublishValidationDiagnostic("API113", $"Route parameter name '{token}' is not valid."));
                }
                else if (!tokens.Add(token))
                {
                    errors.Add(new ApiPublishValidationDiagnostic("API114", $"Route parameter '{token}' is declared more than once."));
                }

                index = close;
                depth = 0;
                continue;
            }

            if (normalized[index] == '}')
            {
                errors.Add(new ApiPublishValidationDiagnostic("API115", "REST route template has a closing brace without an opening brace."));
                break;
            }
        }

        if (normalized.Contains("//", StringComparison.Ordinal))
        {
            errors.Add(new ApiPublishValidationDiagnostic("API116", "REST route template cannot contain empty path segments."));
        }

        return new RouteValidationData(normalized, tokens, errors);
    }

    private static string NormalizeRoute(string route)
    {
        var normalized = route.Trim().Replace('\\', '/');
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return normalized.ToLowerInvariant();
    }

    private static string NormalizeTypeName(string? typeName)
        => string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName.Trim();

    private static string UnwrapType(string typeName)
    {
        var unwrapped = typeName.Trim();
        if (unwrapped.EndsWith("[]", StringComparison.Ordinal))
        {
            unwrapped = unwrapped[..^2];
        }

        const string nullablePrefix = "System.Nullable[";
        if (unwrapped.StartsWith(nullablePrefix, StringComparison.OrdinalIgnoreCase) && unwrapped.EndsWith("]", StringComparison.Ordinal))
        {
            unwrapped = unwrapped[nullablePrefix.Length..^1];
        }
        else if (unwrapped.StartsWith("Nullable[", StringComparison.OrdinalIgnoreCase) && unwrapped.EndsWith("]", StringComparison.Ordinal))
        {
            unwrapped = unwrapped["Nullable[".Length..^1];
        }

        return unwrapped.Trim();
    }

    private static bool LooksLikeEnumType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName) || typeName.Contains('<', StringComparison.Ordinal))
        {
            return false;
        }

        if (typeName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return typeName.Split('.').All(part => part.Length > 0 && char.IsUpper(part[0]));
    }

    private sealed record RouteValidationData(
        string? NormalizedRoute,
        IReadOnlySet<string> Tokens,
        IReadOnlyList<ApiPublishValidationDiagnostic> Errors);
}
