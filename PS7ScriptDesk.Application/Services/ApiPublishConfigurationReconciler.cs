using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class ApiPublishConfigurationReconciler
{
    public ApiPublishConfigurationReconciliation Reconcile(
        ApiPublishConfiguration configuration,
        ApiMetadataResult metadata)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(metadata);

        var findings = new List<ApiPublishReconciliationFinding>();
        AddLegacyAuthenticationFindings(configuration, findings, ApiAuthenticationIntentPolicy.Apply(configuration));
        if (!IsMetadataComplete(metadata))
        {
            var message = !metadata.ParsedSuccessfully
                ? metadata.SyntaxErrors.Count == 0
                    ? "PowerShell metadata discovery was incomplete. Fix the script before saving, testing, building, or publishing this API."
                    : $"PowerShell parsing failed ({metadata.SyntaxErrors.Count} syntax error(s)). Fix the script before saving, testing, building, or publishing this API."
                : "One or more PowerShell parameter declarations have incomplete static metadata. Review the affected declarations before saving, testing, building, or publishing this API.";
            findings.Add(new(ApiPublishReconciliationFindingKind.InvalidMetadata, message));
            return new(configuration, findings, MetadataIsValid: false);
        }

        var functions = metadata.Functions
            .GroupBy(function => function.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var configuredFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in configuration.Endpoints.ToList())
        {
            var functionName = endpoint.PowerShellFunctionName?.Trim() ?? string.Empty;
            if (!functions.TryGetValue(functionName, out var function))
            {
                findings.Add(new(
                    ApiPublishReconciliationFindingKind.RemovedFunction,
                    $"Endpoint '{endpoint.DisplayName}' references removed function '{functionName}'. Its obsolete endpoint definition was excluded from the effective configuration.",
                    endpoint.EndpointId,
                    functionName));
                configuration.Endpoints.Remove(endpoint);
                continue;
            }

            configuredFunctions.Add(function.Name);
            if (!function.IsPublishable)
            {
                findings.Add(new(
                    ApiPublishReconciliationFindingKind.UnavailableFunction,
                    $"Endpoint '{endpoint.DisplayName}' references '{function.Name}', which is no longer publishable. Its endpoint definition was excluded from the effective configuration.",
                    endpoint.EndpointId,
                    function.Name));
                configuration.Endpoints.Remove(endpoint);
                continue;
            }

            ReconcileBindings(endpoint, function, findings);
        }

        foreach (var function in metadata.Functions.Where(function => function.IsPublishable))
        {
            if (!configuredFunctions.Add(function.Name))
            {
                continue;
            }

            var endpoint = ApiPublishEndpointDefaults.Create(function, configuration.Api.DefaultRoutePrefix);
            endpoint.IsEnabled = false;
            endpoint.RequiresAuthentication = configuration.Security.Mode == ApiSecurityMode.ApiKey;
            endpoint.AuthenticationRequirementIsDefaulted = true;
            configuration.Endpoints.Add(endpoint);
            findings.Add(new(
                ApiPublishReconciliationFindingKind.NewFunction,
                $"New function '{function.Name}' is available as a disabled endpoint. Review its route and bindings, then enable it explicitly if it should be exposed.",
                endpoint.EndpointId,
                function.Name));
        }

        AddLegacyAuthenticationFindings(configuration, findings, ApiAuthenticationIntentPolicy.Apply(configuration));
        return new ApiPublishConfigurationReconciliation(configuration, findings, MetadataIsValid: true);
    }

    private static void AddLegacyAuthenticationFindings(
        ApiPublishConfiguration configuration,
        List<ApiPublishReconciliationFinding> findings,
        ApiAuthenticationIntentResult authenticationIntent)
    {
        foreach (var endpointId in authenticationIntent.LegacyEndpointsProtected)
        {
            var endpoint = configuration.Endpoints.First(candidate =>
                string.Equals(candidate.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
            findings.Add(new(
                ApiPublishReconciliationFindingKind.LegacyAuthenticationProtected,
                $"Endpoint '{endpoint.DisplayName}' had an unknown legacy authentication preference and was protected for API-key mode. Review the endpoint before applying reconciliation.",
                endpoint.EndpointId,
                endpoint.PowerShellFunctionName));
        }
    }

    public static bool IsMetadataComplete(ApiMetadataResult metadata)
        => metadata.ParsedSuccessfully && metadata.Functions.All(function =>
            function.Parameters.All(parameter =>
                parameter.IsMetadataComplete && parameter.MandatoryState != ApiParameterMandatoryState.Unknown));

    private static void ReconcileBindings(
        ApiEndpointConfiguration endpoint,
        ApiFunctionMetadata function,
        List<ApiPublishReconciliationFinding> findings)
    {
        var parameters = function.Parameters
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in endpoint.ParameterBindings.ToList())
        {
            if (!parameters.TryGetValue(binding.PowerShellParameterName, out var parameter))
            {
                findings.Add(new(
                    ApiPublishReconciliationFindingKind.RemovedParameter,
                    $"Endpoint '{endpoint.DisplayName}' has an obsolete binding for removed parameter '{binding.PowerShellParameterName}'. The binding was excluded from the effective configuration.",
                    endpoint.EndpointId,
                    function.Name,
                    binding.PowerShellParameterName));
                endpoint.ParameterBindings.Remove(binding);
                continue;
            }

            boundNames.Add(parameter.Name);
            var currentType = GetApiTypeName(parameter);
            if (!AreTypesCompatible(binding.TypeName, currentType))
            {
                findings.Add(new(
                    ApiPublishReconciliationFindingKind.ChangedParameterType,
                    $"Parameter '{parameter.Name}' on '{function.Name}' changed from configured type '{binding.TypeName}' to '{currentType}'. The mapping type was refreshed; review it before applying reconciliation.",
                    endpoint.EndpointId,
                    function.Name,
                    parameter.Name));
                binding.TypeName = currentType;
            }

            if (parameter.MandatoryState == ApiParameterMandatoryState.Mandatory &&
                binding.Source == ApiParameterSource.ServerDefined &&
                (binding.ServerValue is null ||
                 (binding.ServerValue.Kind is ApiServerDefinedValueKind.EnvironmentVariable or ApiServerDefinedValueKind.Literal) && string.IsNullOrWhiteSpace(binding.ServerValue.Value)))
            {
                findings.Add(new(
                    ApiPublishReconciliationFindingKind.InvalidMandatoryBinding,
                    $"Mandatory parameter '{parameter.Name}' on '{function.Name}' uses a server-defined mapping without a value. Provide a valid server value before applying reconciliation.",
                    endpoint.EndpointId,
                    function.Name,
                    parameter.Name));
            }
            else if (parameter.MandatoryState == ApiParameterMandatoryState.Mandatory &&
                     binding.Source != ApiParameterSource.ServerDefined &&
                     binding.Required != ApiRequiredBehavior.Required)
            {
                findings.Add(new(
                    ApiPublishReconciliationFindingKind.ChangedMandatoryState,
                    $"Parameter '{parameter.Name}' on '{function.Name}' is now mandatory. Its HTTP mapping was marked required; review the updated contract.",
                    endpoint.EndpointId,
                    function.Name,
                    parameter.Name));
                binding.Required = ApiRequiredBehavior.Required;
            }
        }

        foreach (var parameter in function.Parameters)
        {
            if (boundNames.Contains(parameter.Name))
            {
                continue;
            }

            var binding = ApiPublishEndpointDefaults.CreateBinding(parameter);
            endpoint.ParameterBindings.Add(binding);
            findings.Add(new(
                ApiPublishReconciliationFindingKind.AddedParameter,
                $"New parameter '{parameter.Name}' on '{function.Name}' has a proposed HTTP mapping. Review it before applying reconciliation.",
                endpoint.EndpointId,
                function.Name,
                parameter.Name));
        }
    }

    private static string GetApiTypeName(ApiParameterMetadata parameter)
        => parameter.DeclaredTypeName ?? (parameter.IsSwitch ? "bool" : "string");

    internal static bool AreTypesCompatible(string? configuredType, string currentType)
    {
        if (string.IsNullOrWhiteSpace(configuredType))
        {
            return true;
        }

        static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "string";
            var normalized = value.Trim().Replace("System.", string.Empty, StringComparison.OrdinalIgnoreCase);
            return normalized.ToLowerInvariant() switch
            {
                "int32" => "int",
                "int64" => "long",
                "boolean" => "bool",
                "datetimeoffset" => "datetimeoffset",
                "datetime" => "datetime",
                _ => normalized.ToLowerInvariant()
            };
        }

        return string.Equals(Normalize(configuredType), Normalize(currentType), StringComparison.Ordinal);
    }
}

