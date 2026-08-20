using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IExeExportWizardService
{
    ExeExportConfiguration? ShowWizard(ExeExportWizardRequest request);
}
