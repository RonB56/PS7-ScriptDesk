using System.Threading;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Application.Interfaces
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
