using System.Collections.Generic;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Composition
{
    public static class AppBootstrapper
    {
        public static MainWindow CreateMainWindow(ApplicationSettingsService applicationSettingsService, ApplicationSettings applicationSettings, PowerShellRuntimeInfo? startupRuntimeInfo, IUiScaleService? uiScaleService = null)
        {
            TerminalStartupTrace.Write("BOOTSTRAPPER_CREATE_MAINWINDOW_ENTER", $"runtime={startupRuntimeInfo?.LaunchExecutablePath ?? "(none)"}");
            StartupLifecycleTrace.Write("AppBootstrapper.CreateMainWindow", "ENTER", $"runtime={startupRuntimeInfo?.LaunchExecutablePath ?? "(none)"}; structuredExecution={Environment.GetEnvironmentVariable(EditorExecutionFeatureGate.EnvironmentVariableName) ?? "(unset)"}");
            uiScaleService ??= new UiScaleService(applicationSettings.UiScalePercent);
            UiScaleServiceHost.SetCurrent(uiScaleService);
            var workspaceService = new WorkspaceService();
            var fileDocumentService = new FileDocumentService();
            var documentRecoveryService = new DocumentRecoveryService();
            var workspaceFolderService = new WorkspaceFolderService();
            var gitCommandRunner = new GitCommandRunner();
            var gitRepositoryLocator = new GitRepositoryLocator(gitCommandRunner);
            var gitService = new GitService(gitCommandRunner, gitRepositoryLocator);
            var gitWorkspaceCoordinator = new GitWorkspaceCoordinator(gitService);
            var userPromptService = new UserPromptService();
            var psesOwnedSessionFeatureGate = PsesOwnedInteractiveSessionFeatureGate.FromEnvironment();
            var experimentalRunFileBridgeFeatureGate = ExperimentalRunFileBridgeFeatureGate.FromEnvironment();
            var experimentalRunSelectionBridgeFeatureGate = ExperimentalRunSelectionBridgeFeatureGate.FromEnvironment();
            var psesOwnedSessionGateValue = Environment.GetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName);
            ILiveConsoleService liveConsoleService = psesOwnedSessionFeatureGate.IsEnabled
                ? new PsesOwnedInteractiveSessionService(experimentalRunFileBridgeFeatureGate, experimentalRunSelectionBridgeFeatureGate)
                : new LiveConsoleService();
            DeveloperDiagnostics.LogInfo(
                "Startup",
                "Interactive session ownership path selected.",
                new Dictionary<string, object?>
                {
                    ["psesOwnedInteractiveSession"] = psesOwnedSessionFeatureGate.IsEnabled,
                    ["featureGate"] = PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName,
                    ["featureGateSource"] = "process-environment",
                    ["featureGateValuePresent"] = !string.IsNullOrWhiteSpace(psesOwnedSessionGateValue),
                    ["experimentalDirectRunFileBridge"] = psesOwnedSessionFeatureGate.IsEnabled && experimentalRunFileBridgeFeatureGate.IsEnabled,
                    ["experimentalRunFileFeatureGate"] = ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName,
                    ["directRunSelectionEnabled"] = psesOwnedSessionFeatureGate.IsEnabled && experimentalRunSelectionBridgeFeatureGate.IsEnabled,
                    ["experimentalRunSelectionFeatureGate"] = ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName,
                    ["provider"] = liveConsoleService.GetType().Name,
                    ["expectedDebuggerBackend"] = psesOwnedSessionFeatureGate.IsEnabled ? "PsesDapDebugSession" : "PsesDebugSession",
                    ["fallbackPolicy"] = psesOwnedSessionFeatureGate.IsEnabled ? "NoSilentLegacyFallback" : "LegacyByExplicitSelection"
                });
            var runtimeSessionCoordinator = new PowerShellRuntimeSessionCoordinator(liveConsoleService);
            TerminalStartupTrace.Write("LIVE_CONSOLE_SERVICE_CONSTRUCTED", $"serviceId={liveConsoleService.GetHashCode():X8}");
            var exeExportService = new ExeExportService();
            var exeExportWizardService = new ExportWizardService(applicationSettings);
            var restApiPublishWizardService = new RestApiPublishWizardService(new ApiPublishConfigurationStore());
            var runtimeService = new RuntimeService(applicationSettings.SelectedRuntimeExecutablePath);
            var structuredExecutionFeatureGate = EditorExecutionFeatureGate.FromEnvironment();
            var effectiveExecutionFeatureGate = structuredExecutionFeatureGate;
            var structuredAvailability = structuredExecutionFeatureGate.IsStructuredExecutionEnabled
                ? ExecutionBackendAvailability.InitializationFailed
                : ExecutionBackendAvailability.Unavailable;
            IEditorExecutionAdapter? editorExecutionAdapter = null;
            IEditorExecutionBackend? structuredBackend = null;
            if (structuredExecutionFeatureGate.IsStructuredExecutionEnabled)
            {
                StructuredExecutionStartupResult structuredStartup;
                structuredStartup = TryInitializeStructuredExecution(() =>
                {
                    if (!psesOwnedSessionFeatureGate.IsEnabled)
                    {
                        throw new InvalidOperationException("Editor execution requires the PSES-owned interactive session; an independent editor runspace is not created.");
                    }

                    if (liveConsoleService is not IAuthoritativeSessionStructuredExecution authoritativeSession)
                    {
                        throw new InvalidOperationException("The PSES-owned interactive service does not expose the authoritative structured execution contract.");
                    }

                    return new AuthoritativeSessionStructuredExecutionAdapter(authoritativeSession, structuredExecutionFeatureGate);
                });
                editorExecutionAdapter = structuredStartup.Adapter;
                if (structuredStartup.IsAvailable)
                {
                    structuredAvailability = ExecutionBackendAvailability.Available;
                    structuredBackend = new AuthoritativeSessionStructuredExecutionBackend(editorExecutionAdapter!);
                }
                else
                {
                    structuredAvailability = structuredStartup.Failure is PowerShellRuntimeCompatibilityException
                        ? ExecutionBackendAvailability.RuntimeIncompatible
                        : ExecutionBackendAvailability.InitializationFailed;
                    effectiveExecutionFeatureGate = new EditorExecutionFeatureGate(false);
                    var failure = structuredStartup.Failure ?? new InvalidOperationException("Structured execution initialization returned no adapter and no exception.");
                    DeveloperDiagnostics.LogException(
                        "Startup",
                        failure,
                        "Editor execution was requested but the PSES-owned bridge backend is unavailable. No independent runspace or console-injection fallback was created.",
                        new Dictionary<string, object?>
                        {
                            ["structuredExecutionRequested"] = true,
                            ["structuredExecutionAvailable"] = false,
                            ["effectiveBackend"] = "none",
                            ["legacyBackendAvailable"] = false,
                            ["fallbackScope"] = "none"
                        });
                }
            }
            var executionBackends = new List<IEditorExecutionBackend>();
            if (structuredBackend is not null)
            {
                executionBackends.Add(structuredBackend);
            }
            var executionCoordinator = new EditorExecutionCoordinator(
                executionBackends,
                structuredExecutionFeatureGate,
                shadowMode: false,
                structuredAvailability,
                new PowerShellExecutionRequestClassifier(),
                experimentalRunSelectionBridgeFeatureGate);
            DeveloperDiagnostics.LogInfo(
                "Startup",
                    "Canonical execution coordinator composed with production dispatch enabled.",
                new Dictionary<string, object?>
                {
                    ["structuredExecutionEnabled"] = structuredExecutionFeatureGate.IsStructuredExecutionEnabled,
                    ["structuredExecutionEffective"] = effectiveExecutionFeatureGate.IsStructuredExecutionEnabled,
                    ["structuredExecutionAvailable"] = editorExecutionAdapter is not null,
                    ["directRunSelectionDefault"] = experimentalRunSelectionBridgeFeatureGate.IsEnabled,
                    ["backendCount"] = executionBackends.Count,
                    ["shadowMode"] = false
                });
            DeveloperDiagnostics.ConfigureFromSettings(applicationSettings, "AppBootstrapper loaded settings");
            ExecutionCorrelationTelemetry.Shared.LogActivation();
            DeveloperDiagnostics.LogInfo(
                "Startup",
                "AppBootstrapper loaded application settings and is creating MainWindow.",
                new Dictionary<string, object?>
                {
                    ["settingsPath"] = applicationSettingsService.SettingsFilePath,
                    ["developerDiagnosticsEnabled"] = applicationSettings.IsDeveloperDiagnosticsEnabled
                });

            var viewModel = new MainWindowViewModel(
                workspaceService,
                runtimeService,
                fileDocumentService,
                workspaceFolderService,
                userPromptService,
                liveConsoleService,
                exeExportService,
                applicationSettings,
                startupRuntimeInfo,
                exeExportWizardService,
                restApiPublishWizardService,
                uiScaleService,
                documentRecoveryService,
                editorExecutionAdapter,
                effectiveExecutionFeatureGate,
                new InteractiveTerminalCoordinator(),
                new TerminalOutputMultiplexer(),
                gitService,
                userPromptService,
                gitWorkspaceCoordinator,
                executionCoordinator,
                runtimeSessionCoordinator);
            StartupLifecycleTrace.Write("MainWindowViewModel", "CONSTRUCTED", $"terminalServiceId={liveConsoleService.GetHashCode():X8}; gitServiceId={gitService.GetHashCode():X8}");

            var window = new MainWindow(applicationSettingsService, applicationSettings, uiScaleService, liveConsoleService, runtimeSessionCoordinator);
            window.AttachViewModel(viewModel);
            TerminalStartupTrace.Write("BOOTSTRAPPER_CREATE_MAINWINDOW_EXIT", $"windowId={window.GetHashCode():X8}; viewModelId={viewModel.GetHashCode():X8}; serviceId={liveConsoleService.GetHashCode():X8}");
            StartupLifecycleTrace.Write("AppBootstrapper.CreateMainWindow", "EXIT", "ViewModel attached.");

            DeveloperDiagnostics.LogInfo("Startup", "MainWindow instance created and view model attached.");
            return window;
        }

        internal static StructuredExecutionStartupResult TryInitializeStructuredExecution(Func<IEditorExecutionAdapter> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            try
            {
                return new StructuredExecutionStartupResult(factory(), null);
            }
            catch (Exception ex)
            {
                return new StructuredExecutionStartupResult(null, ex);
            }
        }
    }

    internal sealed record StructuredExecutionStartupResult(IEditorExecutionAdapter? Adapter, Exception? Failure)
    {
        public bool IsAvailable => Adapter is not null && Failure is null;
    }
}
