using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Application.Interfaces
{
    public interface IRuntimeService
    {
        RuntimeDiscoveryResult DiscoverRuntimes();

        PowerShellRuntimeInfo? TryResolveRuntimeIdentity(string executablePath);
    }
}
