using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IApiPublishConfigurationValidator
{
    ApiPublishValidationResult Validate(ApiPublishConfiguration configuration, ApiMetadataResult? metadata = null);
}
