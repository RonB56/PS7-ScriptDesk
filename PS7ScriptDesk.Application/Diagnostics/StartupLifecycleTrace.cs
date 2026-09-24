using System.Diagnostics;
using System.Text;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Small synchronous, bounded trace for reconstructing startup hangs where the normal
/// asynchronous logger may not have flushed before the process becomes unresponsive.
/// </summary>
public static class StartupLifecycleTrace
{
    private const long MaximumFileBytes = 512 * 1024;
    private const int MaximumArchives = 2;
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "StartupLifecycleTrace.log");
    private static long _sequence;
    private static Func<(bool IsUiThread, int? DispatcherThreadId)>? _uiSnapshotProvider;

    public static string CurrentLogPath => LogPath;

    public static void ConfigureUiThreadSnapshotProvider(Func<(bool IsUiThread, int? DispatcherThreadId)> provider)
        => _uiSnapshotProvider = provider;

    public static void Write(string stage, string phase, string? detail = null)
    {
        try
        {
            PerformanceTrace.Record("milestone", "Startup", $"{stage}.{phase}");
            var sequence = Interlocked.Increment(ref _sequence);
            var snapshot = default((bool IsUiThread, int? DispatcherThreadId));
            try { snapshot = _uiSnapshotProvider?.Invoke() ?? (false, null); } catch { }
            var line = new StringBuilder(512)
                .Append(sequence.ToString("D6"))
                .Append(' ').Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(" T").Append(Environment.CurrentManagedThreadId)
                .Append(" UI=").Append(snapshot.IsUiThread ? "True" : "False")
                .Append(" DISP=").Append(snapshot.DispatcherThreadId?.ToString() ?? "?")
                .Append(" AttemptId=").Append(TerminalStartupTrace.StartupAttemptId)
                .Append(' ').Append(stage).Append(' ').Append(phase);
            if (!string.IsNullOrWhiteSpace(detail)) line.Append(" ").Append(Bound(detail));
            line.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(line.Length * 2);
                File.AppendAllText(LogPath, line.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupLifecycleTrace] write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Bound(string value)
    {
        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return value.Length <= 512 ? value : value[..512] + "…";
    }

    private static void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length + incomingBytes < MaximumFileBytes) return;
        for (var index = MaximumArchives - 1; index >= 1; index--)
        {
            var source = LogPath + "." + index;
            var destination = LogPath + "." + (index + 1);
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(source)) File.Move(source, destination);
        }
        var first = LogPath + ".1";
        if (File.Exists(first)) File.Delete(first);
        File.Move(LogPath, first);
    }
}
