using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.PowerShell;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.RestApiProofHost.Hosting;

public static class RestApiProofHostFactory
{
    public static System.Text.Json.JsonSerializerOptions JsonOptions => ApiJsonOptions.Shared;

    public static int ResolvePortFromEnvironment(int defaultPort)
        => int.TryParse(Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_REST_POC_PORT"), out var port) && port is > 0 and <= 65535
            ? port
            : defaultPort;

    public static async Task<RunningRestApiProofHost> StartAsync(RestApiProofHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var contentRoot = ResolveContentRoot(options.ContentRootPath);
        var configuration = LoadConfiguration(contentRoot, options.ConfigurationRelativePath);
        var scriptPath = Path.Combine(contentRoot, "Scripts", configuration.SourceScript);
        var metadata = LoadStaticMetadata(scriptPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            ApplicationName = typeof(RestApiProofHostFactory).Assembly.FullName
        });
        builder.WebHost.UseUrls(RequireLocalhostUrl(options.Url));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = Math.Max(1, configuration.Runtime.RequestBodySizeLimitBytes);
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(metadata);
        builder.Services.AddSingleton<ApiKeyAuthenticationService>();
        builder.Services.AddSingleton(RestParameterBinder.Shared);
        builder.Services.AddSingleton<OpenApiDocumentBuilder>();
        builder.Services.AddSingleton(PowerShellResultNormalizer.Shared);
        builder.Services.AddSingleton<RunspacePoolManager>();
        builder.Services.AddSingleton<IPowerShellFunctionInvoker, PowerShellFunctionInvoker>();
        builder.Services.AddSingleton<PowerShellInvocationCoordinator>();

        var app = builder.Build();
        app.MapGet("/healthz", () => Results.Json(new { status = "Ready" }, ApiJsonOptions.Shared));
        OpenApiEndpointMapper.MapOpenApiEndpoints(app);
        RestEndpointMapper.MapConfiguredEndpoints(app);

        var coordinator = app.Services.GetRequiredService<PowerShellInvocationCoordinator>();
        try
        {
            await coordinator.InitializeAsync(
                scriptPath,
                configuration.Endpoints.Select(endpoint => endpoint.PowerShellFunctionName),
                configuration.Runtime,
                cancellationToken);

            await app.StartAsync(cancellationToken);
            var address = ResolveStartedAddress(app);
            return new RunningRestApiProofHost(app, coordinator, configuration, address);
        }
        catch
        {
            await coordinator.DisposeAsync();
            await app.DisposeAsync();
            throw;
        }
    }

    private static ApiMetadataResult LoadStaticMetadata(string scriptPath)
    {
        var source = File.ReadAllText(scriptPath);
        return new PowerShellApiMetadataService().Analyze(source);
    }

    private static ApiPublishConfiguration LoadConfiguration(string contentRoot, string? relativeConfigurationPath)
    {
        var configurationPath = ResolveConfigurationPath(contentRoot, relativeConfigurationPath);
        using var stream = File.OpenRead(configurationPath);
        return System.Text.Json.JsonSerializer.Deserialize<ApiPublishConfiguration>(stream, ApiJsonOptions.Shared)
               ?? throw new InvalidDataException("The proof host API configuration is empty.");
    }

    private static string ResolveConfigurationPath(string contentRoot, string? relativeConfigurationPath)
    {
        var relativePath = string.IsNullOrWhiteSpace(relativeConfigurationPath)
            ? Path.Combine("Config", "TestApi.ps7api.json")
            : relativeConfigurationPath;
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("The REST API configuration path must be relative to the content root.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The REST API configuration path escapes the content root.");
        }

        return path;
    }

    private static Uri ResolveStartedAddress(WebApplication app)
    {
        var addressFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = addressFeature?.Addresses.FirstOrDefault()
                      ?? app.Urls.FirstOrDefault()
                      ?? throw new InvalidOperationException("The proof host did not report a listening address.");
        return new Uri(address.Replace("0.0.0.0", "127.0.0.1", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveContentRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDirectory, "Scripts", "TestApi.ps1")) &&
            File.Exists(Path.Combine(baseDirectory, "Config", "TestApi.ps7api.json")))
        {
            return baseDirectory;
        }

        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost");
            if (File.Exists(Path.Combine(candidate, "Scripts", "TestApi.ps1")) &&
                File.Exists(Path.Combine(candidate, "Config", "TestApi.ps7api.json")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return baseDirectory;
    }

    private static string RequireLocalhostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("The Phase 3 proof host only accepts explicit localhost HTTP URLs such as http://127.0.0.1:5087.");
        }

        return uri.ToString();
    }

}

public sealed class RestApiProofHostOptions
{
    public string Url { get; init; } = "http://127.0.0.1:5087";
    public string? ContentRootPath { get; init; }
    public string? ConfigurationRelativePath { get; init; }
}

public sealed class RunningRestApiProofHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly PowerShellInvocationCoordinator _coordinator;
    private bool _disposed;

    public RunningRestApiProofHost(
        WebApplication app,
        PowerShellInvocationCoordinator coordinator,
        ApiPublishConfiguration configuration,
        Uri baseAddress)
    {
        _app = app;
        _coordinator = coordinator;
        Configuration = configuration;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }
    public ApiPublishConfiguration Configuration { get; }
    public bool RequiredFunctionsVerified => _coordinator.RequiredFunctionsVerified;
    public bool IsPowerShellDisposed => _coordinator.IsDisposed;
    public bool IsDisposed => _disposed;
    public PowerShellInvocationMetricsSnapshot Metrics => _coordinator.CreateMetricsSnapshot();

    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        => _app.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _coordinator.DisposeAsync();
        await _app.DisposeAsync();
        _disposed = true;
    }
}
