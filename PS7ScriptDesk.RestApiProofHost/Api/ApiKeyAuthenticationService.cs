using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public sealed class ApiKeyAuthenticationService
{
    public const string ApiKeyHeaderName = "X-API-Key";
    public const string ApiKeySecuritySchemeName = "ApiKeyAuth";

    public ApiAuthenticationResult AuthenticateEndpoint(HttpContext context, ApiEndpointConfiguration endpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoint);

        var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
        return IsEndpointAuthenticationRequired(configuration, endpoint)
            ? AuthenticateApiKey(context, configuration)
            : ApiAuthenticationResult.Success();
    }

    public ApiAuthenticationResult AuthenticateOpenApi(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configuration = context.RequestServices.GetRequiredService<ApiPublishConfiguration>();
        return IsOpenApiAuthenticationRequired(configuration)
            ? AuthenticateApiKey(context, configuration)
            : ApiAuthenticationResult.Success();
    }

    public static bool IsEndpointAuthenticationRequired(ApiPublishConfiguration configuration, ApiEndpointConfiguration endpoint)
        => configuration.Security.Mode == ApiSecurityMode.ApiKey && endpoint.RequiresAuthentication;

    public static bool IsOpenApiAuthenticationRequired(ApiPublishConfiguration configuration)
        => configuration.Security.Mode == ApiSecurityMode.ApiKey &&
           configuration.OpenApi.RequireAuthenticationForPublishedSwagger;

    private static ApiAuthenticationResult AuthenticateApiKey(HttpContext context, ApiPublishConfiguration configuration)
    {
        var variableName = configuration.Security.ApiKeyEnvironmentVariableName;
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return ApiAuthenticationResult.ServiceUnavailable("API key authentication is not configured.");
        }

        var expectedKey = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(expectedKey))
        {
            return ApiAuthenticationResult.ServiceUnavailable("API key authentication is not configured.");
        }

        var suppliedKey = ReadSuppliedApiKey(context);
        if (string.IsNullOrEmpty(suppliedKey))
        {
            return ApiAuthenticationResult.Unauthorized("API key credentials are required.");
        }

        if (!FixedTimeEquals(suppliedKey, expectedKey))
        {
            return ApiAuthenticationResult.Unauthorized("API key credentials are invalid.");
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ApiKey")],
            authenticationType: ApiKeySecuritySchemeName));
        return ApiAuthenticationResult.Success();
    }

    private static string? ReadSuppliedApiKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerValues) &&
            headerValues.Count > 0 &&
            !string.IsNullOrWhiteSpace(headerValues[0]))
        {
            return headerValues[0];
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var authorizationValues) &&
            authorizationValues.Count > 0)
        {
            var authorization = authorizationValues[0]?.Trim();
            const string bearerPrefix = "Bearer ";
            const string apiKeyPrefix = "ApiKey ";
            if (!string.IsNullOrWhiteSpace(authorization) &&
                authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return authorization[bearerPrefix.Length..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(authorization) &&
                authorization.StartsWith(apiKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return authorization[apiKeyPrefix.Length..].Trim();
            }
        }

        return null;
    }

    private static bool FixedTimeEquals(string suppliedKey, string expectedKey)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }
}

public sealed record ApiAuthenticationResult(bool IsSuccess, int StatusCode, string Title, string Detail)
{
    public static ApiAuthenticationResult Success()
        => new(true, StatusCodes.Status200OK, string.Empty, string.Empty);

    public static ApiAuthenticationResult Unauthorized(string detail)
        => new(false, StatusCodes.Status401Unauthorized, "Unauthorized.", detail);

    public static ApiAuthenticationResult Forbidden(string detail)
        => new(false, StatusCodes.Status403Forbidden, "Forbidden.", detail);

    public static ApiAuthenticationResult ServiceUnavailable(string detail)
        => new(false, StatusCodes.Status503ServiceUnavailable, "API authentication unavailable.", detail);

    public IResult ToResult(HttpContext context)
    {
        var details = new ProblemDetails
        {
            Type = StatusCode == StatusCodes.Status401Unauthorized
                ? "https://ps7scriptdesk.local/errors/authentication-required"
                : StatusCode == StatusCodes.Status403Forbidden
                    ? "https://ps7scriptdesk.local/errors/access-denied"
                    : "https://ps7scriptdesk.local/errors/authentication-unavailable",
            Title = Title,
            Status = StatusCode,
            Detail = Detail,
            Instance = context.Request.Path.Value
        };
        details.Extensions["requestId"] = context.TraceIdentifier;

        var result = Results.Json(
            details,
            statusCode: StatusCode,
            options: ApiJsonOptions.Shared,
            contentType: "application/problem+json");

        return StatusCode == StatusCodes.Status401Unauthorized
            ? result.WithHeader("WWW-Authenticate", "ApiKey")
            : result;
    }
}

internal static class ResultHeaderExtensions
{
    public static IResult WithHeader(this IResult result, string name, string value)
        => new HeaderResult(result, name, value);

    private sealed class HeaderResult(IResult inner, string name, string value) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[name] = value;
            await inner.ExecuteAsync(httpContext);
        }
    }
}
