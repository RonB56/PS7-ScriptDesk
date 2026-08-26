using System.Text.RegularExpressions;
using System.Windows;
using System.IO;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Shell.Services;

public sealed class RestApiPublishWizardService : IRestApiPublishWizardService
{
    private readonly IApiPublishConfigurationStore _configurationStore;
    private readonly Func<IApiLocalTestHostService> _localTestHostServiceFactory;
    private readonly Func<IApiBuildPublishService> _buildPublishServiceFactory;

    public RestApiPublishWizardService(
        IApiPublishConfigurationStore configurationStore,
        Func<IApiLocalTestHostService>? localTestHostServiceFactory = null,
        Func<IApiBuildPublishService>? buildPublishServiceFactory = null)
    {
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _localTestHostServiceFactory = localTestHostServiceFactory ?? (() => new ApiLocalTestHostService());
        _buildPublishServiceFactory = buildPublishServiceFactory ?? (() => new ApiBuildPublishService());
    }

    public ApiPublishConfiguration? ShowWizard(ApiPublishWizardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var diagnosticsScope = DeveloperDiagnostics.BeginScope(operationId: $"RestApiWizard-{Guid.NewGuid():N}");
        DeveloperDiagnostics.LogUserAction(
            "RestApiPublish",
            "OpenRestApiWizard",
            "Opening REST API publish wizard.",
            new Dictionary<string, object?>
            {
                ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath),
                ["scriptLength"] = request.ScriptContent.Length
            });

        var metadata = new PowerShellApiMetadataService().Analyze(request.ScriptContent, request.SourceScriptPath);
        var configuration = LoadOrCreateConfiguration(request, metadata);
        var dialog = new RestApiPublishWizardWindow(
            request,
            metadata,
            configuration,
            _configurationStore,
            _localTestHostServiceFactory(),
            _buildPublishServiceFactory())
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true || dialog.Configuration is null)
        {
            DeveloperDiagnostics.LogDecision(
                "RestApiPublish",
                "RestApiWizardClosed",
                "REST API publish wizard was closed without saving a configuration.",
                "Canceled",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath) });
            return null;
        }

        DeveloperDiagnostics.LogInfo(
            "RestApiPublish",
            "REST API publish wizard saved configuration.",
            new Dictionary<string, object?>
            {
                ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath),
                ["endpointCount"] = dialog.Configuration.Endpoints.Count
            });
        return dialog.Configuration;
    }

    internal ApiPublishConfiguration LoadOrCreateConfiguration(ApiPublishWizardRequest request, ApiMetadataResult metadata)
    {
        try
        {
            if (_configurationStore.ConfigurationExists(request.SourceScriptPath))
            {
                var existing = _configurationStore.Load(request.SourceScriptPath);
                if (string.IsNullOrWhiteSpace(existing.SourceScript))
                {
                    existing.SourceScript = Path.GetFileName(request.SourceScriptPath);
                }

                if (existing.Endpoints.Count == 0)
                {
                    existing.Endpoints = CreateDefaultEndpoints(metadata, existing.Api.DefaultRoutePrefix);
                }

                return existing;
            }
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "Existing REST API companion configuration could not be loaded; creating a fresh wizard configuration.",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath) });
        }

        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(request.SourceScriptPath);
        configuration.Api.Title = string.IsNullOrWhiteSpace(request.SuggestedApiName)
            ? configuration.Api.Title
            : request.SuggestedApiName;
        configuration.OpenApi.Title = configuration.Api.Title;
        configuration.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        configuration.Security.AllowNoAuthenticationForLocalTest = true;
        configuration.OpenApi.EnableSwaggerUiForLocalTest = true;
        configuration.Endpoints = CreateDefaultEndpoints(metadata, configuration.Api.DefaultRoutePrefix);
        return configuration;
    }

    internal static List<ApiEndpointConfiguration> CreateDefaultEndpoints(ApiMetadataResult metadata, string routePrefix)
    {
        var prefix = NormalizeRoutePrefix(routePrefix);
        return metadata.Functions
            .Where(function => function.IsPublishable)
            .Select(function =>
            {
                var route = $"{prefix}/{Slugify(function.Name)}";
                return new ApiEndpointConfiguration
                {
                    EndpointId = ApiEndpointConfiguration.CreateStableEndpointId(function.Name),
                    PowerShellFunctionName = function.Name,
                    DisplayName = function.Name,
                    Description = function.CommentHelp?.Synopsis ?? string.Empty,
                    RequiresAuthentication = false,
                    Rest =
                    {
                        Method = ApiHttpMethod.Get,
                        RouteTemplate = route,
                        OperationId = ToOperationId(function.Name),
                        Tags = ["PowerShell"],
                        IncludeInOpenApi = true
                    },
                    ParameterBindings = function.Parameters
                        .Select(parameter => new ApiParameterBindingConfiguration
                        {
                            PowerShellParameterName = parameter.Name,
                            Source = ApiParameterSource.Query,
                            Name = ToCamelCase(parameter.Name),
                            Required = parameter.MandatoryState == ApiParameterMandatoryState.Mandatory
                                ? ApiRequiredBehavior.Required
                                : ApiRequiredBehavior.Optional,
                            TypeName = parameter.DeclaredTypeName ?? (parameter.IsSwitch ? "bool" : "string"),
                            ArrayBinding = parameter.IsArray ? ApiArrayBindingBehavior.RepeatedValues : ApiArrayBindingBehavior.RepeatedValues
                        })
                        .ToList()
                };
            })
            .ToList();
    }

    private static string NormalizeRoutePrefix(string routePrefix)
    {
        var value = string.IsNullOrWhiteSpace(routePrefix) ? "/api" : routePrefix.Trim();
        value = "/" + value.Trim('/');
        return value == "/" ? "/api" : value;
    }

    private static string Slugify(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(cleaned) ? "endpoint" : cleaned;
    }

    private static string ToOperationId(string value)
    {
        var parts = Regex.Split(value.Trim(), @"[^A-Za-z0-9]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length == 0)
        {
            return "invokeEndpoint";
        }

        return char.ToLowerInvariant(parts[0][0]) + parts[0][1..] + string.Concat(parts.Skip(1).Select(ToPascalCase));
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "value";
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string ToPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}
