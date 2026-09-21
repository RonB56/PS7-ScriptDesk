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
            var liveConsoleService = new LiveConsoleService();
            TerminalStartupTrace.Write("LIVE_CONSOLE_SERVICE_CONSTRUCTED", $"serviceId={liveConsoleService.GetHashCode():X8}");
            var exeExportService = new ExeExportService();
            var exeExportWizardService = new ExportWizardService(applicationSettings);
            var restApiPublishWizardService = new RestApiPublishWizardService(new ApiPublishConfigurationStore());
            var runtimeService = new RuntimeService(applicationSettings.SelectedRuntimeExecutablePath);
            var structuredExecutionFeatureGate = EditorExecutionFeatureGate.FromEnvironment();
            IEditorExecutionAdapter? editorExecutionAdapter = null;
            if (structuredExecutionFeatureGate.IsStructuredExecutionEnabled)
            {
                var broker = PersistentPowerShellSessionBroker
                    .CreateAsync("Structured editor PowerShell broker")
                    .GetAwaiter()
                    .GetResult();
                editorExecutionAdapter = new StructuredEditorExecutionAdapter(broker, structuredExecutionFeatureGate);
            }
            DeveloperDiagnostics.ConfigureFromSettings(applicationSettings, "AppBootstrapper loaded settings");
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
                structuredExecutionFeatureGate,
                new InteractiveTerminalCoordinator(),
                new TerminalOutputMultiplexer(),
                gitService,
                userPromptService,
                gitWorkspaceCoordinator);
            StartupLifecycleTrace.Write("MainWindowViewModel", "CONSTRUCTED", $"terminalServiceId={liveConsoleService.GetHashCode():X8}; gitServiceId={gitService.GetHashCode():X8}");

            var window = new MainWindow(applicationSettingsService, applicationSettings, uiScaleService, liveConsoleService);
            window.AttachViewModel(viewModel);
            TerminalStartupTrace.Write("BOOTSTRAPPER_CREATE_MAINWINDOW_EXIT", $"windowId={window.GetHashCode():X8}; viewModelId={viewModel.GetHashCode():X8}; serviceId={liveConsoleService.GetHashCode():X8}");
            StartupLifecycleTrace.Write("AppBootstrapper.CreateMainWindow", "EXIT", "ViewModel attached.");

            DeveloperDiagnostics.LogInfo("Startup", "MainWindow instance created and view model attached.");
            return window;
        }
    }
}
