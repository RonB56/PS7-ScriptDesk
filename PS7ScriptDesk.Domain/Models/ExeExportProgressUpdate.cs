namespace PS7ScriptDesk.Domain.Models;

/// <summary>
/// A meaningful exporter lifecycle update. Percentage is intentionally omitted for
/// process stages whose duration cannot be measured reliably.
/// </summary>
public sealed class ExeExportProgressUpdate
{
    public ExeExportProgressUpdate(
        string stage,
        string statusMessage,
        bool isIndeterminate,
        bool isCompleted = false,
        bool succeeded = false,
        string? outputExecutablePath = null,
        string? detailedLog = null)
    {
        Stage = string.IsNullOrWhiteSpace(stage) ? "Export" : stage;
        StatusMessage = string.IsNullOrWhiteSpace(statusMessage) ? "Export is in progress." : statusMessage;
        IsIndeterminate = isIndeterminate;
        IsCompleted = isCompleted;
        Succeeded = succeeded;
        OutputExecutablePath = outputExecutablePath ?? string.Empty;
        DetailedLog = detailedLog ?? string.Empty;
    }

    public string Stage { get; }

    public string StatusMessage { get; }

    public bool IsIndeterminate { get; }

    public bool IsCompleted { get; }

    public bool Succeeded { get; }

    public string OutputExecutablePath { get; }

    public string DetailedLog { get; }
}
