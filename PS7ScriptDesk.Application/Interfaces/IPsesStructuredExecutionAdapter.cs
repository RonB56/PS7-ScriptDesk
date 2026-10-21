using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>Request-capable LSP execution over the already-authoritative PSES session.</summary>
public interface IPsesStructuredExecutionAdapter : IAsyncDisposable
{
    event Action<bool>? BusyChanged;
    event Action<Exception>? SessionFaulted;

    int SessionGeneration { get; }

    bool IsInitialized { get; }

    Task InitializeAsync(
        string languageServicePipePath,
        int processId,
        int sessionGeneration,
        Func<string, CancellationToken, Task> sendSyntheticTerminalInputAsync,
        CancellationToken cancellationToken = default);

    Task<PsesStructuredEvaluationResult> EvaluateAsync(
        Guid requestId,
        string expression,
        CancellationToken cancellationToken = default);

    Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
}

public sealed record PsesStructuredEvaluationResult(
    EditorExecutionStatus Status,
    bool ResponseReceived,
    bool ExecutionBusyObserved,
    string? ProtocolError,
    string? ErrorMessage);
