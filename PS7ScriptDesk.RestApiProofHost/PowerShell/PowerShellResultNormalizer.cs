using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class PowerShellResultNormalizer
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.General);
    public static PowerShellResultNormalizer Shared { get; } = new();

    public NormalizedApiResult Normalize(
        IReadOnlyList<PSObject> output,
        ApiRuntimeOptions runtimeOptions,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(runtimeOptions);

        var options = NormalizationOptions.FromRuntime(runtimeOptions);
        if (output.Count > options.MaximumItems)
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.ItemLimitExceeded,
                "The PowerShell output exceeded the configured item limit.");
        }

        var context = new NormalizationContext(options);
        var result = output.Count switch
        {
            0 => NormalizedApiResult.Success(null, 0),
            1 => NormalizeValue(output[0], context, depth: 1),
            _ => NormalizePipelineArray(output, context)
        };

        if (!result.IsSuccess)
        {
            return result;
        }

        return ValidateSerializedSize(result.Value, options.MaximumBytes, jsonOptions ?? DefaultJsonOptions);
    }

    private static NormalizedApiResult NormalizePipelineArray(IReadOnlyList<PSObject> output, NormalizationContext context)
    {
        var items = new List<object?>(output.Count);
        foreach (var item in output)
        {
            if (!context.TryConsumeItem())
            {
                return NormalizedApiResult.Failure(
                    NormalizationFailureKind.ItemLimitExceeded,
                    "The PowerShell output exceeded the configured item limit.");
            }

            var normalized = NormalizeValue(item, context, depth: 2);
            if (!normalized.IsSuccess)
            {
                return normalized;
            }

            items.Add(normalized.Value);
        }

        return NormalizedApiResult.Success(items, 0);
    }

    private static NormalizedApiResult NormalizeValue(object? value, NormalizationContext context, int depth)
    {
        if (depth > context.Options.MaximumDepth)
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.DepthExceeded,
                "The PowerShell output exceeded the configured serialization depth.");
        }

        if (value is null)
        {
            return NormalizedApiResult.Success(null, 0);
        }

        if (TryNormalizeScalar(value, out var scalar))
        {
            return NormalizedApiResult.Success(scalar, 0);
        }

        if (value is PSObject psObject)
        {
            return NormalizePowerShellObject(psObject, context, depth);
        }

        if (value is IDictionary dictionary)
        {
            return NormalizeDictionary(dictionary, context, depth);
        }

        if (value is IEnumerable enumerable and not string)
        {
            return NormalizeEnumerable(enumerable, context, depth);
        }

        return NormalizeDotNetObject(value, context, depth);
    }

    private static NormalizedApiResult NormalizePowerShellObject(PSObject psObject, NormalizationContext context, int depth)
    {
        if (IsFormattingObject(psObject))
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.FormattingObjectRejected,
                "PowerShell formatting output cannot be returned as API data.");
        }

        if (!context.TryEnter(psObject))
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.CycleDetected,
                "The PowerShell output contains a cycle.");
        }

        try
        {
            var baseObject = psObject.BaseObject;
            if (baseObject is null)
            {
                return NormalizedApiResult.Success(null, 0);
            }

            if (TryNormalizeScalar(baseObject, out var scalar))
            {
                return NormalizedApiResult.Success(scalar, 0);
            }

            if (baseObject is IDictionary dictionary)
            {
                return NormalizeDictionary(dictionary, context, depth);
            }

            if (baseObject is IEnumerable enumerable and not string)
            {
                return NormalizeEnumerable(enumerable, context, depth);
            }

            var properties = psObject.Properties
                .Where(property => property.IsGettable &&
                                   property.MemberType is PSMemberTypes.NoteProperty or PSMemberTypes.Property or PSMemberTypes.ScriptProperty)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToList();

            if (properties.Count > 0)
            {
                return NormalizePowerShellProperties(properties, context, depth);
            }

            return NormalizeDotNetObject(baseObject, context, depth);
        }
        finally
        {
            context.Exit(psObject);
        }
    }

    private static NormalizedApiResult NormalizePowerShellProperties(
        IReadOnlyList<PSPropertyInfo> properties,
        NormalizationContext context,
        int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (!names.Add(property.Name))
            {
                return NormalizedApiResult.Failure(
                    NormalizationFailureKind.DuplicatePropertyName,
                    "The PowerShell output contains duplicate property names.");
            }

            object? propertyValue;
            try
            {
                propertyValue = property.Value;
            }
            catch
            {
                return NormalizedApiResult.Failure(
                    NormalizationFailureKind.PropertyGetterFailed,
                    "A PowerShell output property could not be read safely.");
            }

            var normalized = NormalizeValue(propertyValue, context, depth + 1);
            if (!normalized.IsSuccess)
            {
                return normalized;
            }

            result[property.Name] = normalized.Value;
        }

        return NormalizedApiResult.Success(result, 0);
    }

    private static NormalizedApiResult NormalizeDictionary(IDictionary dictionary, NormalizationContext context, int depth)
    {
        if (!context.TryEnter(dictionary))
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.CycleDetected,
                "The PowerShell output contains a cycle.");
        }

        try
        {
            var entries = new List<KeyValuePair<string, object?>>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!TryNormalizeDictionaryKey(entry.Key, out var key))
                {
                    return NormalizedApiResult.Failure(
                        NormalizationFailureKind.UnsupportedValue,
                        "The PowerShell output contains an unsupported dictionary key.");
                }

                entries.Add(new KeyValuePair<string, object?>(key, entry.Value));
            }

            entries = entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!names.Add(entry.Key))
                {
                    return NormalizedApiResult.Failure(
                        NormalizationFailureKind.DuplicatePropertyName,
                        "The PowerShell output contains duplicate dictionary keys.");
                }

                if (!context.TryConsumeItem())
                {
                    return NormalizedApiResult.Failure(
                        NormalizationFailureKind.ItemLimitExceeded,
                        "The PowerShell output exceeded the configured item limit.");
                }

                var normalized = NormalizeValue(entry.Value, context, depth + 1);
                if (!normalized.IsSuccess)
                {
                    return normalized;
                }

                result[entry.Key] = normalized.Value;
            }

            return NormalizedApiResult.Success(result, 0);
        }
        finally
        {
            context.Exit(dictionary);
        }
    }

    private static NormalizedApiResult NormalizeEnumerable(IEnumerable enumerable, NormalizationContext context, int depth)
    {
        if (!context.TryEnter(enumerable))
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.CycleDetected,
                "The PowerShell output contains a cycle.");
        }

        try
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                if (!context.TryConsumeItem())
                {
                    return NormalizedApiResult.Failure(
                        NormalizationFailureKind.ItemLimitExceeded,
                        "The PowerShell output exceeded the configured item limit.");
                }

                var normalized = NormalizeValue(item, context, depth + 1);
                if (!normalized.IsSuccess)
                {
                    return normalized;
                }

                result.Add(normalized.Value);
            }

            return NormalizedApiResult.Success(result, 0);
        }
        finally
        {
            context.Exit(enumerable);
        }
    }

    private static NormalizedApiResult NormalizeDotNetObject(object value, NormalizationContext context, int depth)
    {
        if (!context.TryEnter(value))
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.CycleDetected,
                "The PowerShell output contains a cycle.");
        }

        try
        {
            var properties = value.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetMethod is { IsPublic: true, IsStatic: false } &&
                                   property.GetIndexParameters().Length == 0)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToList();

            if (properties.Count == 0)
            {
                return NormalizedApiResult.Failure(
                    NormalizationFailureKind.UnsupportedValue,
                    "The PowerShell output contains an unsupported value.");
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
            {
                if (!names.Add(property.Name))
                {
                    return NormalizedApiResult.Failure(
                        NormalizationFailureKind.DuplicatePropertyName,
                        "The PowerShell output contains duplicate property names.");
                }

                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch
                {
                    return NormalizedApiResult.Failure(
                        NormalizationFailureKind.PropertyGetterFailed,
                        "A PowerShell output property could not be read safely.");
                }

                var normalized = NormalizeValue(propertyValue, context, depth + 1);
                if (!normalized.IsSuccess)
                {
                    return normalized;
                }

                result[property.Name] = normalized.Value;
            }

            return NormalizedApiResult.Success(result, 0);
        }
        finally
        {
            context.Exit(value);
        }
    }

    private static NormalizedApiResult ValidateSerializedSize(object? value, int maximumBytes, JsonSerializerOptions jsonOptions)
    {
        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions);
            if (serialized.Length > maximumBytes)
            {
                return NormalizedApiResult.Failure(
                    NormalizationFailureKind.ByteLimitExceeded,
                    "The normalized PowerShell output exceeded the configured byte limit.");
            }

            return NormalizedApiResult.Success(value, serialized.Length);
        }
        catch (Exception)
        {
            return NormalizedApiResult.Failure(
                NormalizationFailureKind.UnsupportedValue,
                "The normalized PowerShell output could not be measured safely.");
        }
    }

    private static bool TryNormalizeScalar(object value, out object? normalized)
    {
        normalized = value switch
        {
            string text => text,
            char character => character.ToString(),
            bool boolean => boolean,
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => number,
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            Guid guid => guid,
            Enum enumValue => enumValue.ToString(),
            _ => null
        };

        return normalized is not null || value is string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or Guid or Enum;
    }

    private static bool TryNormalizeDictionaryKey(object? key, out string normalized)
    {
        normalized = key switch
        {
            string value => value,
            char value => value.ToString(),
            bool value => value.ToString(CultureInfo.InvariantCulture),
            byte value => value.ToString(CultureInfo.InvariantCulture),
            sbyte value => value.ToString(CultureInfo.InvariantCulture),
            short value => value.ToString(CultureInfo.InvariantCulture),
            ushort value => value.ToString(CultureInfo.InvariantCulture),
            int value => value.ToString(CultureInfo.InvariantCulture),
            uint value => value.ToString(CultureInfo.InvariantCulture),
            long value => value.ToString(CultureInfo.InvariantCulture),
            ulong value => value.ToString(CultureInfo.InvariantCulture),
            float value => value.ToString(CultureInfo.InvariantCulture),
            double value => value.ToString(CultureInfo.InvariantCulture),
            decimal value => value.ToString(CultureInfo.InvariantCulture),
            DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
            Guid value => value.ToString("D", CultureInfo.InvariantCulture),
            Enum value => value.ToString(),
            _ => string.Empty
        };

        return key is string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or Guid or Enum;
    }

    private static bool IsFormattingObject(PSObject psObject)
        => psObject.TypeNames.Any(typeName =>
               typeName.StartsWith("Microsoft.PowerShell.Commands.Internal.Format.", StringComparison.Ordinal)) ||
           psObject.BaseObject.GetType().FullName?.StartsWith("Microsoft.PowerShell.Commands.Internal.Format.", StringComparison.Ordinal) == true;

    private sealed class NormalizationContext
    {
        private readonly HashSet<object> _activeReferences = new(ReferenceEqualityComparer.Instance);
        private int _consumedItems;

        public NormalizationContext(NormalizationOptions options)
        {
            Options = options;
        }

        public NormalizationOptions Options { get; }

        public bool TryConsumeItem()
        {
            if (_consumedItems >= Options.MaximumItems)
            {
                return false;
            }

            _consumedItems++;
            return true;
        }

        public bool TryEnter(object value)
            => !ShouldTrack(value) || _activeReferences.Add(value);

        public void Exit(object value)
        {
            if (ShouldTrack(value))
            {
                _activeReferences.Remove(value);
            }
        }

        private static bool ShouldTrack(object value)
            => value.GetType().IsClass && value is not string;
    }

    private sealed record NormalizationOptions(int MaximumDepth, int MaximumItems, int MaximumBytes)
    {
        public static NormalizationOptions FromRuntime(ApiRuntimeOptions runtimeOptions)
            => new(
                MaximumDepth: Math.Max(1, runtimeOptions.SerializationDepth),
                MaximumItems: Math.Max(1, runtimeOptions.ResponseItemLimit),
                MaximumBytes: Math.Max(1, runtimeOptions.ResponseByteLimit));
    }
}

public enum NormalizationFailureKind
{
    None,
    DepthExceeded,
    CycleDetected,
    ItemLimitExceeded,
    ByteLimitExceeded,
    FormattingObjectRejected,
    UnsupportedValue,
    PropertyGetterFailed,
    DuplicatePropertyName
}

public sealed class NormalizedApiResult
{
    private NormalizedApiResult(
        bool isSuccess,
        object? value,
        NormalizationFailureKind failureKind,
        string safeMessage,
        int serializedByteCount)
    {
        IsSuccess = isSuccess;
        Value = value;
        FailureKind = failureKind;
        SafeMessage = safeMessage;
        SerializedByteCount = serializedByteCount;
    }

    public bool IsSuccess { get; }
    public object? Value { get; }
    public NormalizationFailureKind FailureKind { get; }
    public string SafeMessage { get; }
    public int SerializedByteCount { get; }

    public static NormalizedApiResult Success(object? value, int serializedByteCount)
        => new(true, value, NormalizationFailureKind.None, string.Empty, serializedByteCount);

    public static NormalizedApiResult Failure(NormalizationFailureKind failureKind, string safeMessage)
        => new(false, null, failureKind, safeMessage, 0);
}
