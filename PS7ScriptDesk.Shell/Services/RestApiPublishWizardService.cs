using System.Text.RegularExpressions;
using System.Windows;
using System.IO;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
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
        var loaded = LoadOrCreateConfiguration(request, metadata);
        var dialog = new RestApiPublishWizardWindow(
            request,
            metadata,
            loaded.Configuration,
            _configurationStore,
            _localTestHostServiceFactory(),
            _buildPublishServiceFactory())
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        dialog.SetReconciliation(loaded.Reconciliation);

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

    internal (ApiPublishConfiguration Configuration, ApiPublishConfigurationReconciliation Reconciliation) LoadOrCreateConfiguration(ApiPublishWizardRequest request, ApiMetadataResult metadata)
    {
        bool configurationExists;
        try
        {
            configurationExists = _configurationStore.ConfigurationExists(request.SourceScriptPath);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API companion configuration availability could not be checked; refusing to create a replacement configuration.",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath) });
            return CreateBlockedLoadResult(request, metadata);
        }

        if (configurationExists)
        {
            try
            {
                var existing = ApiPublishConfigurationCopy.Clone(_configurationStore.Load(request.SourceScriptPath));
                if (string.IsNullOrWhiteSpace(existing.SourceScript))
                {
                    existing.SourceScript = Path.GetFileName(request.SourceScriptPath);
                }

                var existingConfigurationReconciliation = new ApiPublishConfigurationReconciler().Reconcile(existing, metadata);
                return (existing, existingConfigurationReconciliation);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException(
                    "RestApiPublish",
                    ex,
                    "Existing REST API companion configuration could not be loaded or reconciled; refusing to create a replacement configuration.",
                    new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath) });
                return CreateBlockedLoadResult(request, metadata);
            }
        }

        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(request.SourceScriptPath);
        configuration.Api.Title = string.IsNullOrWhiteSpace(request.SuggestedApiName)
            ? configuration.Api.Title
            : request.SuggestedApiName;
        configuration.OpenApi.Title = configuration.Api.Title;
        configuration.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        configuration.Security.AllowNoAuthenticationForLocalTest = true;
        configuration.OpenApi.EnableSwaggerUiForLocalTest = true;
        var metadataIsComplete = ApiPublishConfigurationReconciler.IsMetadataComplete(metadata);
        configuration.Endpoints = metadataIsComplete
            ? CreateDefaultEndpoints(metadata, configuration.Api.DefaultRoutePrefix)
            : [];
        ApiAuthenticationIntentPolicy.Apply(configuration);
        var findings = metadataIsComplete
            ? Array.Empty<ApiPublishReconciliationFinding>()
            : new[] { new ApiPublishReconciliationFinding(ApiPublishReconciliationFindingKind.InvalidMetadata,
                metadata.ParsedSuccessfully
                    ? "One or more PowerShell parameter declarations have incomplete static metadata. Review the affected declarations before saving, testing, building, or publishing this API."
                    : "PowerShell metadata discovery failed. Fix the script before saving, testing, building, or publishing this API.") };
        var reconciliation = new ApiPublishConfigurationReconciliation(configuration, findings, metadataIsComplete);
        return (configuration, reconciliation);
    }

    private static (ApiPublishConfiguration Configuration, ApiPublishConfigurationReconciliation Reconciliation) CreateBlockedLoadResult(
        ApiPublishWizardRequest request,
        ApiMetadataResult metadata)
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(request.SourceScriptPath);
        var finding = new ApiPublishReconciliationFinding(
            ApiPublishReconciliationFindingKind.ConfigurationLoadFailure,
            "The saved API configuration could not be read or reconciled. The existing file was left untouched; fix or restore it before continuing.");
        return (configuration, new ApiPublishConfigurationReconciliation(
            configuration,
            [finding],
            ApiPublishConfigurationReconciler.IsMetadataComplete(metadata)));
    }

    internal static List<ApiEndpointConfiguration> CreateDefaultEndpoints(ApiMetadataResult metadata, string routePrefix)
    {
        return metadata.Functions
            .Where(function => function.IsPublishable)
            .Select(function => ApiPublishEndpointDefaults.Create(function, routePrefix))
            .ToList();
    }

}
