namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>Live completion over the already-authoritative PowerShell/PSES session.</summary>
public interface IAuthoritativeLiveCompletionProvider
{
    bool IsLiveCompletionAvailable { get; }

    Task<AuthoritativeCompletionResult?> GetLiveCompletionsAsync(
        string documentUri,
        string documentText,
        int cursorOffset,
        CancellationToken cancellationToken = default);

    Task CloseDocumentAsync(string documentUri, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed record AuthoritativeCompletionResult(
    int ReplacementIndex,
    int ReplacementLength,
    IReadOnlyList<AuthoritativeCompletionItem> Items);

public sealed record AuthoritativeCompletionItem(
    string CompletionText,
    string ListItemText,
    int Kind,
    string Tooltip);
