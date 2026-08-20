using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Shell.Services;

public sealed class ExportWizardService : IExeExportWizardService
{
    private readonly ApplicationSettings _settings;

    public ExportWizardService(ApplicationSettings settings) => _settings = settings;

    public ExeExportConfiguration? ShowWizard(ExeExportWizardRequest request)
    {
        var analyzedRequest = request with { Dependencies = new PowerShellDependencyAnalyzer().Analyze(request.ScriptContent) };
        var dialog = new ExportWizardWindow(analyzedRequest, _settings.LastExeExportConfiguration)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.Configuration is null)
            return null;

        _settings.LastExeExportConfiguration = dialog.Configuration.Clone();
        return dialog.Configuration;
    }
}
