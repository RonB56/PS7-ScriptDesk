using System.Text.Encodings.Web;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class OpenApiEndpointMapper
{
    public const string OpenApiJsonRoute = "/openapi/v1.json";
    public const string SwaggerRoute = "/swagger";
    public const string SwaggerIndexRoute = "/swagger/index.html";

    public static void MapOpenApiEndpoints(WebApplication app)
    {
        var configuration = app.Services.GetRequiredService<ApiPublishConfiguration>();
        if (!configuration.OpenApi.IsEnabled)
        {
            return;
        }

        app.MapGet(OpenApiJsonRoute, (HttpContext context) =>
        {
            var authentication = context.RequestServices.GetRequiredService<ApiKeyAuthenticationService>();
            var authenticationResult = authentication.AuthenticateOpenApi(context);
            if (!authenticationResult.IsSuccess)
            {
                return authenticationResult.ToResult(context);
            }

            var builder = context.RequestServices.GetRequiredService<OpenApiDocumentBuilder>();
            var metadata = context.RequestServices.GetRequiredService<ApiMetadataResult>();
            var document = builder.Build(configuration, metadata);
            return Results.Json(
                document,
                options: ApiJsonOptions.Shared,
                contentType: "application/json");
        });

        if (!ShouldExposeSwaggerUi(configuration))
        {
            return;
        }

        app.MapGet(SwaggerRoute, (HttpContext context) => OpenSwaggerViewer(context));
        app.MapGet(SwaggerIndexRoute, (HttpContext context) => OpenSwaggerViewer(context));
    }

    public static bool ShouldExposeSwaggerUi(ApiPublishConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.OpenApi.IsEnabled)
        {
            return false;
        }

        return configuration.Security.Mode == ApiSecurityMode.LocalTestNoAuthentication &&
               configuration.Security.AllowNoAuthenticationForLocalTest
            ? configuration.OpenApi.EnableSwaggerUiForLocalTest
            : configuration.OpenApi.EnableSwaggerUiForPublishedApi;
    }

    private static IResult OpenSwaggerViewer(HttpContext context)
    {
        var authentication = context.RequestServices.GetRequiredService<ApiKeyAuthenticationService>();
        var authenticationResult = authentication.AuthenticateOpenApi(context);
        return authenticationResult.IsSuccess
            ? Results.Content(BuildSwaggerHtml(OpenApiJsonRoute), "text/html; charset=utf-8")
            : authenticationResult.ToResult(context);
    }

    private static string BuildSwaggerHtml(string openApiJsonRoute)
    {
        var encodedRoute = HtmlEncoder.Default.Encode(openApiJsonRoute);
        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>PS7 ScriptDesk REST API</title>
  <style>
    :root { color-scheme: light dark; font-family: "Segoe UI", sans-serif; }
    body { margin: 0; background: #f6f8fa; color: #1f2328; }
    main { max-width: 1080px; margin: 0 auto; padding: 28px; }
    h1 { margin: 0 0 6px; font-size: 28px; }
    a { color: #0f5f9e; }
    .summary { margin: 0 0 22px; color: #57606a; }
    .endpoint { background: #ffffff; border: 1px solid #d0d7de; border-radius: 6px; margin: 10px 0; padding: 14px; }
    .method { display: inline-block; min-width: 58px; margin-right: 10px; font-weight: 700; text-transform: uppercase; color: #0969da; }
    .route { font-family: Consolas, monospace; }
    pre { white-space: pre-wrap; background: #0d1117; color: #e6edf3; border-radius: 6px; padding: 14px; overflow: auto; }
    @media (prefers-color-scheme: dark) {
      body { background: #0d1117; color: #e6edf3; }
      .summary { color: #8b949e; }
      .endpoint { background: #161b22; border-color: #30363d; }
      a { color: #58a6ff; }
    }
  </style>
</head>
<body>
  <main>
    <h1>PS7 ScriptDesk REST API</h1>
    <p class="summary">Offline OpenAPI viewer. <a href="{{encodedRoute}}">Open raw OpenAPI JSON</a>.</p>
    <section id="endpoint-list">Loading OpenAPI document...</section>
    <h2>OpenAPI JSON</h2>
    <pre id="openapi-json"></pre>
  </main>
  <script>
    const endpointList = document.getElementById("endpoint-list");
    const rawJson = document.getElementById("openapi-json");
    fetch("{{encodedRoute}}")
      .then(response => response.json())
      .then(openApiDocument => {
        rawJson.textContent = JSON.stringify(openApiDocument, null, 2);
        const paths = openApiDocument.paths || {};
        const entries = [];
        for (const route of Object.keys(paths).sort()) {
          for (const method of Object.keys(paths[route]).sort()) {
            const operation = paths[route][method] || {};
            entries.push({ route, method, summary: operation.summary || operation.operationId || "" });
          }
        }
        endpointList.textContent = "";
        if (entries.length === 0) {
          endpointList.textContent = "No endpoints are included in the OpenAPI document.";
          return;
        }
        for (const entry of entries) {
          const article = document.createElement("article");
          article.className = "endpoint";
          const method = document.createElement("span");
          method.className = "method";
          method.textContent = entry.method;
          const route = document.createElement("span");
          route.className = "route";
          route.textContent = entry.route;
          const summary = document.createElement("p");
          summary.textContent = entry.summary;
          article.append(method, route, summary);
          endpointList.appendChild(article);
        }
      })
      .catch(error => {
        endpointList.textContent = "OpenAPI document could not be loaded.";
        rawJson.textContent = String(error);
      });
  </script>
</body>
</html>
""";
    }
}
