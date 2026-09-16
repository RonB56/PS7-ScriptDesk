namespace PS7ScriptDesk.Tests;

public sealed class GitRemoteFailurePresentationTests
{
    [Fact]
    public void PullAndSyncNoUpstreamPreflightUseTheEstablishedWarningDialogPath()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var method = source[source.IndexOf("private async Task<bool> EnsureRemoteAvailableAsync", StringComparison.Ordinal)..source.IndexOf("private async Task RunRemoteMutationAsync", StringComparison.Ordinal)];

        Assert.Contains("StatusText = $\"{operation} failed\"", method, StringComparison.Ordinal);
        Assert.Contains("_userPromptService.ShowWarningMessage($\"{operation} failed\", message)", method, StringComparison.Ordinal);
        Assert.Contains("requireUpstream", method, StringComparison.Ordinal);
        Assert.Contains("userNotificationDestination", method, StringComparison.Ordinal);
    }

    [Fact]
    public void PushKeepsItsExistingMutationFailureDialogPath()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var method = source[source.IndexOf("private async Task<bool> RunGitMutationAsync", StringComparison.Ordinal)..source.IndexOf("private static string DescribeGitFailure", StringComparison.Ordinal)];

        Assert.Contains("_userPromptService.ShowWarningMessage($\"{operationName} failed\", DescribeGitFailure", method, StringComparison.Ordinal);
        Assert.Contains("if (!result.Success)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchNoRemoteStillUsesPersistentInformationalStateWithoutChangingGitSemantics()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var method = source[source.IndexOf("private async Task<bool> EnsureRemoteAvailableAsync", StringComparison.Ordinal)..source.IndexOf("private async Task RunRemoteMutationAsync", StringComparison.Ordinal)];

        Assert.Contains("remoteState.Remotes.Count == 0", method, StringComparison.Ordinal);
        Assert.Contains("persistent remote summary remains the user-facing explanation", method, StringComparison.Ordinal);
        Assert.Contains("FetchAsync", source, StringComparison.Ordinal);
        Assert.Contains("requireUpstream: false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteCommandsRemainOnTheSingleExistingWorkspaceBackendAndClearBusyState()
    {
        var viewModel = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var remotes = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "GitRemotesViewModel.cs");

        Assert.Contains("finally", viewModel, StringComparison.Ordinal);
        Assert.Contains("_isRemoteOperationInProgress = false", viewModel, StringComparison.Ordinal);
        Assert.Contains("ExecuteGitCommandSafelyAsync", remotes, StringComparison.Ordinal);
        Assert.DoesNotContain("new GitService", viewModel, StringComparison.Ordinal);
    }
}
