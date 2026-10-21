using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>
/// Structured execution contract over the already-authoritative interactive session.
/// Implementations must not create another PowerShell process or in-process runspace.
/// </summary>
public interface IAuthoritativeSessionStructuredExecution
{
    bool IsSessionRunning { get; }

    int? ProcessId { get; }

    int SessionGeneration { get; }

    string? CurrentWorkingDirectory { get; }

    Task<AuthoritativeStructuredExecutionResult> ExecuteStructuredScriptAsync(
        Guid requestId,
        int sessionGeneration,
        string documentDisplayName,
        string scriptContent,
        bool executeInCurrentScope,
        Action<ExecutionOutputRecord> onOutput,
        CancellationToken cancellationToken = default);
}

public sealed record AuthoritativeStructuredExecutionResult(
    EditorExecutionStatus Status,
    string? CurrentWorkingDirectory,
    string? ErrorMessage,
    bool CompletionObserved);
