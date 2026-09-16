using System.Collections.Concurrent;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.Tests;

public sealed class GitUatBatch23Tests
{
    [Fact]
    public void RelayCommandCanExecuteChangedReturnsToCapturedSynchronizationContext()
    {
        var original = SynchronizationContext.Current;
        var context = new QueuedSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var command = new AsyncRelayCommand(() => Task.CompletedTask);
            var eventThread = -1;
            command.CanExecuteChanged += (_, _) => eventThread = Environment.CurrentManagedThreadId;

            using var workerFinished = new ManualResetEventSlim();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                command.RaiseCanExecuteChanged();
                workerFinished.Set();
            });
            Assert.True(workerFinished.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(-1, eventThread);
            context.Drain();
            Assert.Equal(Environment.CurrentManagedThreadId, eventThread);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    [Fact]
    public async Task AsyncRelayCommandOwnsAndObservesWorkflowTask()
    {
        var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("expected test failure")));

        command.Execute(null);
        var task = command.ExecutionTask;
        Assert.NotNull(task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => task!);
    }

    [Fact]
    public void InitializeUsesOwnedAsyncCommandAndUiMarshaledStatePublication()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var relayCommand = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "Commands", "RelayCommand.cs");
        var workspace = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitWorkspaceViewModel.cs");

        Assert.Contains("new AsyncRelayCommand(() => InitializeRepositoryAsync()", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = InitializeRepositoryAsync()", viewModel, StringComparison.Ordinal);
        Assert.Contains("await PostToUiAsync(() =>", viewModel, StringComparison.Ordinal);
        Assert.Contains("CurrentGitOperation = GitOperationKind.Initialize", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsGitMutationInProgress = true", viewModel, StringComparison.Ordinal);
        Assert.Contains("_uiSynchronizationContext.Post(_ => RefreshGitCommands()", viewModel, StringComparison.Ordinal);
        Assert.Contains("RaiseCanExecuteChanged", relayCommand, StringComparison.Ordinal);
        Assert.Contains("new AsyncRelayCommand", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void InitializePublishesOperationBeforeBusyAndClearsBothOnUiContext()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var identityIndex = viewModel.IndexOf("CurrentGitOperation = GitOperationKind.Initialize", StringComparison.Ordinal);
        var busyIndex = viewModel.IndexOf("IsGitMutationInProgress = true", identityIndex, StringComparison.Ordinal);
        var cleanupIndex = viewModel.IndexOf("IsGitMutationInProgress = false", busyIndex, StringComparison.Ordinal);
        var clearIdentityIndex = viewModel.IndexOf("CurrentGitOperation = GitOperationKind.None", cleanupIndex, StringComparison.Ordinal);

        Assert.True(identityIndex >= 0);
        Assert.True(busyIndex > identityIndex);
        Assert.True(cleanupIndex > busyIndex);
        Assert.True(clearIdentityIndex > cleanupIndex);
        Assert.Contains("command-requery-raised", viewModel, StringComparison.Ordinal);
        Assert.Contains("managedThreadId", viewModel, StringComparison.Ordinal);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback d, object? state)
            => _callbacks.Enqueue((d, state));

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var callback))
                callback.Callback(callback.State);
        }
    }
}
