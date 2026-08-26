using System.Globalization;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

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

            values[binding.PowerShellParameterName] = converted.Value;
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
}

public sealed class ApiParameterValueConverter
{
    public static ApiParameterValueConverter Shared { get; } = new();

    public ApiConvertedParameterValue Convert(object? value, string? typeName)
    {
        var normalizedType = string.IsNullOrWhiteSpace(typeName) ? "string" : typeName.Trim();
        return value switch
        {
            JsonElement element => ConvertJsonElement(element, normalizedType),
            _ => ConvertString(System.Convert.ToString(value, CultureInfo.InvariantCulture), normalizedType)
        };
    }

    private static ApiConvertedParameterValue ConvertJsonElement(JsonElement element, string typeName)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return ApiConvertedParameterValue.Valid(null);
        }

        if (IsStringType(typeName))
        {
            return element.ValueKind == JsonValueKind.String
                ? ApiConvertedParameterValue.Valid(element.GetString())
                : ApiConvertedParameterValue.Valid(element.ToString());
        }

        if (IsIntType(typeName))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? ApiConvertedParameterValue.Valid(parsed)
                    : ApiConvertedParameterValue.Invalid("Expected an integer value.");
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
                ? ApiConvertedParameterValue.Valid(value)
                : ApiConvertedParameterValue.Invalid("Expected an integer value.");
        }

        if (IsLongType(typeName))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? ApiConvertedParameterValue.Valid(parsed)
                    : ApiConvertedParameterValue.Invalid("Expected a long integer value.");
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
                ? ApiConvertedParameterValue.Valid(value)
                : ApiConvertedParameterValue.Invalid("Expected a long integer value.");
        }

        if (IsDoubleType(typeName))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? ApiConvertedParameterValue.Valid(parsed)
                    : ApiConvertedParameterValue.Invalid("Expected a numeric value.");
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
                ? ApiConvertedParameterValue.Valid(value)
                : ApiConvertedParameterValue.Invalid("Expected a numeric value.");
        }

        if (IsBoolType(typeName))
        {
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ApiConvertedParameterValue.Valid(element.GetBoolean())
                : ApiConvertedParameterValue.Invalid("Expected a boolean value.");
        }

        return ApiConvertedParameterValue.Valid(element.ToString());
    }

    private static ApiConvertedParameterValue ConvertString(string? value, string typeName)
    {
        if (value is null)
        {
            return ApiConvertedParameterValue.Valid(null);
        }

        if (IsStringType(typeName))
        {
            return ApiConvertedParameterValue.Valid(value);
        }

        if (IsIntType(typeName))
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected an integer value.");
        }

        if (IsLongType(typeName))
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a long integer value.");
        }

        if (IsDoubleType(typeName))
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a numeric value.");
        }

        if (IsBoolType(typeName))
        {
            return bool.TryParse(value, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a boolean value.");
        }

        return ApiConvertedParameterValue.Valid(value);
    }

    private static bool IsStringType(string typeName)
        => string.Equals(typeName, "string", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.String", StringComparison.OrdinalIgnoreCase);

    private static bool IsIntType(string typeName)
        => string.Equals(typeName, "int", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "int32", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Int32", StringComparison.OrdinalIgnoreCase);

    private static bool IsLongType(string typeName)
        => string.Equals(typeName, "long", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "int64", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Int64", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleType(string typeName)
        => string.Equals(typeName, "double", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Double", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoolType(string typeName)
        => string.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "boolean", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Boolean", StringComparison.OrdinalIgnoreCase);
}

public sealed record ApiParameterBindingValue(bool IsPresent, object? Value)
{
    public static ApiParameterBindingValue Missing { get; } = new(false, null);
    public static ApiParameterBindingValue Present(object? value) => new(true, value);
}

public sealed record ApiConvertedParameterValue(bool IsValid, object? Value, string? ErrorMessage)
{
    public static ApiConvertedParameterValue Valid(object? value) => new(true, value, null);
    public static ApiConvertedParameterValue Invalid(string message) => new(false, null, message);
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
