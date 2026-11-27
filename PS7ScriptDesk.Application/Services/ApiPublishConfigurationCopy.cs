using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

/// <summary>Creates fully independent configuration snapshots through the supported JSON model.</summary>
public static class ApiPublishConfigurationCopy
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ApiPublishConfiguration Clone(ApiPublishConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Deserialize<ApiPublishConfiguration>(
                   JsonSerializer.Serialize(configuration, Options), Options)
               ?? throw new InvalidOperationException("API configuration copy could not be materialized.");
    }

    public static ApiEndpointConfiguration Clone(ApiEndpointConfiguration endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return JsonSerializer.Deserialize<ApiEndpointConfiguration>(
                   JsonSerializer.Serialize(endpoint, Options), Options)
               ?? throw new InvalidOperationException("API endpoint copy could not be materialized.");
    }

    public static ApiParameterBindingConfiguration Clone(ApiParameterBindingConfiguration binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return JsonSerializer.Deserialize<ApiParameterBindingConfiguration>(
                   JsonSerializer.Serialize(binding, Options), Options)
               ?? throw new InvalidOperationException("API parameter binding copy could not be materialized.");
    }
}
