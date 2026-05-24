using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IRuntimeService
    {
        RuntimeDiscoveryResult DiscoverRuntimes();

        PowerShellRuntimeInfo? TryResolveRuntimeIdentity(string executablePath);

        RuntimeValidationResult ValidateRuntimePath(string executablePath, string source);
    }
}
