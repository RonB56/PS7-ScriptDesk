using System.Management.Automation;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

internal static class PowerShellFailureClassifier
{
    public static ApiInvocationStatus ClassifyTerminatingException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (IsValidationFailure(exception, TryGetErrorRecord(exception)))
        {
            return ApiInvocationStatus.PowerShellValidationFailure;
        }

        if (IsParameterBindingFailure(exception, TryGetErrorRecord(exception)))
        {
            return ApiInvocationStatus.PowerShellParameterBindingFailure;
        }

        return ApiInvocationStatus.PowerShellTerminatingFailure;
    }

    public static ApiInvocationStatus ClassifyNonTerminatingErrors(IEnumerable<ErrorRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var retained = records.ToList();
        if (retained.Any(record => IsValidationFailure(record.Exception, record)))
        {
            return ApiInvocationStatus.PowerShellValidationFailure;
        }

        if (retained.Any(record => IsParameterBindingFailure(record.Exception, record)))
        {
            return ApiInvocationStatus.PowerShellParameterBindingFailure;
        }

        return ApiInvocationStatus.PowerShellNonTerminatingError;
    }

    private static bool IsValidationFailure(Exception? exception, ErrorRecord? record)
        => exception is ValidationMetadataException ||
           StartsWithErrorId(record, "ParameterArgumentValidationError");

    private static bool IsParameterBindingFailure(Exception? exception, ErrorRecord? record)
        => exception is ParameterBindingException ||
           StartsWithErrorId(record, "ParameterArgument") ||
           StartsWithErrorId(record, "NamedParameterNotFound") ||
           StartsWithErrorId(record, "AmbiguousParameter") ||
           IsParameterBindingCategory(record);

    private static ErrorRecord? TryGetErrorRecord(Exception exception)
        => exception is RuntimeException runtimeException
            ? runtimeException.ErrorRecord
            : null;

    private static bool StartsWithErrorId(ErrorRecord? record, string prefix)
        => !string.IsNullOrWhiteSpace(record?.FullyQualifiedErrorId) &&
           record.FullyQualifiedErrorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsParameterBindingCategory(ErrorRecord? record)
        => record?.CategoryInfo.Category is ErrorCategory.InvalidArgument &&
           string.Equals(record.CategoryInfo.Activity, "ParameterBinding", StringComparison.OrdinalIgnoreCase);
}
