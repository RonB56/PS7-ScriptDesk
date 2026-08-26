using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IRestApiPublishWizardService
{
    ApiPublishConfiguration? ShowWizard(ApiPublishWizardRequest request);
}
