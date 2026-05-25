using System.Collections.Generic;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Composition
{
    public static class AppBootstrapper
    {
        public static MainWindow CreateMainWindow(ApplicationSettingsService applicationSettingsService, ApplicationSettings applicationSettings, PowerShellRuntimeInfo? startupRuntimeInfo)
        {
            var workspaceService = new WorkspaceService();
            var fileDocumentService = new FileDocumentService();
            var workspaceFolderService = new WorkspaceFolderService();
            var userPromptService = new UserPromptService();
            var liveConsoleService = new LiveConsoleService();
            var exeExportService = new ExeExportService();
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
                startupRuntimeInfo);

            var window = new MainWindow(applicationSettingsService, applicationSettings)
            {
                DataContext = viewModel
            };

            DeveloperDiagnostics.LogInfo("Startup", "MainWindow instance created and DataContext assigned.");
            return window;
        }
    }
}
