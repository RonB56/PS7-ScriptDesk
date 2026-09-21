using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Best-effort, synchronous forensic trace for one desktop terminal startup attempt.
/// It is deliberately independent of the asynchronous developer diagnostics queue so
/// the last completed milestone survives a hung startup.
/// </summary>
public static class TerminalStartupTrace
{
    private const long MaximumFileBytes = 512 * 1024;
    private const int MaximumRetainedTraces = 10;
    private static readonly object Gate = new();
    private static readonly Stopwatch Elapsed = Stopwatch.StartNew();
    private static readonly string AttemptId = Guid.NewGuid().ToString("N");
    private static readonly string TraceDirectory = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "DeveloperDebugging");
    private static readonly string TracePath = Path.Combine(
        TraceDirectory,
        $"TerminalStartupTrace_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{AttemptId}.log");
    private static Func<(bool IsUiThread, int? DispatcherThreadId)>? _uiSnapshotProvider;
    private static int _started;
    private static int _firstRead;
    private static int _firstOutput;
    private static int _firstRendererOutput;

    public static string StartupAttemptId => AttemptId;
    public static string CurrentTracePath => TracePath;

    public static void ConfigureUiThreadSnapshotProvider(Func<(bool IsUiThread, int? DispatcherThreadId)> provider)
        => _uiSnapshotProvider = provider;

    public static void Start(string? detail = null)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            Write("APP_START_DUPLICATE", detail);
            return;
        }

        Write("APP_START", detail ?? $"pid={Environment.ProcessId}; processPath={Environment.ProcessPath}");
    }

    public static void Write(string stage, string? detail = null)
    {
        try
        {
            var ui = default((bool IsUiThread, int? DispatcherThreadId));
            try { ui = _uiSnapshotProvider?.Invoke() ?? (false, null); } catch { }
            var line = new StringBuilder(768)
                .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" elapsedMs=").Append(Elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Append(" StartupAttemptId=").Append(AttemptId)
                .Append(" pid=").Append(Environment.ProcessId)
                .Append(" ThreadId=").Append(Environment.CurrentManagedThreadId)
                .Append(" DispatcherCheckAccess=").Append(ui.IsUiThread)
                .Append(" DispatcherThreadId=").Append(ui.DispatcherThreadId?.ToString(CultureInfo.InvariantCulture) ?? "?")
                .Append(" Stage=").Append(stage);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                line.Append(' ').Append(Bound(detail));
            }
            line.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(TraceDirectory);
                RotateTraceDirectoryIfNeeded(line.Length * sizeof(char));
                File.AppendAllText(TracePath, line.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalStartupTrace] write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void FirstRead(string? detail = null)
    {
        if (Interlocked.Exchange(ref _firstRead, 1) == 0) Write("CONPTY_FIRST_READ", detail);
    }

    public static void FirstNonEmptyOutput(string? detail = null)
    {
        if (Interlocked.Exchange(ref _firstOutput, 1) == 0) Write("CONPTY_FIRST_NONEMPTY_OUTPUT", detail);
    }

    public static void FirstRendererOutput(string? detail = null)
    {
        if (Interlocked.Exchange(ref _firstRendererOutput, 1) == 0) Write("FIRST_TERMINAL_OUTPUT_TO_RENDERER", detail);
    }

    private static string Bound(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 768 ? value : value[..768] + "…";
    }

    private static void RotateTraceDirectoryIfNeeded(int incomingBytes)
    {
        if (!File.Exists(TracePath) || new FileInfo(TracePath).Length + incomingBytes <= MaximumFileBytes)
        {
            TrimRetainedTraceFiles();
            return;
        }

        // A single trace is bounded; older per-launch traces are bounded separately.
        var archived = TracePath + ".complete";
        if (File.Exists(archived)) File.Delete(archived);
        File.Move(TracePath, archived);
        TrimRetainedTraceFiles();
    }

    private static void TrimRetainedTraceFiles()
    {
        var traces = Directory.EnumerateFiles(TraceDirectory, "TerminalStartupTrace_*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(MaximumRetainedTraces)
            .ToArray();
        foreach (var trace in traces)
        {
            try { File.Delete(trace); } catch { }
        }
    }
}
