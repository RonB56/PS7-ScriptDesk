using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Infrastructure.Services
{
    public class WorkspaceService : IWorkspaceService
    {
        public string GetWorkspaceDisplayText()
        {
            return "Workspace: No folder open";
        }
    }
}