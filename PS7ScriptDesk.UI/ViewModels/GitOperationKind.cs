namespace PS7ScriptDesk.UI.ViewModels;

public enum GitOperationKind
{
    None, Refresh, Clone, Fetch, Pull, Push, Sync, Initialize, SwitchBranch, Commit, Stage, Unstage, Discard, Branch
}

public static class GitOperationPresentation
{
    public static string GetBusyText(GitOperationKind operation) => operation switch
    {
        GitOperationKind.Refresh => "Refreshing repository...",
        GitOperationKind.Clone => "Cloning repository...",
        GitOperationKind.Fetch => "Fetching...",
        GitOperationKind.Pull => "Pulling...",
        GitOperationKind.Push => "Pushing...",
        GitOperationKind.Sync => "Syncing...",
        GitOperationKind.Initialize => "Initializing repository...",
        GitOperationKind.SwitchBranch => "Switching branch...",
        GitOperationKind.Commit => "Committing changes...",
        GitOperationKind.Stage => "Staging changes...",
        GitOperationKind.Unstage => "Unstaging changes...",
        GitOperationKind.Discard => "Discarding changes...",
        GitOperationKind.Branch => "Updating branch...",
        _ => "Git operation in progress..."
    };
}
