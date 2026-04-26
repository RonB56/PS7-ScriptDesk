using System;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Application.Interfaces
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
