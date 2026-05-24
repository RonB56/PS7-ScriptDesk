using System;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IScriptExecutionService
    {
        bool IsExecutionInProgress { get; }

        Task<ScriptExecutionResult> ExecuteScriptAsync(
            PowerShellRuntimeInfo runtime,
            string documentDisplayName,
            string scriptContent,
            Action<ExecutionOutputRecord> onOutput,
            CancellationToken cancellationToken = default);

        Task<bool> StopExecutionAsync();
    }
}
