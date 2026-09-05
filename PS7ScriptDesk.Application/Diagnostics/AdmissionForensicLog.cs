using System.Globalization;
using System.Text;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Temporary local-only timeline for terminal admission investigation.</summary>
public static class AdmissionForensicLog
{
    public const string LogPath = @"C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\Codex_Work\LIVE_RUN_ENABLEMENT_FORENSIC.log";
    public static string ApplicationInstanceId { get; } = Guid.NewGuid().ToString("N");
    private static readonly object SyncRoot = new();
    private static readonly AsyncLocal<string?> CurrentRequest = new();
    private static int _enabled;
    private static int _terminalGeneration;

    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;
    public static int TerminalGeneration => Volatile.Read(ref _terminalGeneration);

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
            Volatile.Write(ref _enabled, 1);
            Write("FORENSIC_GUI_START", new Dictionary<string, object?> { ["logPath"] = LogPath });
        }
        catch { }
    }

    public static void Stop()
    {
        if (!IsEnabled)
        {
            return;
        }

        Write("FORENSIC_GUI_STOP");
        Volatile.Write(ref _enabled, 0);
    }

    public static void SetTerminalGeneration(int generation) => Volatile.Write(ref _terminalGeneration, generation);

    public static IDisposable BeginRequest(string eventName)
    {
        var previous = CurrentRequest.Value;
        var requestId = $"{eventName}-{Guid.NewGuid():N}";
        CurrentRequest.Value = requestId;
        Write(eventName, new Dictionary<string, object?> { ["requestId"] = requestId });
        return new Scope(previous);
    }

    public static void Write(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var fields = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" event=").Append(eventName)
                .Append(" processId=").Append(Environment.ProcessId)
                .Append(" applicationInstanceId=").Append(ApplicationInstanceId)
                .Append(" terminalGeneration=").Append(TerminalGeneration)
                .Append(" requestId=").Append(CurrentRequest.Value ?? "(none)");
            if (properties is not null)
            {
                foreach (var pair in properties)
                {
                    fields.Append(' ').Append(pair.Key).Append('=').Append(Sanitize(pair.Value));
                }
            }

            lock (SyncRoot)
            {
                File.AppendAllText(LogPath, fields.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch { }
    }

    private static string Sanitize(object? value) =>
        (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)")
            .Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        public Scope(string? previous) => _previous = previous;
        public void Dispose() => CurrentRequest.Value = _previous;
    }
}
