using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class GitWorkspaceCoordinator : IGitWorkspaceCoordinator
{
    private readonly object _gate = new();
    private GitRepositoryState? _currentState;
    private GitBranchState? _currentBranchState;
    private GitRemoteState? _currentRemoteState;

    public GitWorkspaceCoordinator(IGitService gitService)
        => GitService = gitService ?? throw new ArgumentNullException(nameof(gitService));

    public IGitService GitService { get; }

    public GitRepositoryState? CurrentState
    {
        get { lock (_gate) return _currentState; }
    }
    public GitBranchState? CurrentBranchState { get { lock (_gate) return _currentBranchState; } }
    public GitRemoteState? CurrentRemoteState { get { lock (_gate) return _currentRemoteState; } }

    public event EventHandler<GitRepositoryState?>? StateChanged;
    public event EventHandler<GitBranchState?>? BranchStateChanged;
    public event EventHandler<GitRemoteState?>? RemoteStateChanged;
    public event EventHandler? RefreshRequested;

    public void PublishState(GitRepositoryState? state)
    {
        lock (_gate) _currentState = state;
        StateChanged?.Invoke(this, state);
    }

    public void PublishBranchState(GitBranchState? state)
    {
        lock (_gate) _currentBranchState = state;
        BranchStateChanged?.Invoke(this, state);
    }

    public void PublishRemoteState(GitRemoteState? state)
    {
        lock (_gate) _currentRemoteState = state;
        RemoteStateChanged?.Invoke(this, state);
    }

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);
}
