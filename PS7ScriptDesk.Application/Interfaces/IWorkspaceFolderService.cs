using System.Threading;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IWorkspaceFolderService
    {
        WorkspaceFolderLoadResult GetWorkspaceItems(
            string folderPath,
            string? filterText = null,
            bool recursive = true,
            CancellationToken cancellationToken = default);

        WorkspaceFolderLoadResult GetWorkspaceChildItems(
            string workspaceRootPath,
            string directoryPath,
            string? filterText = null,
            CancellationToken cancellationToken = default);
    }
}
