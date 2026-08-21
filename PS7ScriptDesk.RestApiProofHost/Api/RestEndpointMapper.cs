using System.Diagnostics;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Models;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class RestEndpointMapper
{
    public static void MapConfiguredEndpoints(WebApplication app)
    {
        var configuration = app.Services.GetRequiredService<ApiPublishConfiguration>();

        foreach (var endpoint in configuration.Endpoints.Where(endpoint => endpoint.IsEnabled))
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
                    (Delegate)((HttpContext context) => InvokePostEndpointAsync(context, capturedEndpoint)));
            }
        }
    }

    private static async Task<IResult> InvokePostEndpointAsync(HttpContext context, ApiEndpointConfiguration endpoint)
    {
        if (IsSystemInfoPostEndpoint(endpoint))
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync<SystemInfoRequest>(
                    context.Request.Body,
                    RestApiProofHost.Hosting.RestApiProofHostFactory.JsonOptions,
                    context.RequestAborted);

                if (string.IsNullOrWhiteSpace(request?.ComputerName))
                {
                    return BadRequest("Required parameter 'computerName' is missing.");
                }

                return await InvokeEndpointWithParametersAsync(
                    context,
                    endpoint,
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ComputerName"] = request.ComputerName
                    });
            }
            catch (JsonException)
            {
                return BadRequest("The request body is not valid JSON.");
            }
        }

        return await InvokeEndpointAsync(context, endpoint);
    }

    private static async Task<IResult> InvokeEndpointAsync(HttpContext context, ApiEndpointConfiguration endpoint)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiProofHost.Endpoint");
        var binder = context.RequestServices.GetRequiredService<RestParameterBinder>();

        var bindResult = await binder.BindAsync(context, endpoint, context.RequestAborted);
        if (!bindResult.IsValid)
        {
            logger.LogWarning(
                "Rejected request for endpoint {EndpointId} function {FunctionName} path {Path}: {ErrorCode}",
                endpoint.EndpointId,
                endpoint.PowerShellFunctionName,
                context.Request.Path.Value,
                bindResult.ErrorCode);
            return BadRequest(bindResult.ErrorMessage ?? "The request could not be bound.");
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
                RestApiProofHost.Hosting.RestApiProofHostFactory.JsonOptions);
            if (!normalized.IsSuccess)
            {
                logger.LogWarning(
                    "Endpoint {EndpointId} function {FunctionName} path {Path} normalization failed with {FailureKind} in {ElapsedMilliseconds} ms.",
                    endpoint.EndpointId,
                    endpoint.PowerShellFunctionName,
                    context.Request.Path.Value,
                    normalized.FailureKind,
                    stopwatch.ElapsedMilliseconds);
                return ServerError("PowerShell output could not be serialized.", "The configured PowerShell operation returned output that could not be converted safely.");
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
                options: RestApiProofHost.Hosting.RestApiProofHostFactory.JsonOptions);
        }

        logger.LogWarning(
            "Endpoint {EndpointId} function {FunctionName} path {Path} completed with status {Status} in {ElapsedMilliseconds} ms.",
            endpoint.EndpointId,
            endpoint.PowerShellFunctionName,
            context.Request.Path.Value,
            result.Status,
            stopwatch.ElapsedMilliseconds);

        return result.Status switch
        {
            ApiInvocationStatus.QueueFull => Problem("PowerShell host busy.", StatusCodes.Status429TooManyRequests, "The PowerShell invocation queue is full."),
            ApiInvocationStatus.QueueWaitTimedOut => Problem("PowerShell host busy.", StatusCodes.Status429TooManyRequests, "The PowerShell invocation queue wait timed out."),
            ApiInvocationStatus.InvocationTimedOut => Problem("PowerShell invocation timed out.", StatusCodes.Status504GatewayTimeout, "The configured PowerShell operation did not complete before its timeout."),
            ApiInvocationStatus.CallerCanceled => Problem("PowerShell invocation canceled.", 499, "The caller canceled the request."),
            ApiInvocationStatus.HostUnavailable => Problem("PowerShell host unavailable.", StatusCodes.Status503ServiceUnavailable, "The PowerShell host is not available."),
            ApiInvocationStatus.InvalidFunction => ServerError("PowerShell invocation failed.", "The configured PowerShell operation could not be completed."),
            ApiInvocationStatus.PowerShellFailure => ServerError("PowerShell invocation failed.", "The configured PowerShell operation could not be completed."),
            _ => ServerError("API request failed.", "The request could not be completed.")
        };
    }

    private static bool IsSystemInfoPostEndpoint(ApiEndpointConfiguration endpoint)
        => endpoint.Rest.Method == ApiHttpMethod.Post &&
           string.Equals(endpoint.PowerShellFunctionName, "Get-SystemInfo", StringComparison.OrdinalIgnoreCase) &&
           endpoint.ParameterBindings.Count == 1 &&
           endpoint.ParameterBindings[0].Source == ApiParameterSource.Body &&
           string.Equals(endpoint.ParameterBindings[0].PowerShellParameterName, "ComputerName", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(endpoint.ParameterBindings[0].Name, "computerName", StringComparison.OrdinalIgnoreCase);

    private static IResult BadRequest(string detail)
        => Results.Json(
            new ApiProofProblemDetails("Invalid request.", StatusCodes.Status400BadRequest, detail),
            statusCode: StatusCodes.Status400BadRequest,
            options: RestApiProofHost.Hosting.RestApiProofHostFactory.JsonOptions);

    private static IResult ServerError(string title, string detail)
        => Problem(title, StatusCodes.Status500InternalServerError, detail);

    private static IResult Problem(string title, int statusCode, string detail)
        => Results.Json(
            new ApiProofProblemDetails(title, statusCode, detail),
            statusCode: statusCode,
            options: RestApiProofHost.Hosting.RestApiProofHostFactory.JsonOptions);
}

public sealed record ApiProofProblemDetails(string Title, int Status, string Detail);
