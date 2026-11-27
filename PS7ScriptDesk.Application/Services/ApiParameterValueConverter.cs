using System.Globalization;
using System.Text.Json;

namespace PS7ScriptDesk.Application.Services;

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

        if (IsDecimalType(typeName))
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a decimal value.");
        }

        if (IsDateTimeType(typeName))
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a date and time value.");
        }

        if (IsDateTimeOffsetType(typeName))
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a date and time with offset value.");
        }

        if (IsGuidType(typeName))
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return Guid.TryParse(text, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a GUID value.");
        }

        if (IsBoolType(typeName))
        {
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ApiConvertedParameterValue.Valid(element.GetBoolean())
                : ApiConvertedParameterValue.Invalid("Expected a boolean value.");
        }

        if (IsSwitchType(typeName))
        {
            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return ApiConvertedParameterValue.Valid(element.GetBoolean());
            }

            if (element.ValueKind == JsonValueKind.String &&
                bool.TryParse(element.GetString(), out var parsed))
            {
                return ApiConvertedParameterValue.Valid(parsed);
            }

            return ApiConvertedParameterValue.Invalid("Expected a boolean switch value.");
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

        if (IsDecimalType(typeName))
        {
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a decimal value.");
        }

        if (IsDateTimeType(typeName))
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a date and time value.");
        }

        if (IsDateTimeOffsetType(typeName))
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a date and time with offset value.");
        }

        if (IsGuidType(typeName))
        {
            return Guid.TryParse(value, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a GUID value.");
        }

        if (IsBoolType(typeName))
        {
            return bool.TryParse(value, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a boolean value.");
        }

        if (IsSwitchType(typeName))
        {
            return bool.TryParse(value, out var parsed)
                ? ApiConvertedParameterValue.Valid(parsed)
                : ApiConvertedParameterValue.Invalid("Expected a boolean switch value.");
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

    private static bool IsDecimalType(string typeName)
        => string.Equals(typeName, "decimal", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Decimal", StringComparison.OrdinalIgnoreCase);

    private static bool IsDateTimeType(string typeName)
        => string.Equals(typeName, "datetime", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.DateTime", StringComparison.OrdinalIgnoreCase);

    private static bool IsDateTimeOffsetType(string typeName)
        => string.Equals(typeName, "datetimeoffset", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.DateTimeOffset", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuidType(string typeName)
        => string.Equals(typeName, "guid", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Guid", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoolType(string typeName)
        => string.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "boolean", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Boolean", StringComparison.OrdinalIgnoreCase);

    private static bool IsSwitchType(string typeName)
        => string.Equals(typeName, "switch", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(typeName, "System.Management.Automation.SwitchParameter", StringComparison.OrdinalIgnoreCase);
}

public sealed record ApiConvertedParameterValue(bool IsValid, object? Value, string? ErrorMessage)
{
    public static ApiConvertedParameterValue Valid(object? value) => new(true, value, null);
    public static ApiConvertedParameterValue Invalid(string message) => new(false, null, message);
}