public enum ApiPublishReconciliationFindingKind
{
    InvalidMetadata,
    RemovedFunction,
    UnavailableFunction,
    NewFunction,
    RemovedParameter,
    AddedParameter,
    ChangedParameterType,
    ChangedMandatoryState,
    InvalidMandatoryBinding,
    LegacyAuthenticationProtected,
    ConfigurationLoadFailure
}

public sealed record ApiPublishReconciliationFinding(
    ApiPublishReconciliationFindingKind Kind,
    string Message,
    string? EndpointId = null,
    string? FunctionName = null,
    string? ParameterName = null);

public sealed record ApiPublishConfigurationReconciliation(
    ApiPublishConfiguration Configuration,
    IReadOnlyList<ApiPublishReconciliationFinding> Findings,
    bool MetadataIsValid)
{
    public bool RequiresExplicitResolution => !MetadataIsValid || Findings.Any(finding => finding.Kind != ApiPublishReconciliationFindingKind.NewFunction);

    public bool IsBlocked => !MetadataIsValid || Findings.Any(finding => finding.Kind == ApiPublishReconciliationFindingKind.ConfigurationLoadFailure);

}

public static class ApiPublishEndpointDefaults
{
    public static string GetApiTypeName(ApiParameterMetadata parameter)
        => parameter.DeclaredTypeName ?? (parameter.IsSwitch ? "bool" : "string");

