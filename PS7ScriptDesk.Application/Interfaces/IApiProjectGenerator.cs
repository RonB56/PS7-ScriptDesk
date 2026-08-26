using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IApiProjectGenerator
{
    Task<ApiProjectGenerationResult> GenerateAsync(
        ApiProjectGenerationRequest request,
        CancellationToken cancellationToken = default);
}
