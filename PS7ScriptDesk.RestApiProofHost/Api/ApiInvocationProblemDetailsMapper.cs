using Microsoft.AspNetCore.Mvc;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class ApiInvocationProblemDetailsMapper
{
    public static IReadOnlyList<int> PublicProblemStatusCodes { get; } =
    [
        StatusCodes.Status400BadRequest,
        StatusCodes.Status413PayloadTooLarge,
        StatusCodes.Status429TooManyRequests,
        StatusCodes.Status500InternalServerError,
        StatusCodes.Status503ServiceUnavailable,
        StatusCodes.Status504GatewayTimeout
    ];

    public static IResult ToResult(ApiInvocationResult result, HttpContext context)
    {
        var details = CreateProblemDetails(result, context);
        return Results.Json(
            details,
            statusCode: details.Status,
            options: ApiJsonOptions.Shared);
    }

    public static IResult ToRequestBindingFailure(HttpContext context, string safeDetail)
        => ToResult(
            ApiInvocationResult.Failure(
                ApiInvocationStatus.RequestBindingFailure,
                string.IsNullOrWhiteSpace(safeDetail) ? "The request could not be bound." : safeDetail),
            context);

    public static IResult ToRequestBodyTooLarge(HttpContext context)
    {
        var descriptor = ApiInvocationErrorDescriptorMapper.DescribeRequestBodyTooLarge();
        var details = new ProblemDetails
        {
            Type = descriptor.Type,
            Title = descriptor.Title,
            Status = descriptor.StatusCode,
            Detail = descriptor.Detail,
            Instance = context.Request.Path.Value
        };
        details.Extensions["requestId"] = context.TraceIdentifier;

        return Results.Json(
            details,
            statusCode: details.Status,
            options: ApiJsonOptions.Shared,
            contentType: "application/problem+json");
    }

    public static ProblemDetails CreateProblemDetails(ApiInvocationResult result, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var descriptor = ApiInvocationErrorDescriptorMapper.Describe(result.Status);
        var details = new ProblemDetails
        {
            Type = descriptor.Type,
            Title = descriptor.Title,
            Status = descriptor.StatusCode,
            Detail = string.IsNullOrWhiteSpace(result.SafeMessage) ? descriptor.Detail : result.SafeMessage,
            Instance = context.Request.Path.Value
        };
        details.Extensions["requestId"] = context.TraceIdentifier;

        if (result.NormalizationFailureKind != NormalizationFailureKind.None)
        {
            details.Extensions["failureKind"] = result.NormalizationFailureKind.ToString();
        }

        return details;
    }
}
