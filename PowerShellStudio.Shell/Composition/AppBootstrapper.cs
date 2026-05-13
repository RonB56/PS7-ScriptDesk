using System.Collections.Generic;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Domain.Models;
using PowerShellStudio.Infrastructure.Services;
using PowerShellStudio.PowerShell.Services;
using PowerShellStudio.Shell.Services;
using PowerShellStudio.UI.ViewModels;

namespace PowerShellStudio.Shell.Composition
{
    public static class AppBootstrapper
    {
        public static MainWindow CreateMainWindow(ApplicationSettingsService applicationSettingsService, ApplicationSettings applicationSettings)
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
                applicationSettings);

            var window = new MainWindow(applicationSettingsService, applicationSettings)
            {
                DataContext = viewModel
            };

            DeveloperDiagnostics.LogInfo("Startup", "MainWindow instance created and DataContext assigned.");
            return window;
        }
    }
}
