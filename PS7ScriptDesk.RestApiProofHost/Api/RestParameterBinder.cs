using System.Globalization;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public sealed class RestParameterBinder
{
    public static RestParameterBinder Shared { get; } = new();

    public async Task<RestParameterBindingResult> BindAsync(
        HttpContext context,
        ApiEndpointConfiguration endpoint,
        CancellationToken cancellationToken)
    {
        JsonDocument? body = null;
        try
        {
            if (endpoint.ParameterBindings.Any(binding => binding.Source == ApiParameterSource.Body))
            {
                body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return RestParameterBindingResult.Invalid("BodyNotObject", "The request body must be a JSON object.");
                }
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in endpoint.ParameterBindings)
            {
                var valueResult = ResolveBindingValue(context, body, binding);
                if (!valueResult.IsPresent)
                {
                    if (IsRequired(binding))
                    {
                        return RestParameterBindingResult.Invalid("MissingParameter", $"Required parameter '{binding.Name}' is missing.");
                    }

                    continue;
                }

                var converted = ConvertValue(valueResult.Value, binding.TypeName);
                if (!converted.IsValid)
                {
                    return RestParameterBindingResult.Invalid("InvalidParameter", converted.ErrorMessage ?? $"Parameter '{binding.Name}' has an invalid value.");
                }

                if (IsRequired(binding) && IsMissingRequiredValue(converted.Value))
                {
                    return RestParameterBindingResult.Invalid("MissingParameter", $"Required parameter '{binding.Name}' is missing.");
                }

                values[binding.PowerShellParameterName] = converted.Value;
            }

            return RestParameterBindingResult.Valid(values);
        }
        catch (JsonException)
        {
            return RestParameterBindingResult.Invalid("MalformedJson", "The request body is not valid JSON.");
        }
        finally
        {
            body?.Dispose();
        }
    }

    private static BindingValue ResolveBindingValue(HttpContext context, JsonDocument? body, ApiParameterBindingConfiguration binding)
    {
        switch (binding.Source)
        {
            case ApiParameterSource.Query:
                return context.Request.Query.TryGetValue(binding.Name, out var queryValue) && queryValue.Count > 0
                    ? BindingValue.Present(queryValue[0])
                    : BindingValue.Missing;
            case ApiParameterSource.Route:
                return context.Request.RouteValues.TryGetValue(binding.Name, out var routeValue)
                    ? BindingValue.Present(Convert.ToString(routeValue, CultureInfo.InvariantCulture))
                    : BindingValue.Missing;
            case ApiParameterSource.Header:
                return context.Request.Headers.TryGetValue(binding.Name, out var headerValue) && headerValue.Count > 0
                    ? BindingValue.Present(headerValue[0])
                    : BindingValue.Missing;
            case ApiParameterSource.Body:
                return body is not null && body.RootElement.TryGetProperty(binding.Name, out var property)
                    ? BindingValue.Present(property.Clone())
                    : BindingValue.Missing;
            case ApiParameterSource.ServerDefined:
                return binding.ServerValue?.Kind == ApiServerDefinedValueKind.Literal
                    ? BindingValue.Present(binding.ServerValue.Value)
                    : BindingValue.Missing;
            default:
                return BindingValue.Missing;
        }
    }

    private static ConvertedValue ConvertValue(object? value, string? typeName)
    {
        var normalizedType = string.IsNullOrWhiteSpace(typeName) ? "string" : typeName.Trim();
        return value switch
        {
            JsonElement element => ConvertJsonElement(element, normalizedType),
            _ => ConvertString(Convert.ToString(value, CultureInfo.InvariantCulture), normalizedType)
        };
    }

    private static ConvertedValue ConvertJsonElement(JsonElement element, string typeName)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return ConvertedValue.Valid(null);
        }

        if (IsStringType(typeName))
        {
            return element.ValueKind == JsonValueKind.String
                ? ConvertedValue.Valid(element.GetString())
                : ConvertedValue.Valid(element.ToString());
        }

        if (IsIntType(typeName))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? ConvertedValue.Valid(parsed)
                    : ConvertedValue.Invalid("Expected an integer value.");
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
                ? ConvertedValue.Valid(value)
                : ConvertedValue.Invalid("Expected an integer value.");
        }

        if (IsLongType(typeName))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? ConvertedValue.Valid(parsed)
                    : ConvertedValue.Invalid("Expected a long integer value.");
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
                ? ConvertedValue.Valid(value)
                : ConvertedValue.Invalid("Expected a long integer value.");
        }

        if (IsDoubleType(typeName))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? ConvertedValue.Valid(parsed)
                    : ConvertedValue.Invalid("Expected a numeric value.");
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
                ? ConvertedValue.Valid(value)
                : ConvertedValue.Invalid("Expected a numeric value.");
        }

        if (IsBoolType(typeName))
        {
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ConvertedValue.Valid(element.GetBoolean())
                : ConvertedValue.Invalid("Expected a boolean value.");
        }

        return ConvertedValue.Valid(element.ToString());
    }

    private static ConvertedValue ConvertString(string? value, string typeName)
    {
        if (value is null)
        {
            return ConvertedValue.Valid(null);
        }

        if (IsStringType(typeName))
        {
            return ConvertedValue.Valid(value);
        }

        if (IsIntType(typeName))
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? ConvertedValue.Valid(parsed)
                : ConvertedValue.Invalid("Expected an integer value.");
        }

        if (IsLongType(typeName))
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? ConvertedValue.Valid(parsed)
                : ConvertedValue.Invalid("Expected a long integer value.");
        }

        if (IsDoubleType(typeName))
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? ConvertedValue.Valid(parsed)
                : ConvertedValue.Invalid("Expected a numeric value.");
        }

        if (IsBoolType(typeName))
        {
            return bool.TryParse(value, out var parsed)
                ? ConvertedValue.Valid(parsed)
                : ConvertedValue.Invalid("Expected a boolean value.");
        }

        return ConvertedValue.Valid(value);
    }

    private static bool IsRequired(ApiParameterBindingConfiguration binding)
        => binding.Required != ApiRequiredBehavior.Optional;

    private static bool IsMissingRequiredValue(object? value)
        => value is null || value is string text && string.IsNullOrWhiteSpace(text);

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

    private sealed record BindingValue(bool IsPresent, object? Value)
    {
        public static BindingValue Missing { get; } = new(false, null);
        public static BindingValue Present(object? value) => new(true, value);
    }

    private sealed record ConvertedValue(bool IsValid, object? Value, string? ErrorMessage)
    {
        public static ConvertedValue Valid(object? value) => new(true, value, null);
        public static ConvertedValue Invalid(string message) => new(false, null, message);
    }
}

public sealed record RestParameterBindingResult(
    bool IsValid,
    IReadOnlyDictionary<string, object?> Parameters,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static RestParameterBindingResult Valid(IReadOnlyDictionary<string, object?> parameters)
        => new(true, parameters, null, null);

    public static RestParameterBindingResult Invalid(string errorCode, string errorMessage)
        => new(false, new Dictionary<string, object?>(), errorCode, errorMessage);
}
