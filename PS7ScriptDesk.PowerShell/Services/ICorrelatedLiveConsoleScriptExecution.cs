using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

internal interface ICorrelatedLiveConsoleScriptExecution
{
    Task<LiveConsoleCommandResult> ExecuteScriptForRequestAsync(
        Guid requestId,
        string documentDisplayName,
        string scriptContent,
        Action<ExecutionOutputRecord> onOutput,
        bool executeInCurrentScope,
        CancellationToken cancellationToken);
}
