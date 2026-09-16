using PS7ScriptDesk.UI.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitRepositoryChangeMonitorTests
{
    [Fact]
    public async Task WorkingTreeChange_SchedulesOneRefresh()
    {
        using var repository = TemporaryRepository.Create();
        var refresh = NewRefreshCounter();
        using var monitor = new GitRepositoryChangeMonitor(repository.Root, refresh.Callback);

        File.WriteAllText(Path.Combine(repository.Root, "tracked.ps1"), "Write-Output changed");

        await refresh.FirstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, refresh.Count);
    }

    [Fact]
    public async Task RapidWorkingTreeEvents_AreDebounced()
    {
        using var repository = TemporaryRepository.Create();
        var refresh = NewRefreshCounter();
        using var monitor = new GitRepositoryChangeMonitor(repository.Root, refresh.Callback);

        for (var i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(repository.Root, "burst.txt"), i.ToString());

        await refresh.FirstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(GitRepositoryChangeMonitor.DebounceMilliseconds + 250);
        Assert.Equal(1, refresh.Count);
    }

    [Fact]
    public async Task GitMetadataChanges_IndexHeadRefAndMergeState_ScheduleRefresh()
    {
        using var repository = TemporaryRepository.Create();
        Directory.CreateDirectory(Path.Combine(repository.Root, ".git", "refs", "heads"));
        var refresh = NewRefreshCounter();
        using var monitor = new GitRepositoryChangeMonitor(repository.Root, refresh.Callback);
        await Task.Delay(100);

        File.WriteAllText(Path.Combine(repository.Root, ".git", "index"), "index");
        File.WriteAllText(Path.Combine(repository.Root, ".git", "HEAD"), "ref: refs/heads/main");
        File.WriteAllText(Path.Combine(repository.Root, ".git", "refs", "heads", "main"), "commit");
        File.WriteAllText(Path.Combine(repository.Root, ".git", "MERGE_HEAD"), "other-commit");

        await refresh.FirstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, refresh.Count);
    }

    [Fact]
    public async Task GitFileWorktreePointer_WatchesEffectiveGitDirectory()
    {
        using var repository = TemporaryRepository.Create();
        var effectiveGitDirectory = Path.Combine(repository.Root, "..", "effective-git");
        Directory.CreateDirectory(effectiveGitDirectory);
        File.WriteAllText(Path.Combine(repository.Root, ".git"), $"gitdir: {effectiveGitDirectory}{Environment.NewLine}");
        var refresh = NewRefreshCounter();
        using var monitor = new GitRepositoryChangeMonitor(repository.Root, refresh.Callback);

        File.WriteAllText(Path.Combine(effectiveGitDirectory, "HEAD"), "ref: refs/heads/main");

        await refresh.FirstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Path.GetFullPath(effectiveGitDirectory), GitRepositoryChangeMonitor.ResolveGitDirectory(repository.Root, out var isDirectory));
        Assert.False(isDirectory);
    }

    [Fact]
    public async Task RefreshAlreadyRunning_CoalescesFollowUpWithoutOverlap()
    {
        using var repository = TemporaryRepository.Create();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        async Task RefreshAsync()
        {
            var active = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, active);
            var count = Interlocked.Increment(ref refreshCount);
            if (count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            Interlocked.Decrement(ref concurrent);
        }

        using var monitor = new GitRepositoryChangeMonitor(repository.Root, RefreshAsync);
        File.WriteAllText(Path.Combine(repository.Root, "first.txt"), "one");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        File.WriteAllText(Path.Combine(repository.Root, "second.txt"), "two");
        releaseFirst.SetResult();

        await Task.Delay(GitRepositoryChangeMonitor.DebounceMilliseconds * 3);
        Assert.Equal(1, maxConcurrent);
        Assert.InRange(refreshCount, 1, 2);
    }

    [Fact]
    public async Task Dispose_StopsFutureRefreshNotifications()
    {
        using var repository = TemporaryRepository.Create();
        var refresh = NewRefreshCounter();
        var monitor = new GitRepositoryChangeMonitor(repository.Root, refresh.Callback);
        await Task.Delay(100);
        monitor.Dispose();

        File.WriteAllText(Path.Combine(repository.Root, "after-close.txt"), "ignored");
        await Task.Delay(GitRepositoryChangeMonitor.DebounceMilliseconds + 250);

        Assert.Equal(0, refresh.Count);
    }

    private static RefreshCounter NewRefreshCounter() => new();

    private sealed class RefreshCounter
    {
        public TaskCompletionSource FirstRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
        public Task Callback()
        {
            if (Interlocked.Increment(ref Count) == 1)
                FirstRefresh.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        private TemporaryRepository(string root) => Root = root;
        public string Root { get; }
        public static TemporaryRepository Create()
        {
            var root = Directory.CreateTempSubdirectory("PS7ScriptDesk-GitMonitor-").FullName;
            return new TemporaryRepository(root);
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
            var sibling = Path.Combine(Path.GetDirectoryName(Root)!, "effective-git");
            try { if (Directory.Exists(sibling)) Directory.Delete(sibling, recursive: true); } catch { }
        }
    }
}
