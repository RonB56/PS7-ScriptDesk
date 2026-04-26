using PowerShellStudio.Application.Interfaces;

namespace PowerShellStudio.Infrastructure.Services
{
    public class WorkspaceService : IWorkspaceService
    {
        public string GetWorkspaceDisplayText()
        {
            return "Workspace: No folder open";
        }
    }
}