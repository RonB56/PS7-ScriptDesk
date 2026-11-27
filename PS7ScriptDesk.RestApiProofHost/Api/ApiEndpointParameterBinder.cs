using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public sealed class ApiEndpointParameterBinder
{
    public static ApiEndpointParameterBinder Shared { get; } = new();

    private readonly ApiParameterValueConverter _converter;

    public ApiEndpointParameterBinder()
        : this(ApiParameterValueConverter.Shared)
    {
    }

    public ApiEndpointParameterBinder(ApiParameterValueConverter converter)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public ApiParameterBindingResult Bind(
        ApiEndpointConfiguration endpoint,
        Func<ApiParameterBindingConfiguration, ApiParameterBindingValue> resolveValue)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(resolveValue);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in endpoint.ParameterBindings)
        {
            var valueResult = resolveValue(binding);
            if (!valueResult.IsPresent)
            {
                if (IsRequired(binding))
                {
                    return ApiParameterBindingResult.Invalid(
                        "MissingParameter",
                        $"Required parameter '{binding.Name}' is missing.");
                }

                continue;
            }

            var converted = _converter.Convert(valueResult.Value, binding.TypeName);
            if (!converted.IsValid)
            {
                return ApiParameterBindingResult.Invalid(
                    "InvalidParameter",
                    converted.ErrorMessage ?? $"Parameter '{binding.Name}' has an invalid value.");
            }

            if (IsRequired(binding) && IsMissingRequiredValue(converted.Value))
            {
                return ApiParameterBindingResult.Invalid(
                    "MissingParameter",
                    $"Required parameter '{binding.Name}' is missing.");
            }

            values[binding.PowerShellParameterName] = IsSwitchType(binding.TypeName) && converted.Value is bool switchValue
                ? new SwitchParameter(switchValue)
                : converted.Value;
        }

        return ApiParameterBindingResult.Valid(values);
    }

    public static ApiParameterBindingValue ResolveServerDefinedValue(
        ApiServerDefinedValue? serverValue,
        string? correlationId,
        string? authenticatedPrincipalName)
    {
        if (serverValue is null)
        {
            return ApiParameterBindingValue.Missing;
        }

        return serverValue.Kind switch
        {
            ApiServerDefinedValueKind.Literal => ApiParameterBindingValue.Present(serverValue.Value),
            ApiServerDefinedValueKind.EnvironmentVariable => string.IsNullOrWhiteSpace(serverValue.Value)
                ? ApiParameterBindingValue.Missing
                : Environment.GetEnvironmentVariable(serverValue.Value) is { } value
                    ? ApiParameterBindingValue.Present(value)
                    : ApiParameterBindingValue.Missing,
            ApiServerDefinedValueKind.CorrelationId => string.IsNullOrWhiteSpace(correlationId)
                ? ApiParameterBindingValue.Missing
                : ApiParameterBindingValue.Present(correlationId),
            ApiServerDefinedValueKind.AuthenticatedPrincipalName => string.IsNullOrWhiteSpace(authenticatedPrincipalName)
                ? ApiParameterBindingValue.Missing
                : ApiParameterBindingValue.Present(authenticatedPrincipalName),
            _ => ApiParameterBindingValue.Missing
        };
    }

    private static bool IsRequired(ApiParameterBindingConfiguration binding)
        => binding.Required != ApiRequiredBehavior.Optional;

    private static bool IsMissingRequiredValue(object? value)
        => value is null || value is string text && string.IsNullOrWhiteSpace(text);

    private static bool IsSwitchType(string typeName)
        => string.Equals(typeName, "switch", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Management.Automation.SwitchParameter", StringComparison.OrdinalIgnoreCase);
}

public sealed record ApiParameterBindingValue(bool IsPresent, object? Value)
{
    public static ApiParameterBindingValue Missing { get; } = new(false, null);
    public static ApiParameterBindingValue Present(object? value) => new(true, value);
}

public sealed record ApiParameterBindingResult(
    bool IsValid,
    IReadOnlyDictionary<string, object?> Parameters,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ApiParameterBindingResult Valid(IReadOnlyDictionary<string, object?> parameters)
        => new(true, parameters, null, null);

    public static ApiParameterBindingResult Invalid(string errorCode, string errorMessage)
        => new(false, new Dictionary<string, object?>(), errorCode, errorMessage);
}

