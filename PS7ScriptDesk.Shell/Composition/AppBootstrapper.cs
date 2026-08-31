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
            uiScaleService ??= new UiScaleService(applicationSettings.UiScalePercent);
            UiScaleServiceHost.SetCurrent(uiScaleService);
            var workspaceService = new WorkspaceService();
            var fileDocumentService = new FileDocumentService();
            var documentRecoveryService = new DocumentRecoveryService();
            var workspaceFolderService = new WorkspaceFolderService();
            var userPromptService = new UserPromptService();
            var liveConsoleService = new LiveConsoleService();
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
                new TerminalOutputMultiplexer());

            var window = new MainWindow(applicationSettingsService, applicationSettings, uiScaleService);
            window.AttachViewModel(viewModel);

            DeveloperDiagnostics.LogInfo("Startup", "MainWindow instance created and view model attached.");
            return window;
        }
    }
}