    public static ApiEndpointConfiguration Create(ApiFunctionMetadata function, string routePrefix)
    {
        var prefix = string.IsNullOrWhiteSpace(routePrefix) ? "/api" : "/" + routePrefix.Trim().Trim('/');
        if (prefix == "/") prefix = "/api";
        var slug = System.Text.RegularExpressions.Regex.Replace(function.Name.Trim(), @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug)) slug = "endpoint";
        var endpoint = new ApiEndpointConfiguration
        {
            EndpointId = ApiEndpointConfiguration.CreateStableEndpointId(function.Name),
            Transport = ApiTransport.Rest,
            PowerShellFunctionName = function.Name,
            DisplayName = function.Name,
            Description = function.CommentHelp?.Synopsis ?? string.Empty,
            RequiresAuthentication = false,
            AuthenticationRequirementIsDefaulted = true,
            Rest =
            {
                Method = ApiHttpMethod.Get,
                RouteTemplate = $"{prefix}/{slug}",
                OperationId = ToOperationId(function.Name),
                Tags = ["PowerShell"],
                IncludeInOpenApi = true
            },
            ParameterBindings = function.Parameters.Select(CreateBinding).ToList()
        };
        return endpoint;
    }

    public static ApiParameterBindingConfiguration CreateBinding(ApiParameterMetadata parameter)
        => new()
        {
            PowerShellParameterName = parameter.Name,
            Source = ApiParameterSource.Query,
            Name = char.ToLowerInvariant(parameter.Name[0]) + parameter.Name[1..],
            Required = parameter.MandatoryState == ApiParameterMandatoryState.Mandatory
                ? ApiRequiredBehavior.Required
                : ApiRequiredBehavior.Optional,
            TypeName = GetApiTypeName(parameter),
            ArrayBinding = ApiArrayBindingBehavior.RepeatedValues
        };

    private static string ToOperationId(string value)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(value.Trim(), @"[^A-Za-z0-9]+")
            .Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        if (parts.Length == 0) return "invokeEndpoint";
        return char.ToLowerInvariant(parts[0][0]) + parts[0][1..] +
               string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
