using System.Security.Cryptography;
using System.Text;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Privacy-safe forensic logging for the recall-then-Enter investigation.</summary>
public static class TerminalRecallEnterForensicLog
{
    public const string LogPath = @"C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\Codex_Work\TERMINAL_RECALL_ENTER_SUBMISSION_FORENSIC.log";

    public static void LogXtermInput(int generation, string? payload) => Write("XTERM_INPUT", new Dictionary<string, object?>
    {
        ["terminalGeneration"] = generation,
        ["inputClass"] = TerminalInputClassifier.Classify(payload ?? string.Empty),
        ["payloadLength"] = payload?.Length ?? 0,
        ["source"] = "xterm.onData"
    });

    public static void LogRouterWrite(string origin, int generation, string? payload, bool internalDispatchPending, bool userEditOwnershipActive, string? requestId)
        => Write("CONPTY_WRITE", new Dictionary<string, object?>
        {
            ["origin"] = origin,
            ["payloadLength"] = payload?.Length ?? 0,
            ["inputClass"] = TerminalInputClassifier.Classify(payload ?? string.Empty),
            ["payloadHash"] = Hash(payload),
            ["requestId"] = requestId ?? "(none)",
            ["terminalGeneration"] = generation,
            ["internalDispatchPending"] = internalDispatchPending,
            ["userEditOwnershipActive"] = userEditOwnershipActive
        });

    public static void LogAcceptedLine(string classification, int length, string hash)
        => Write("PSREADLINE_ACCEPTED", new Dictionary<string, object?>
        {
            ["bufferClassification"] = classification,
            ["bufferLength"] = length,
            ["bufferHash"] = hash,
            ["cursorPosition"] = "unsupported-by-public-api"
        });

    public static string Hash(string? value)
    {
        using var algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private static void Write(string eventName, IReadOnlyDictionary<string, object?> fields)
    {
        try
        {
            var parts = new List<string> { $"event={eventName}" };
            foreach (var field in fields) parts.Add($"{field.Key}={field.Value ?? "(none)"}");
            parts.Add($"timestamp={DateTimeOffset.UtcNow:O}");
            File.AppendAllText(LogPath, string.Join(' ', parts) + Environment.NewLine);
        }
        catch { }
    }
}
