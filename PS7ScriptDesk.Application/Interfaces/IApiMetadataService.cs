using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IApiMetadataService
{
    ApiMetadataResult Analyze(string sourceText, string? sourcePath = null);
}
