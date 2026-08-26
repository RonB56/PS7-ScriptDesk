using System.Globalization;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public sealed class RestParameterBinder
{
    public static RestParameterBinder Shared { get; } = new();

    private readonly ApiEndpointParameterBinder _endpointBinder;

    public RestParameterBinder()
        : this(ApiEndpointParameterBinder.Shared)
    {
    }

    public RestParameterBinder(ApiEndpointParameterBinder endpointBinder)
    {
        _endpointBinder = endpointBinder ?? throw new ArgumentNullException(nameof(endpointBinder));
    }

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

            return RestParameterBindingResult.FromApiResult(
                _endpointBinder.Bind(endpoint, binding => ResolveBindingValue(context, body, binding)));
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

    private static ApiParameterBindingValue ResolveBindingValue(HttpContext context, JsonDocument? body, ApiParameterBindingConfiguration binding)
    {
        switch (binding.Source)
        {
            case ApiParameterSource.Query:
                return context.Request.Query.TryGetValue(binding.Name, out var queryValue) && queryValue.Count > 0
                    ? ApiParameterBindingValue.Present(queryValue[0])
                    : ApiParameterBindingValue.Missing;
            case ApiParameterSource.Route:
                return context.Request.RouteValues.TryGetValue(binding.Name, out var routeValue)
                    ? ApiParameterBindingValue.Present(Convert.ToString(routeValue, CultureInfo.InvariantCulture))
                    : ApiParameterBindingValue.Missing;
            case ApiParameterSource.Header:
                return context.Request.Headers.TryGetValue(binding.Name, out var headerValue) && headerValue.Count > 0
                    ? ApiParameterBindingValue.Present(headerValue[0])
                    : ApiParameterBindingValue.Missing;
            case ApiParameterSource.Body:
                return body is not null && body.RootElement.TryGetProperty(binding.Name, out var property)
                    ? ApiParameterBindingValue.Present(property.Clone())
                    : ApiParameterBindingValue.Missing;
            case ApiParameterSource.ServerDefined:
                return ResolveServerDefinedValue(context, binding.ServerValue);
            default:
                return ApiParameterBindingValue.Missing;
        }
    }

    private static ApiParameterBindingValue ResolveServerDefinedValue(HttpContext context, ApiServerDefinedValue? serverValue)
        => ApiEndpointParameterBinder.ResolveServerDefinedValue(
            serverValue,
            context.TraceIdentifier,
            context.User?.Identity?.Name);
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

    public static RestParameterBindingResult FromApiResult(ApiParameterBindingResult result)
        => new(result.IsValid, result.Parameters, result.ErrorCode, result.ErrorMessage);
}
