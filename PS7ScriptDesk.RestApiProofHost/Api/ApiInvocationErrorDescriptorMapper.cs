using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class ApiInvocationErrorDescriptorMapper
{
    public const string ErrorTypeBase = "https://ps7scriptdesk.local/errors/";

    public static ApiInvocationErrorDescriptor Describe(ApiInvocationStatus status)
        => status switch
        {
            ApiInvocationStatus.RequestBindingFailure => new(
                "request-binding-failure",
                "Invalid request.",
                StatusCodes.Status400BadRequest,
                "The request could not be bound."),
            ApiInvocationStatus.InvalidFunction => new(
                "invalid-function",
                "Invalid PowerShell function.",
                StatusCodes.Status400BadRequest,
                "The configured PowerShell function is not available."),
            ApiInvocationStatus.QueueFull => new(
                "queue-full",
                "PowerShell host busy.",
                StatusCodes.Status429TooManyRequests,
                "The PowerShell invocation queue is full."),
            ApiInvocationStatus.QueueWaitTimedOut => new(
                "queue-wait-timeout",
                "PowerShell host busy.",
                StatusCodes.Status429TooManyRequests,
                "The PowerShell invocation queue wait timed out."),
            ApiInvocationStatus.CallerCanceled => new(
                "caller-canceled",
                "PowerShell invocation canceled.",
                StatusCodes.Status400BadRequest,
                "The caller canceled the request."),
            ApiInvocationStatus.InvocationTimedOut => new(
                "invocation-timeout",
                "PowerShell invocation timed out.",
                StatusCodes.Status504GatewayTimeout,
                "The configured PowerShell operation did not complete before its timeout."),
            ApiInvocationStatus.PowerShellParameterBindingFailure => new(
                "powershell-parameter-binding-failure",
                "PowerShell parameter binding failed.",
                StatusCodes.Status400BadRequest,
                "The PowerShell invocation parameters are invalid."),
            ApiInvocationStatus.PowerShellValidationFailure => new(
                "powershell-validation-failure",
                "PowerShell validation failed.",
                StatusCodes.Status400BadRequest,
                "The PowerShell invocation parameters failed validation."),
            ApiInvocationStatus.PowerShellNonTerminatingError => new(
                "powershell-non-terminating-error",
                "PowerShell invocation failed.",
                StatusCodes.Status500InternalServerError,
                "The configured PowerShell operation reported a non-terminating error."),
            ApiInvocationStatus.PowerShellTerminatingFailure or ApiInvocationStatus.PowerShellFailure => new(
                "powershell-terminating-failure",
                "PowerShell invocation failed.",
                StatusCodes.Status500InternalServerError,
                "The configured PowerShell operation could not be completed."),
            ApiInvocationStatus.NormalizationFailure => new(
                "normalization-failure",
                "PowerShell output could not be serialized.",
                StatusCodes.Status500InternalServerError,
                "The configured PowerShell operation returned output that could not be converted safely."),
            ApiInvocationStatus.SerializationOutputLimitFailure => new(
                "serialization-output-limit-failure",
                "PowerShell output limit exceeded.",
                StatusCodes.Status500InternalServerError,
                "The configured PowerShell operation returned output that exceeded a configured response limit."),
            ApiInvocationStatus.HostUnavailable => new(
                "host-unavailable",
                "PowerShell host unavailable.",
                StatusCodes.Status503ServiceUnavailable,
                "The PowerShell host is not available."),
            ApiInvocationStatus.InternalFailure => new(
                "internal-failure",
                "API request failed.",
                StatusCodes.Status500InternalServerError,
                "The request could not be completed."),
            _ => new(
                "internal-failure",
                "API request failed.",
                StatusCodes.Status500InternalServerError,
                "The request could not be completed.")
        };

    public static ApiInvocationErrorDescriptor DescribeRequestBodyTooLarge()
        => new(
            "request-body-too-large",
            "Request body too large.",
            StatusCodes.Status413PayloadTooLarge,
            "The request body exceeds the configured API limit.");
}

public sealed record ApiInvocationErrorDescriptor(
    string Slug,
    string Title,
    int StatusCode,
    string Detail)
{
    public string Type => ApiInvocationErrorDescriptorMapper.ErrorTypeBase + Slug;
}
