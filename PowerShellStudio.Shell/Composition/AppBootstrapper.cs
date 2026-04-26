using PowerShellStudio.Infrastructure.Services;
using PowerShellStudio.PowerShell.Services;
using PowerShellStudio.Shell.Services;
using PowerShellStudio.UI.ViewModels;

namespace PowerShellStudio.Shell.Composition
{
    public static class AppBootstrapper
    {
        public static MainWindow CreateMainWindow()
        {
            var workspaceService = new WorkspaceService();
            var runtimeService = new RuntimeService();
            var fileDocumentService = new FileDocumentService();
            var workspaceFolderService = new WorkspaceFolderService();
            var userPromptService = new UserPromptService();
            var liveConsoleService = new LiveConsoleService();
            var exeExportService = new ExeExportService();
            var applicationSettingsService = new ApplicationSettingsService();
            var applicationSettings = applicationSettingsService.LoadSettings();

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

            return window;
        }
    }
}