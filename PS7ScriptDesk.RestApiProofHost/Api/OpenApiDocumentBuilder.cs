using System.Globalization;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public sealed class OpenApiDocumentBuilder
{
    public IReadOnlyDictionary<string, object?> Build(ApiPublishConfiguration configuration, ApiMetadataResult? metadata)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var paths = new Dictionary<string, object?>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var functions = metadata?.Functions.ToDictionary(function => function.Name, StringComparer.OrdinalIgnoreCase)
                        ?? new Dictionary<string, ApiFunctionMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in IncludedEndpoints(configuration))
        {
            functions.TryGetValue(endpoint.PowerShellFunctionName, out var function);
            var path = NormalizeRouteTemplate(endpoint.Rest.RouteTemplate);
            if (!paths.TryGetValue(path, out var pathItemObject) || pathItemObject is not Dictionary<string, object?> pathItem)
            {
                pathItem = new Dictionary<string, object?>(StringComparer.Ordinal);
                paths[path] = pathItem;
            }

            pathItem[endpoint.Rest.Method.ToString().ToLowerInvariant()] = BuildOperation(
                configuration,
                endpoint,
                function,
                operationIds);
        }

        var document = Object(
            ("openapi", "3.0.3"),
            ("info", BuildInfo(configuration)),
            ("paths", paths),
            ("components", BuildComponents(configuration)));

        return document;
    }

    private static IEnumerable<ApiEndpointConfiguration> IncludedEndpoints(ApiPublishConfiguration configuration)
        => configuration.Endpoints
            .Where(endpoint => endpoint.IsEnabled &&
                               ApiTransportFacts.ResolveEndpointTransport(configuration, endpoint) == ApiTransport.Rest &&
                               endpoint.Rest.IncludeInOpenApi &&
                               endpoint.Rest.Method is ApiHttpMethod.Get or ApiHttpMethod.Post)
            .OrderBy(endpoint => NormalizeRouteTemplate(endpoint.Rest.RouteTemplate), StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Rest.Method.ToString(), StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.EndpointId, StringComparer.Ordinal);

    private static Dictionary<string, object?> BuildInfo(ApiPublishConfiguration configuration)
        => Object(
            ("title", ChooseText(configuration.OpenApi.Title, configuration.Api.Title, "PowerShell API")),
            ("version", ChooseText(configuration.OpenApi.Version, configuration.Api.Version, "1.0.0")),
            ("description", ChooseText(configuration.OpenApi.Description, configuration.Api.Description, string.Empty)));

    private static Dictionary<string, object?> BuildOperation(
        ApiPublishConfiguration configuration,
        ApiEndpointConfiguration endpoint,
        ApiFunctionMetadata? function,
        HashSet<string> operationIds)
    {
        var parameters = endpoint.ParameterBindings
            .Where(binding => binding.Source is ApiParameterSource.Route or ApiParameterSource.Query or ApiParameterSource.Header)
            .OrderBy(binding => binding.Source == ApiParameterSource.Route ? 0 : binding.Source == ApiParameterSource.Query ? 1 : 2)
            .ThenBy(binding => binding.Name, StringComparer.Ordinal)
            .Select(binding => BuildParameter(endpoint, binding, FindParameter(function, binding.PowerShellParameterName)))
            .ToList();

        var operation = Object(
            ("operationId", ResolveOperationId(endpoint, operationIds)),
            ("summary", ChooseText(endpoint.DisplayName, endpoint.PowerShellFunctionName, endpoint.EndpointId)),
            ("description", ChooseText(endpoint.Description, function?.CommentHelp?.Description, function?.CommentHelp?.Synopsis, string.Empty)),
            ("tags", endpoint.Rest.Tags.Count > 0 ? endpoint.Rest.Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray() : [ChooseText(configuration.Api.Title, "PowerShell API")]),
            ("parameters", parameters),
            ("responses", BuildResponses(configuration, endpoint)));

        if (ApiKeyAuthenticationService.IsEndpointAuthenticationRequired(configuration, endpoint))
        {
            operation["security"] = new[]
            {
                Object((ApiKeyAuthenticationService.ApiKeySecuritySchemeName, Array.Empty<string>()))
            };
        }

        var requestBody = BuildRequestBody(endpoint, function);
        if (requestBody is not null)
        {
            operation["requestBody"] = requestBody;
        }

        return operation;
    }

    private static Dictionary<string, object?> BuildParameter(
        ApiEndpointConfiguration endpoint,
        ApiParameterBindingConfiguration binding,
        ApiParameterMetadata? metadata)
        => Object(
            ("name", binding.Name),
            ("in", binding.Source == ApiParameterSource.Route ? "path" : binding.Source.ToString().ToLowerInvariant()),
            ("required", binding.Source == ApiParameterSource.Route || IsRequired(binding, metadata)),
            ("description", TryGetParameterDescription(metadata)),
            ("schema", BuildSchema(binding, metadata)));

    private static Dictionary<string, object?>? BuildRequestBody(ApiEndpointConfiguration endpoint, ApiFunctionMetadata? function)
    {
        var bodyBindings = endpoint.ParameterBindings
            .Where(binding => binding.Source == ApiParameterSource.Body)
            .OrderBy(binding => binding.Name, StringComparer.Ordinal)
            .ToList();
        if (bodyBindings.Count == 0)
        {
            return null;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var binding in bodyBindings)
        {
            var parameter = FindParameter(function, binding.PowerShellParameterName);
            properties[binding.Name] = BuildSchema(binding, parameter);
            if (IsRequired(binding, parameter))
            {
                required.Add(binding.Name);
            }
        }

        var schema = Object(
            ("type", "object"),
            ("properties", properties),
            ("additionalProperties", true));
        if (required.Count > 0)
        {
            schema["required"] = required.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        return Object(
            ("required", required.Count > 0),
            ("content", Object(
                (ChooseText(endpoint.Rest.ConsumesContentType, "application/json"), Object(("schema", schema))))));
    }

    private static Dictionary<string, object?> BuildResponses(ApiPublishConfiguration configuration, ApiEndpointConfiguration endpoint)
    {
        var successSchema = Object(
            ("description", "Dynamic normalized PowerShell JSON result."),
            ("nullable", true));
        var successContent = Object(
            (ChooseText(endpoint.Rest.ProducesContentType, "application/json"), Object(("schema", successSchema))));
        var responses = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [endpoint.Rest.SuccessStatusCode.ToString(CultureInfo.InvariantCulture)] = Object(
                ("description", "Successful dynamic PowerShell JSON result. Zero pipeline results are JSON null, one result is returned bare, and multiple results are returned as an array."),
                ("content", successContent))
        };

        foreach (var statusCode in ApiInvocationProblemDetailsMapper.PublicProblemStatusCodes)
        {
            responses[statusCode.ToString(CultureInfo.InvariantCulture)] = Object(
                ("description", ProblemDescription(statusCode)),
                ("content", Object(
                    ("application/problem+json", Object(
                        ("schema", Ref("#/components/schemas/ProblemDetails")))),
                    ("application/json", Object(
                        ("schema", Ref("#/components/schemas/ProblemDetails")))))));
        }

        if (ApiKeyAuthenticationService.IsEndpointAuthenticationRequired(configuration, endpoint))
        {
            responses[StatusCodes.Status401Unauthorized.ToString(CultureInfo.InvariantCulture)] = ProblemResponse("Authentication is required or the supplied API key is invalid.");
            responses[StatusCodes.Status403Forbidden.ToString(CultureInfo.InvariantCulture)] = ProblemResponse("Authentication succeeded, but access to this endpoint is denied.");
        }

        return responses;
    }

    private static Dictionary<string, object?> BuildComponents(ApiPublishConfiguration configuration)
    {
        var components = Object(("schemas", BuildSchemas()));
        if (configuration.Security.Mode == ApiSecurityMode.ApiKey &&
            configuration.Endpoints.Any(endpoint => endpoint.IsEnabled && endpoint.RequiresAuthentication))
        {
            components["securitySchemes"] = Object(
                (ApiKeyAuthenticationService.ApiKeySecuritySchemeName, Object(
                    ("type", "apiKey"),
                    ("in", "header"),
                    ("name", ApiKeyAuthenticationService.ApiKeyHeaderName),
                    ("description", "Supply the API key configured externally on the server."))));
        }

        return components;
    }

    private static Dictionary<string, object?> BuildSchemas()
        => Object(
            ("ProblemDetails", Object(
                ("type", "object"),
                ("description", "Sanitized public REST error contract."),
                ("required", new[] { "type", "title", "status", "detail", "requestId" }),
                ("properties", Object(
                    ("type", Object(("type", "string"), ("format", "uri"))),
                    ("title", Object(("type", "string"))),
                    ("status", Object(("type", "integer"), ("format", "int32"))),
                    ("detail", Object(("type", "string"))),
                    ("instance", Object(("type", "string"))),
                    ("requestId", Object(("type", "string"))),
                    ("failureKind", Object(("type", "string"))))))));

    private static Dictionary<string, object?> BuildSchema(ApiParameterBindingConfiguration binding, ApiParameterMetadata? metadata)
    {
        var schema = BuildBaseSchema(binding, metadata);
        ApplyValidation(schema, metadata);
        return schema;
    }

    private static Dictionary<string, object?> BuildBaseSchema(ApiParameterBindingConfiguration binding, ApiParameterMetadata? metadata)
    {
        var typeName = ChooseText(binding.TypeName, metadata?.DeclaredTypeName, metadata?.IsSwitch == true ? "switch" : "string");
        var scalarType = UnwrapType(typeName);
        return scalarType switch
        {
            "int" or "int32" or "System.Int32" => Object(("type", "integer"), ("format", "int32")),
            "long" or "int64" or "System.Int64" => Object(("type", "integer"), ("format", "int64")),
            "double" or "System.Double" => Object(("type", "number"), ("format", "double")),
            "decimal" or "System.Decimal" => Object(("type", "number"), ("format", "decimal")),
            "bool" or "boolean" or "System.Boolean" or "switch" or "System.Management.Automation.SwitchParameter" => Object(("type", "boolean")),
            "datetime" or "System.DateTime" or "datetimeoffset" or "System.DateTimeOffset" => Object(("type", "string"), ("format", "date-time")),
            "guid" or "System.Guid" => Object(("type", "string"), ("format", "uuid")),
            "hashtable" or "System.Collections.Hashtable" or "pscustomobject" or "System.Management.Automation.PSObject" or "System.Management.Automation.PSCustomObject" => Object(("type", "object"), ("additionalProperties", true)),
            _ => Object(("type", "string"))
        };
    }

    private static void ApplyValidation(Dictionary<string, object?> schema, ApiParameterMetadata? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        foreach (var validation in metadata.ValidationAttributes.Where(attribute => attribute.IsFullyResolved))
        {
            switch (validation.Name)
            {
                case "ValidateSet":
                    var values = validation.Arguments
                        .SelectMany(argument => SplitValidationValues(argument.Value))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    if (values.Length > 0)
                    {
                        schema["enum"] = values;
                    }

                    break;
                case "ValidateRange":
                    ApplyDecimalRange(schema, validation.Arguments);
                    break;
                case "ValidateLength":
                    ApplyIntegerRange(schema, validation.Arguments, "minLength", "maxLength");
                    break;
                case "ValidatePattern":
                    var pattern = validation.Arguments.FirstOrDefault()?.Value;
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        schema["pattern"] = pattern;
                    }

                    break;
                case "ValidateNotNullOrEmpty":
                    if (string.Equals(Convert.ToString(schema.GetValueOrDefault("type"), CultureInfo.InvariantCulture), "string", StringComparison.Ordinal))
                    {
                        schema["minLength"] = 1;
                    }

                    break;
            }
        }
    }

    private static void ApplyDecimalRange(Dictionary<string, object?> schema, IReadOnlyList<ApiAttributeArgumentMetadata> arguments)
    {
        if (arguments.Count >= 1 && decimal.TryParse(arguments[0].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var minimum))
        {
            schema["minimum"] = minimum;
        }

        if (arguments.Count >= 2 && decimal.TryParse(arguments[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var maximum))
        {
            schema["maximum"] = maximum;
        }
    }

    private static void ApplyIntegerRange(Dictionary<string, object?> schema, IReadOnlyList<ApiAttributeArgumentMetadata> arguments, string minimumName, string maximumName)
    {
        if (arguments.Count >= 1 && int.TryParse(arguments[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum))
        {
            schema[minimumName] = minimum;
        }

        if (arguments.Count >= 2 && int.TryParse(arguments[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum))
        {
            schema[maximumName] = maximum;
        }
    }

    private static ApiParameterMetadata? FindParameter(ApiFunctionMetadata? function, string parameterName)
        => function?.Parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase));

    private static bool IsRequired(ApiParameterBindingConfiguration binding, ApiParameterMetadata? metadata)
        => binding.Required switch
        {
            ApiRequiredBehavior.Required => true,
            ApiRequiredBehavior.Optional => false,
            _ => metadata?.MandatoryState == ApiParameterMandatoryState.Mandatory
        };

    private static string? TryGetParameterDescription(ApiParameterMetadata? metadata)
        => null;

    private static string ResolveOperationId(ApiEndpointConfiguration endpoint, HashSet<string> operationIds)
    {
        var candidate = ChooseText(endpoint.Rest.OperationId, endpoint.EndpointId, endpoint.PowerShellFunctionName, "operation");
        if (operationIds.Add(candidate))
        {
            return candidate;
        }

        var suffix = SanitizeOperationId(ChooseText(endpoint.EndpointId, endpoint.PowerShellFunctionName, "endpoint"));
        var resolved = $"{candidate}_{suffix}";
        var index = 2;
        while (!operationIds.Add(resolved))
        {
            resolved = $"{candidate}_{suffix}_{index.ToString(CultureInfo.InvariantCulture)}";
            index++;
        }

        return resolved;
    }

    private static string SanitizeOperationId(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray();
        return new string(chars).Trim('_');
    }

    private static string ProblemDescription(int statusCode)
        => statusCode switch
        {
            StatusCodes.Status400BadRequest => "Invalid request, binding failure, or PowerShell validation failure.",
            StatusCodes.Status413PayloadTooLarge => "Request body exceeds the configured API limit.",
            StatusCodes.Status429TooManyRequests => "PowerShell invocation queue capacity or wait timeout failure.",
            StatusCodes.Status500InternalServerError => "PowerShell invocation, normalization, serialization, or internal failure.",
            StatusCodes.Status503ServiceUnavailable => "PowerShell host unavailable.",
            StatusCodes.Status504GatewayTimeout => "PowerShell invocation timeout.",
            _ => "API error."
        };

    private static Dictionary<string, object?> ProblemResponse(string description)
        => Object(
            ("description", description),
            ("content", Object(
                ("application/problem+json", Object(
                    ("schema", Ref("#/components/schemas/ProblemDetails")))),
                ("application/json", Object(
                    ("schema", Ref("#/components/schemas/ProblemDetails")))))));

    private static string NormalizeRouteTemplate(string routeTemplate)
    {
        var route = string.IsNullOrWhiteSpace(routeTemplate) ? "/" : routeTemplate.Trim().Replace('\\', '/');
        if (!route.StartsWith("/", StringComparison.Ordinal))
        {
            route = "/" + route;
        }

        while (route.Length > 1 && route.EndsWith("/", StringComparison.Ordinal))
        {
            route = route[..^1];
        }

        return route;
    }

    private static string UnwrapType(string typeName)
    {
        var unwrapped = string.IsNullOrWhiteSpace(typeName) ? "string" : typeName.Trim();
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

    private static IEnumerable<string> SplitValidationValues(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, object?> Ref(string reference)
        => Object(("$ref", reference));

    private static Dictionary<string, object?> Object(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string ChooseText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
