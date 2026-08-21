using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.RestApiProofHost.Hosting;

public static class RestApiProofHostFactory
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static int ResolvePortFromEnvironment(int defaultPort)
        => int.TryParse(Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_REST_POC_PORT"), out var port) && port is > 0 and <= 65535
            ? port
            : defaultPort;

    public static async Task<RunningRestApiProofHost> StartAsync(RestApiProofHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var contentRoot = ResolveContentRoot(options.ContentRootPath);
        var configuration = LoadConfiguration(contentRoot);
        var scriptPath = Path.Combine(contentRoot, "Scripts", configuration.SourceScript);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            ApplicationName = typeof(RestApiProofHostFactory).Assembly.FullName
        });
        builder.WebHost.UseUrls(RequireLocalhostUrl(options.Url));
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(RestParameterBinder.Shared);
        builder.Services.AddSingleton(PowerShellResultNormalizer.Shared);
        builder.Services.AddSingleton<RunspacePoolManager>();
        builder.Services.AddSingleton<IPowerShellFunctionInvoker, PowerShellFunctionInvoker>();
        builder.Services.AddSingleton<PowerShellInvocationCoordinator>();

        var app = builder.Build();
        RestEndpointMapper.MapConfiguredEndpoints(app);

        var coordinator = app.Services.GetRequiredService<PowerShellInvocationCoordinator>();
        await coordinator.InitializeAsync(
            scriptPath,
            configuration.Endpoints.Select(endpoint => endpoint.PowerShellFunctionName),
            configuration.Runtime,
            cancellationToken);

        await app.StartAsync(cancellationToken);
        var address = ResolveStartedAddress(app);
        return new RunningRestApiProofHost(app, coordinator, configuration, address);
    }

    private static ApiPublishConfiguration LoadConfiguration(string contentRoot)
    {
        var configurationPath = Path.Combine(contentRoot, "Config", "TestApi.ps7api.json");
        using var stream = File.OpenRead(configurationPath);
        return JsonSerializer.Deserialize<ApiPublishConfiguration>(stream, JsonOptions)
               ?? throw new InvalidDataException("The proof host API configuration is empty.");
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class RestApiProofHostOptions
{
    public string Url { get; init; } = "http://127.0.0.1:5087";
    public string? ContentRootPath { get; init; }
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
