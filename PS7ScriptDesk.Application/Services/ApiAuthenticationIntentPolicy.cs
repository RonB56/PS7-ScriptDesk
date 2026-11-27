using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

/// <summary>Resolves persisted endpoint authentication intent against the selected global mode.</summary>
public static class ApiAuthenticationIntentPolicy
{
    public static ApiAuthenticationIntentResult Apply(ApiPublishConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var legacyEndpointsProtected = new List<string>();
        foreach (var endpoint in configuration.Endpoints)
        {
            if (endpoint.AuthenticationRequirementIsDefaulted == true)
            {
                endpoint.RequiresAuthentication = configuration.Security.Mode == ApiSecurityMode.ApiKey;
            }
            else if (endpoint.AuthenticationRequirementIsDefaulted is null &&
                     configuration.Security.Mode == ApiSecurityMode.ApiKey &&
                     !endpoint.RequiresAuthentication)
            {
                // Legacy false has no trustworthy user-intent marker. Fail closed and retain
                // the unknown marker so subsequent mode transitions remain conservative.
                endpoint.RequiresAuthentication = true;
                legacyEndpointsProtected.Add(endpoint.EndpointId);
            }
        }

        return new ApiAuthenticationIntentResult(legacyEndpointsProtected);
    }
}

public sealed record ApiAuthenticationIntentResult(IReadOnlyList<string> LegacyEndpointsProtected);
