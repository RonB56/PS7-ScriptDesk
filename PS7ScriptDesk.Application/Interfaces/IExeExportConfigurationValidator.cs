using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IExeExportConfigurationValidator
{
    ExeExportValidationResult Validate(ExeExportConfiguration configuration);
}
