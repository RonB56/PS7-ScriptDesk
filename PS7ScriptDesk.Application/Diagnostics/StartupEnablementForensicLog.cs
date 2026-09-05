using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Local-only, startup-scoped edge recorder for Run enablement diagnosis.</summary>
public static class StartupEnablementForensicLog
{
    public const string LogPath = @"C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\Codex_Work\TERMINAL_THREE_BYTE_INPUT_CLASSIFICATION.log";
    public static string ApplicationInstanceId { get; } = Guid.NewGuid().ToString("N");
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> LastDependencies = new(StringComparer.Ordinal);
    private static readonly Queue<string> RecentDependencyChanges = new();
    private static int _enabled;
    private static bool? _lastRunEnabled;
    private static string _lastFailingFactors = "(unknown)";

    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
            Volatile.Write(ref _enabled, 1);
            Write("APP_START", BuildIdentity());
        }
        catch { }
    }

    public static void Stop()
    {
        Write("APP_STOP");
        Volatile.Write(ref _enabled, 0);
    }

    public static void Write(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!IsEnabled) return;
        try
        {
            var fields = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
                .Append(" event=").Append(eventName)
                .Append(" processId=").Append(Environment.ProcessId)
                .Append(" applicationInstanceId=").Append(ApplicationInstanceId);
            if (properties is not null)
            {
                foreach (var pair in properties)
                    fields.Append(' ').Append(pair.Key).Append('=').Append(Sanitize(pair.Value));
            }
            lock (SyncRoot) File.AppendAllText(LogPath, fields.AppendLine().ToString(), Encoding.UTF8);
        }
        catch { }
    }

    public static void DependencyChanged(string dependency, object? oldValue, object? newValue, string source)
    {
        if (!IsEnabled) return;
        var oldText = Sanitize(oldValue);
        var newText = Sanitize(newValue);
        lock (SyncRoot)
        {
            if (LastDependencies.TryGetValue(dependency, out var previous) && previous == newText) return;
            LastDependencies[dependency] = newText;
            var entry = $"dependency={dependency} old={oldText} new={newText} source={Sanitize(source)}";
            RecentDependencyChanges.Enqueue(entry);
            while (RecentDependencyChanges.Count > 8) RecentDependencyChanges.Dequeue();
        }
        Write("ENABLEMENT_DEPENDENCY_CHANGED", new Dictionary<string, object?>
        {
            ["dependency"] = dependency, ["old"] = oldValue, ["new"] = newValue, ["source"] = source
        });
    }

    public static void ObserveDependency(string dependency, object? currentValue, string source)
    {
        if (!IsEnabled) return;
        var current = Sanitize(currentValue);
        string? previous;
        lock (SyncRoot) LastDependencies.TryGetValue(dependency, out previous);
        if (previous != current) DependencyChanged(dependency, previous ?? "(unknown)", currentValue, source);
    }

    public static void RunEnablementEvaluated(
        bool enabled,
        int terminalGeneration,
        IReadOnlyDictionary<string, object?> factors,
        IReadOnlyList<string> failingFactors)
    {
        if (!IsEnabled) return;
        bool? previous;
        string previousFailingFactors;
        lock (SyncRoot)
        {
            previous = _lastRunEnabled;
            previousFailingFactors = _lastFailingFactors;
            if (previous == enabled) return;
            _lastRunEnabled = enabled;
            _lastFailingFactors = failingFactors.Count == 0 ? "(none)" : string.Join(',', failingFactors);
        }

        var properties = new Dictionary<string, object?>(factors)
        {
            ["terminalGeneration"] = terminalGeneration,
            ["previousEnabled"] = previous?.ToString() ?? "(unknown)",
            ["newEnabled"] = enabled,
            ["failingFactors"] = failingFactors.Count == 0 ? "(none)" : string.Join(',', failingFactors),
            ["previousFailingFactors"] = previousFailingFactors,
            ["precedingChanges"] = GetRecentChanges()
        };
        Write("RUN_ENABLEMENT_EDGE", properties);
    }

    public static void ControlEdge(string eventName, bool previous, bool current, bool isRunAvailable, bool commandCanExecute, bool bindingPresent)
    {
        if (previous == current) return;
        Write(eventName, new Dictionary<string, object?>
        {
            ["previous"] = previous, ["new"] = current, ["isRunAvailable"] = isRunAvailable,
            ["runCommandCanExecute"] = commandCanExecute, ["bindingPresent"] = bindingPresent
        });
    }

    public static void InputClassification(
        int payloadLength,
        string inputClass,
        int terminalGeneration,
        object? coordinatorStateBefore,
        object? coordinatorStateAfter,
        object? editOwnershipBefore,
        object? editOwnershipAfter,
        string source)
    {
        Write("TERMINAL_INPUT_CLASSIFIED", new Dictionary<string, object?>
        {
            ["payloadLength"] = payloadLength,
            ["inputClass"] = inputClass,
            ["terminalGeneration"] = terminalGeneration,
            ["coordinatorStateBefore"] = coordinatorStateBefore,
            ["coordinatorStateAfter"] = coordinatorStateAfter,
            ["editOwnershipBefore"] = editOwnershipBefore,
            ["editOwnershipAfter"] = editOwnershipAfter,
            ["source"] = source,
            ["originatedFromKeyboard"] = "unknown-from-xterm-onData"
        });
    }

    private static string GetRecentChanges()
    {
        lock (SyncRoot) return RecentDependencyChanges.Count == 0 ? "(none)" : string.Join('|', RecentDependencyChanges);
    }

    private static Dictionary<string, object?> BuildIdentity()
    {
        var assemblies = new (string Name, Assembly? Assembly)[]
        {
            (Name: "shell", Assembly: Assembly.GetEntryAssembly()),
            (Name: "ui", Assembly.Load("PS7ScriptDesk.UI")),
            (Name: "application", Assembly.Load("PS7ScriptDesk.Application"))
        };
        var identity = new Dictionary<string, object?>
        {
            ["processPath"] = Environment.ProcessPath,
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        };
        foreach (var item in assemblies)
        {
            var path = item.Assembly?.Location;
            identity[$"{item.Name}AssemblyPath"] = path;
            identity[$"{item.Name}FileVersion"] = item.Assembly?.GetName().Version?.ToString();
            identity[$"{item.Name}BuildTimestampUtc"] = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture) : "(missing)";
            identity[$"{item.Name}Sha256"] = ComputeSha256(path);
        }
        return identity;
    }

    private static string ComputeSha256(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "(missing)";
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return "(unavailable)"; }
    }

    private static string Sanitize(object? value) =>
        (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)")
            .Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
}
