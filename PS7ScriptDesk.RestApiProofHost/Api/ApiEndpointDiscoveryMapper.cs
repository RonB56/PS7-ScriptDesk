using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class ApiEndpointDiscoveryMapper
{
    public const string DiscoveryRoute = "/api/endpoints";

    public static void MapEndpointDiscovery(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(DiscoveryRoute, (HttpContext context) =>
        {
            var authentication = context.RequestServices.GetRequiredService<ApiKeyAuthenticationService>();
            var authenticationResult = authentication.AuthenticateOpenApi(context);
            if (!authenticationResult.IsSuccess)
            {
                return authenticationResult.ToResult(context);
            }

            var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
            var endpoints = configuration.Endpoints
                .Where(endpoint => endpoint.IsEnabled)
                .Select(endpoint =>
                {
                    var transport = ApiTransportFacts.ResolveEndpointTransport(configuration, endpoint);
                    return new
                    {
                        endpointId = endpoint.EndpointId,
                        functionName = endpoint.PowerShellFunctionName,
                        displayName = string.IsNullOrWhiteSpace(endpoint.DisplayName) ? endpoint.PowerShellFunctionName : endpoint.DisplayName,
                        transport = transport.ToString(),
                        transportDisplayName = ApiTransportFacts.GetDisplayName(transport),
                        method = ApiTransportFacts.GetEndpointMethod(endpoint, transport),
                        path = ApiTransportFacts.GetEndpointPath(endpoint, transport),
                        requiresAuthentication = ApiKeyAuthenticationService.IsEndpointAuthenticationRequired(configuration, endpoint),
                        openApi = transport == ApiTransport.Rest && endpoint.Rest.IncludeInOpenApi,
                        streaming = ApiTransportFacts.IsStreaming(transport)
                    };
                })
                .OrderBy(endpoint => endpoint.transport, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.path, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.endpointId, StringComparer.Ordinal)
                .ToArray();

            return Results.Json(
                new
                {
                    schemaVersion = configuration.SchemaVersion,
                    title = string.IsNullOrWhiteSpace(configuration.Api.Title) ? "PowerShell API" : configuration.Api.Title,
                    endpoints
                },
                options: ApiJsonOptions.Shared,
                contentType: "application/json");
        });
    }
}
