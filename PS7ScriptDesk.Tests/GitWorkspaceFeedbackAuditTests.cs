namespace PS7ScriptDesk.Tests;

public sealed class GitWorkspaceFeedbackAuditTests
{
    [Fact]
    public void DirtyDocumentBranchSwitchUsesConciseHeaderAndProductDialogWithoutChangingTheGuard()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var start = source.IndexOf("public async Task<GitCommandResult?> SwitchBranchAsync", StringComparison.Ordinal);
        var end = source.IndexOf("public Task<GitCommandResult?> CreateBranchAsync", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("var safetySnapshot = OpenTabs", method, StringComparison.Ordinal);
        Assert.Contains("var blockingTabs = safetySnapshot.Where(item => item.Tab.IsDirty)", method, StringComparison.Ordinal);
        Assert.Contains("if (blockingTabs.Length > 0)", method, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"Branch switch blocked\"", method, StringComparison.Ordinal);
        Assert.Contains("ShowWarningMessage(\"Cannot switch branch\", message)", method, StringComparison.Ordinal);
        Assert.Contains("[\"gitSwitchInvoked\"] = false", method, StringComparison.Ordinal);
    }

    [Fact]
    public void FeedbackAuditRetainsPersistentWarningsAndDestructiveConfirmationPaths()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("No remotes configured. Add a remote before using Fetch, Pull, Push, or Sync.", TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitRemotesViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("ShowWarningMessage(\"Discard blocked\", message)", source, StringComparison.Ordinal);
        Assert.Contains("ShowConfirmation(\"Delete Local Branch\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionableTemporaryBlocksStayClickableAndRetainExecutionGuards()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var branches = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitBranchesViewModel.cs");

        Assert.DoesNotContain("_actions.CanSwitchBranches &&", branches, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"Pull blocked\"", main, StringComparison.Ordinal);
        Assert.Contains("ShowWarningMessage(\"Cannot pull\"", main, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"Sync blocked\"", main, StringComparison.Ordinal);
        Assert.Contains("ShowWarningMessage(\"Cannot sync\"", main, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"Commit blocked\"", main, StringComparison.Ordinal);
        Assert.Contains("StatusText = \"Discard blocked\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("() => CanGitMutation() && CanSwitchBranches", main, StringComparison.Ordinal);
    }
}
