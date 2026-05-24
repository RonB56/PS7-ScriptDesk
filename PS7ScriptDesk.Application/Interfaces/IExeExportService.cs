using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IExeExportService
    {
        Task<ExeExportResult> ExportScriptAsExeAsync(ExeExportRequest request, CancellationToken cancellationToken = default);
    }
}
