using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public sealed class ApiEndpointResolver
{
    public static ApiEndpointResolver Shared { get; } = new();

    public ApiEndpointResolutionResult ResolveByEndpointId(
        ApiPublishConfiguration configuration,
        string? endpointId,
        ApiTransport? requiredTransport = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return ApiEndpointResolutionResult.Failure(
                "MissingEndpointId",
                "A published endpoint ID is required.");
        }

        var matches = configuration.Endpoints
            .Where(endpoint => endpoint.IsEnabled &&
                               string.Equals(endpoint.EndpointId, endpointId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                               (requiredTransport is null || ApiTransportFacts.ResolveEndpointTransport(configuration, endpoint) == requiredTransport.Value))
            .ToList();

        return matches.Count switch
        {
            1 => ApiEndpointResolutionResult.Success(matches[0]),
            > 1 => ApiEndpointResolutionResult.Failure(
                "DuplicateEndpointId",
                "The published endpoint configuration is ambiguous."),
            _ => ApiEndpointResolutionResult.Failure(
                "EndpointNotFound",
                "The published endpoint was not found.")
        };
    }
}

public sealed record ApiEndpointResolutionResult(
    bool IsSuccess,
    ApiEndpointConfiguration? Endpoint,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ApiEndpointResolutionResult Success(ApiEndpointConfiguration endpoint)
        => new(true, endpoint, null, null);

    public static ApiEndpointResolutionResult Failure(string errorCode, string errorMessage)
        => new(false, null, errorCode, errorMessage);
}
