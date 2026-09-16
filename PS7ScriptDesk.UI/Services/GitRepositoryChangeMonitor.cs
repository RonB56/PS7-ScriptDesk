using System.Diagnostics;

namespace PS7ScriptDesk.UI.Services;

/// <summary>
/// Watches a repository working tree and its effective git directory, then coalesces
/// filesystem notifications into quiet, single-flight refresh requests.
/// </summary>
public sealed class GitRepositoryChangeMonitor : IDisposable
{
    public const int DebounceMilliseconds = 300;

    private readonly object _gate = new();
    private readonly string _workingTreeRoot;
    private readonly Func<Task> _refreshAsync;
    private readonly Timer _debounceTimer;
    private FileSystemWatcher? _workingTreeWatcher;
    private FileSystemWatcher? _gitDirectoryWatcher;
    private bool _refreshPending;
    private bool _refreshRunning;
    private bool _watchersNeedRecovery;
    private bool _disposed;
    private int _scheduledNotificationCount;

    public GitRepositoryChangeMonitor(string workingTreeRoot, Func<Task> refreshAsync)
    {
        if (string.IsNullOrWhiteSpace(workingTreeRoot))
            throw new ArgumentException("A working-tree root is required.", nameof(workingTreeRoot));
        _workingTreeRoot = Path.GetFullPath(workingTreeRoot);
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _debounceTimer = new Timer(DebounceTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        RecreateWatchers();
    }

    private void DebounceTimerCallback(object? state) => _ = ProcessPendingRefreshAsync();

    private void OnFilesystemChange(object sender, FileSystemEventArgs e)
    {
        if (IsWorkingTreeWatcher(sender) && IsIgnoredWorkingTreePath(e.FullPath))
            return;
        ScheduleRefresh("filesystem-change");
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsWorkingTreeWatcher(sender) && IsIgnoredWorkingTreePath(e.FullPath) && IsIgnoredWorkingTreePath(e.OldFullPath))
            return;
        ScheduleRefresh("filesystem-rename");
    }

    private bool IsWorkingTreeWatcher(object sender)
    {
        lock (_gate)
            return ReferenceEquals(sender, _workingTreeWatcher);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _watchersNeedRecovery = true;
        }
        Log("watcher-error", e.GetException().GetType().Name);
        ScheduleRefresh("watcher-error");
    }

    private void ScheduleRefresh(string reason)
    {
        var shouldLog = false;
        lock (_gate)
        {
            if (_disposed)
                return;
            _refreshPending = true;
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
            shouldLog = Interlocked.Increment(ref _scheduledNotificationCount) == 1;
        }
        if (shouldLog)
            Log("refresh-scheduled", reason);
    }

    private async Task ProcessPendingRefreshAsync()
    {
        var recoverWatchers = false;
        lock (_gate)
        {
            if (_disposed || !_refreshPending || _refreshRunning)
                return;
            _refreshPending = false;
            _refreshRunning = true;
            recoverWatchers = _watchersNeedRecovery;
            _watchersNeedRecovery = false;
            Interlocked.Exchange(ref _scheduledNotificationCount, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (recoverWatchers)
            {
                try { RecreateWatchers(); }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        if (!_disposed)
                            _watchersNeedRecovery = true;
                    }
                    Log("watcher-recovery-failed", ex.GetType().Name);
                }
            }
            Log("refresh-start", recoverWatchers ? "watchers-recovered" : "notification-batch");
            await _refreshAsync().ConfigureAwait(false);
            Log("refresh-complete", $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            // A watcher must never take down the Workspace window. A later notification
            // or explicit Refresh can recover the repository state.
            Log("refresh-failed", $"elapsedMs={stopwatch.ElapsedMilliseconds}; exception={ex.GetType().Name}");
        }
        finally
        {
            var scheduleFollowUp = false;
            lock (_gate)
            {
                _refreshRunning = false;
                scheduleFollowUp = !_disposed && _refreshPending;
                if (scheduleFollowUp)
                    _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
            }
        }
    }

    private void RecreateWatchers()
    {
        FileSystemWatcher? oldWorkingTree;
        FileSystemWatcher? oldGitDirectory;
        FileSystemWatcher? newWorkingTree = null;
        FileSystemWatcher? newGitDirectory = null;
        var gitEntryIsDirectory = false;
        var gitDirectory = ResolveGitDirectory(_workingTreeRoot, out gitEntryIsDirectory);

        try
        {
            newWorkingTree = CreateWatcher(_workingTreeRoot, includeSubdirectories: true);
            if (!gitEntryIsDirectory && Directory.Exists(gitDirectory))
                newGitDirectory = CreateWatcher(gitDirectory, includeSubdirectories: true);
            else if (gitEntryIsDirectory)
                newGitDirectory = CreateWatcher(gitDirectory, includeSubdirectories: true);
        }
        catch
        {
            newWorkingTree?.Dispose();
            newGitDirectory?.Dispose();
            throw;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                newWorkingTree.Dispose();
                newGitDirectory?.Dispose();
                return;
            }
            oldWorkingTree = _workingTreeWatcher;
            oldGitDirectory = _gitDirectoryWatcher;
            _workingTreeWatcher = newWorkingTree;
            _gitDirectoryWatcher = newGitDirectory;
        }
        oldWorkingTree?.Dispose();
        oldGitDirectory?.Dispose();
        Log("watchers-attached", $"root={_workingTreeRoot}; gitDirectory={gitDirectory}");
    }

    private FileSystemWatcher CreateWatcher(string path, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            Filter = "*",
            EnableRaisingEvents = true
        };
        watcher.Changed += OnFilesystemChange;
        watcher.Created += OnFilesystemChange;
        watcher.Deleted += OnFilesystemChange;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private bool IsIgnoredWorkingTreePath(string path)
    {
        var gitEntry = Path.Combine(_workingTreeRoot, ".git");
        if (!Directory.Exists(gitEntry))
            return false;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var gitPath = Path.GetFullPath(gitEntry).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(gitPath, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(gitPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveGitDirectory(string workingTreeRoot, out bool gitEntryIsDirectory)
    {
        var gitEntry = Path.Combine(Path.GetFullPath(workingTreeRoot), ".git");
        gitEntryIsDirectory = Directory.Exists(gitEntry);
        if (gitEntryIsDirectory)
            return gitEntry;
        if (!File.Exists(gitEntry))
            return gitEntry;

        var line = File.ReadLines(gitEntry).FirstOrDefault() ?? string.Empty;
        const string prefix = "gitdir:";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return gitEntry;
        var target = line[prefix.Length..].Trim();
        return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(gitEntry)!, target));
    }

    private static void Log(string eventName, string detail)
    {
        try { Application.Diagnostics.DeveloperDiagnostics.LogInfo("Git", $"GitRepositoryChangeMonitor.{eventName}: {detail}"); }
        catch { /* diagnostics are best-effort */ }
    }

    public void Dispose()
    {
        FileSystemWatcher? workingTree;
        FileSystemWatcher? gitDirectory;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _refreshPending = false;
            workingTree = _workingTreeWatcher;
            gitDirectory = _gitDirectoryWatcher;
            _workingTreeWatcher = null;
            _gitDirectoryWatcher = null;
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        workingTree?.Dispose();
        gitDirectory?.Dispose();
        _debounceTimer.Dispose();
        Log("disposed", $"root={_workingTreeRoot}");
    }
}
