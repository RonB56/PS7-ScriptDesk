namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>A prompt verified against one structured execution's completion boundary.</summary>
public sealed record StructuredPromptReadyObservation(
    Guid RequestId,
    int TerminalGeneration,
    int ProcessId,
    string Path);

/// <summary>Optional source of request-correlated, backend-validated prompt observations.</summary>
public interface IStructuredPromptReadinessSource
{
    event Action<StructuredPromptReadyObservation>? StructuredPromptReadyObserved;
}
