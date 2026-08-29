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
            var workspaceService = new WorkspaceService();
            var fileDocumentService = new FileDocumentService();
            var workspaceFolderService = new WorkspaceFolderService();
            var userPromptService = new UserPromptService();
            var liveConsoleService = new LiveConsoleService();
            var exeExportService = new ExeExportService();
            var exeExportWizardService = new ExportWizardService(applicationSettings);
            var restApiPublishWizardService = new RestApiPublishWizardService(new ApiPublishConfigurationStore());
            var runtimeService = new RuntimeService(applicationSettings.SelectedRuntimeExecutablePath);
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
                uiScaleService);

            var window = new MainWindow(applicationSettingsService, applicationSettings, uiScaleService)
            {
                DataContext = viewModel
            };

            DeveloperDiagnostics.LogInfo("Startup", "MainWindow instance created and DataContext assigned.");
            return window;
        }
    }
}
