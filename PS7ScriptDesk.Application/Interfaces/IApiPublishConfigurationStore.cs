using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IApiPublishConfigurationStore
{
    string? GetCompanionPath(string? sourceScriptPath);
    bool ConfigurationExists(string sourceScriptPath);
    ApiPublishConfiguration Load(string sourceScriptPath);
    void Save(string sourceScriptPath, ApiPublishConfiguration configuration);
}
