using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IGitWorkspaceCoordinator
{
    IGitService GitService { get; }
    GitRepositoryState? CurrentState { get; }
    GitBranchState? CurrentBranchState { get; }
    GitRemoteState? CurrentRemoteState { get; }
    event EventHandler<GitRepositoryState?>? StateChanged;
    event EventHandler<GitBranchState?>? BranchStateChanged;
    event EventHandler<GitRemoteState?>? RemoteStateChanged;
    event EventHandler? RefreshRequested;
    void PublishState(GitRepositoryState? state);
    void PublishBranchState(GitBranchState? state);
    void PublishRemoteState(GitRemoteState? state);
    void RequestRefresh();
}
