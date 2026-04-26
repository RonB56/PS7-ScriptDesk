using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Application.Interfaces
{
    public interface IExeExportService
    {
        Task<ExeExportResult> ExportScriptAsExeAsync(ExeExportRequest request, CancellationToken cancellationToken = default);
    }
}
