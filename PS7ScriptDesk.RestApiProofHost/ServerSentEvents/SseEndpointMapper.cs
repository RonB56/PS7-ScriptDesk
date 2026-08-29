using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.ServerSentEvents;

public static class SseEndpointMapper
{
    public const string RouteTemplate = "/sse/{endpointId}";
    private static readonly JsonSerializerOptions EventJsonOptions = CreateEventJsonOptions();

    public static void MapSseEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(RouteTemplate, HandleSseAsync);
    }

    private static async Task HandleSseAsync(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RestApiProofHost.Sse");
        var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
        var endpointId = Convert.ToString(context.Request.RouteValues["endpointId"], CultureInfo.InvariantCulture);

        var resolved = ApiEndpointResolver.Shared.ResolveByEndpointId(configuration, endpointId, ApiTransport.ServerSentEvents);
        if (!resolved.IsSuccess || resolved.Endpoint is null)
        {
            logger.LogWarning(
                "Rejected SSE request for endpoint route {EndpointId}: {ErrorCode}.",
                endpointId,
                resolved.ErrorCode);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var endpoint = resolved.Endpoint;
        var authentication = context.RequestServices.GetRequiredService<ApiKeyAuthenticationService>();
        var authenticationResult = authentication.AuthenticateEndpoint(context, endpoint);
        if (!authenticationResult.IsSuccess)
        {
            logger.LogWarning(
                "Rejected unauthorized SSE request for endpoint {EndpointId}: status {Status}.",
                endpoint.EndpointId,
                authenticationResult.StatusCode);
            await authenticationResult.ToResult(context).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var binder = context.RequestServices.GetRequiredService<ApiEndpointParameterBinder>();
        var bindResult = binder.Bind(endpoint, binding => ResolveSseBindingValue(context, binding));
        if (!bindResult.IsValid)
        {
            logger.LogWarning(
                "Rejected SSE invocation for endpoint {EndpointId}: {ErrorCode}.",
                endpoint.EndpointId,
                bindResult.ErrorCode);
            await ApiInvocationProblemDetailsMapper.ToRequestBindingFailure(
                context,
                bindResult.ErrorMessage ?? "The request could not be bound.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

        var connectionId = $"sse-{Guid.NewGuid():N}";
        await using var session = await context.RequestServices
            .GetRequiredService<PowerShellInvocationCoordinator>()
            .StartStreamingInvocationAsync(
                new ApiStreamingInvocationRequest
                {
                    InvocationId = Guid.NewGuid().ToString("N"),
                    EndpointId = endpoint.EndpointId,
                    FunctionName = endpoint.PowerShellFunctionName,
                    Parameters = bindResult.Parameters,
                    ConnectionId = connectionId,
                    SessionId = connectionId,
                    Timeout = endpoint.TimeoutOverride
                },
                context.RequestAborted).ConfigureAwait(false);

        logger.LogInformation(
            "Started SSE invocation {InvocationId} connection {ConnectionId} endpoint {EndpointId}.",
            session.Request.InvocationId,
            connectionId,
            endpoint.EndpointId);

        try
        {
            await foreach (var item in session.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                if (!await TryWriteEventAsync(context, item).ConfigureAwait(false))
                {
                    logger.LogInformation(
                        "SSE client disconnect canceled invocation {InvocationId} connection {ConnectionId} endpoint {EndpointId}.",
                        session.Request.InvocationId,
                        connectionId,
                        endpoint.EndpointId);
                    session.Cancel();
                    return;
                }

                if (item.IsTerminal)
                {
                    logger.LogInformation(
                        "Completed SSE invocation {InvocationId} connection {ConnectionId} endpoint {EndpointId} with terminal event {EventKind}.",
                        item.InvocationId,
                        connectionId,
                        endpoint.EndpointId,
                        item.Kind);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "SSE request abort canceled invocation {InvocationId} connection {ConnectionId} endpoint {EndpointId}.",
                session.Request.InvocationId,
                connectionId,
                endpoint.EndpointId);
            session.Cancel();
        }
    }

    private static ApiParameterBindingValue ResolveSseBindingValue(
        HttpContext context,
        ApiParameterBindingConfiguration binding)
    {
        if (binding.Source == ApiParameterSource.ServerDefined)
        {
            return ApiEndpointParameterBinder.ResolveServerDefinedValue(
                binding.ServerValue,
                context.TraceIdentifier,
                context.User?.Identity?.Name);
        }

        if (binding.Source == ApiParameterSource.Header)
        {
            return context.Request.Headers.TryGetValue(binding.Name, out var headerValue) && headerValue.Count > 0
                ? ApiParameterBindingValue.Present(headerValue[0])
                : ApiParameterBindingValue.Missing;
        }

        return context.Request.Query.TryGetValue(binding.Name, out var queryValue) && queryValue.Count > 0
            ? ApiParameterBindingValue.Present(queryValue[0])
            : ApiParameterBindingValue.Missing;
    }

    private static async Task<bool> TryWriteEventAsync(HttpContext context, ApiStreamingInvocationEvent item)
    {
        try
        {
            await context.Response.WriteAsync("event: ", context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync(item.Kind.ToString(), context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync("\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync("id: ", context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync(item.Sequence.ToString(CultureInfo.InvariantCulture), context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync("\n", context.RequestAborted).ConfigureAwait(false);
            await WriteDataLinesAsync(context, JsonSerializer.Serialize(item, EventJsonOptions)).ConfigureAwait(false);
            await context.Response.WriteAsync("\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WriteDataLinesAsync(HttpContext context, string json)
    {
        using var reader = new StringReader(json);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            await context.Response.WriteAsync("data: ", context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync(line, context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsync("\n", context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static JsonSerializerOptions CreateEventJsonOptions()
    {
        var options = new JsonSerializerOptions(ApiJsonOptions.Shared)
        {
            WriteIndented = false
        };
        return options;
    }
}
