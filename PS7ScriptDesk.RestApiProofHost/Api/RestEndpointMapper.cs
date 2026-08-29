using System.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class RestEndpointMapper
{
    public static void MapConfiguredEndpoints(WebApplication app)
    {
        var configuration = app.Services.GetRequiredService<ApiPublishConfiguration>();

        foreach (var endpoint in configuration.Endpoints.Where(endpoint =>
                     endpoint.IsEnabled &&
                     ApiTransportFacts.ResolveEndpointTransport(configuration, endpoint) == ApiTransport.Rest))
        {
            var capturedEndpoint = endpoint;
            if (capturedEndpoint.Rest.Method == ApiHttpMethod.Get)
            {
                app.MapGet(
                    capturedEndpoint.Rest.RouteTemplate,
                    (Delegate)((HttpContext context) => InvokeEndpointAsync(context, capturedEndpoint)));
            }
            else if (capturedEndpoint.Rest.Method == ApiHttpMethod.Post)
            {
                app.MapPost(
                    capturedEndpoint.Rest.RouteTemplate,
                    (Delegate)((HttpContext context) => InvokeEndpointAsync(context, capturedEndpoint)));
            }
        }
    }

    private static async Task<IResult> InvokeEndpointAsync(HttpContext context, ApiEndpointConfiguration endpoint)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiProofHost.Endpoint");
        var authentication = context.RequestServices.GetRequiredService<ApiKeyAuthenticationService>();
        var binder = context.RequestServices.GetRequiredService<RestParameterBinder>();

        var authenticationResult = authentication.AuthenticateEndpoint(context, endpoint);
        if (!authenticationResult.IsSuccess)
        {
            logger.LogWarning(
                "Rejected unauthenticated request for endpoint {EndpointId} function {FunctionName} path {Path}: status {Status}.",
                endpoint.EndpointId,
                endpoint.PowerShellFunctionName,
                context.Request.Path.Value,
                authenticationResult.StatusCode);
            return authenticationResult.ToResult(context);
        }

        RestParameterBindingResult bindResult;
        try
        {
            bindResult = await binder.BindAsync(context, endpoint, context.RequestAborted);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            logger.LogWarning(
                "Rejected request for endpoint {EndpointId} function {FunctionName} path {Path}: request body too large.",
                endpoint.EndpointId,
                endpoint.PowerShellFunctionName,
                context.Request.Path.Value);
            return ApiInvocationProblemDetailsMapper.ToRequestBodyTooLarge(context);
        }

        if (!bindResult.IsValid)
        {
            logger.LogWarning(
                "Rejected request for endpoint {EndpointId} function {FunctionName} path {Path}: {ErrorCode}",
                endpoint.EndpointId,
                endpoint.PowerShellFunctionName,
                context.Request.Path.Value,
                bindResult.ErrorCode);
            return ApiInvocationProblemDetailsMapper.ToRequestBindingFailure(
                context,
                bindResult.ErrorMessage ?? "The request could not be bound.");
        }

        return await InvokeEndpointWithParametersAsync(context, endpoint, bindResult.Parameters);
    }

    private static async Task<IResult> InvokeEndpointWithParametersAsync(
        HttpContext context,
        ApiEndpointConfiguration endpoint,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiProofHost.Endpoint");
        var coordinator = context.RequestServices.GetRequiredService<PowerShellInvocationCoordinator>();
        var normalizer = context.RequestServices.GetRequiredService<PowerShellResultNormalizer>();
        var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
        var stopwatch = Stopwatch.StartNew();

        var result = await coordinator.InvokeAsync(
            new ApiInvocationRequest
            {
                FunctionName = endpoint.PowerShellFunctionName,
                Parameters = parameters,
                Timeout = endpoint.TimeoutOverride
            },
            context.RequestAborted);

        stopwatch.Stop();
        if (result.IsSuccess)
        {
            var normalized = normalizer.Normalize(
                result.Output,
                configuration.Runtime,
                ApiJsonOptions.Shared);
            if (!normalized.IsSuccess)
            {
                logger.LogWarning(
                    "Endpoint {EndpointId} function {FunctionName} path {Path} normalization failed with {FailureKind} in {ElapsedMilliseconds} ms.",
                    endpoint.EndpointId,
                    endpoint.PowerShellFunctionName,
                    context.Request.Path.Value,
                    normalized.FailureKind,
                    stopwatch.ElapsedMilliseconds);
                return ApiInvocationProblemDetailsMapper.ToResult(
                    ApiInvocationResult.Failure(
                        StatusForNormalizationFailure(normalized.FailureKind),
                        normalized.SafeMessage,
                        elapsed: stopwatch.Elapsed,
                        poolGeneration: result.PoolGeneration,
                        normalizationFailureKind: normalized.FailureKind),
                    context);
            }

            logger.LogInformation(
                "Completed endpoint {EndpointId} function {FunctionName} path {Path} in {ElapsedMilliseconds} ms with {SerializedByteCount} serialized bytes.",
                endpoint.EndpointId,
                endpoint.PowerShellFunctionName,
                context.Request.Path.Value,
                stopwatch.ElapsedMilliseconds,
                normalized.SerializedByteCount);
            return Results.Json(
                normalized.Value,
                options: ApiJsonOptions.Shared);
        }

        logger.LogWarning(
            "Endpoint {EndpointId} function {FunctionName} path {Path} completed with status {Status} in {ElapsedMilliseconds} ms.",
            endpoint.EndpointId,
            endpoint.PowerShellFunctionName,
            context.Request.Path.Value,
            result.Status,
            stopwatch.ElapsedMilliseconds);

        return ApiInvocationProblemDetailsMapper.ToResult(result, context);
    }

    private static ApiInvocationStatus StatusForNormalizationFailure(NormalizationFailureKind failureKind)
        => failureKind is NormalizationFailureKind.ItemLimitExceeded or NormalizationFailureKind.ByteLimitExceeded
            ? ApiInvocationStatus.SerializationOutputLimitFailure
            : ApiInvocationStatus.NormalizationFailure;

}
