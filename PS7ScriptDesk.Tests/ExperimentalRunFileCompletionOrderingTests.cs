using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ExperimentalRunFileCompletionOrderingTests
{
    [Fact]
    public async Task OutputAndNativePromptCompleteRequestWhilePsesResponseIsPending()
    {
        await using var fixture = await PsesAvDeterministicTransportTests.ConnectedFixture.CreateAsync();
        var requestId = Guid.NewGuid();
        var evaluation = fixture.Client.EvaluateAsync(requestId, "& { run-snapshot; write-status }");
        using var request = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        var prompt = new PromptRestorationSignal(17, requireInvocationEcho: false);

        // The request-owned status boundary and native prompt arrive while the
        // protocol response and post-evaluate idle notification are both absent.
        prompt.ObserveStatusFileVisible();
        prompt.ObservePrompt(17, statusFileExists: true, invocationEchoRemoved: true, cursorVisible: true);

        Assert.True(await prompt.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(evaluation.IsCompleted);
        Assert.Equal(1, fixture.Client.PendingRequestCount);

        await fixture.WriteResponseAsync(request.RootElement.GetProperty("id").GetInt32());
        await fixture.SendNotificationAsync("powerShell/executionBusyStatus", false);
        Assert.Equal(EditorExecutionStatus.Completed, (await evaluation.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(0, fixture.Client.PendingRequestCount);
    }

    [Fact]
    public async Task PsesResponseBeforePromptDoesNotCompleteTerminalContractEarly()
    {
        await using var fixture = await PsesAvDeterministicTransportTests.ConnectedFixture.CreateAsync();
        var evaluation = fixture.Client.EvaluateAsync(Guid.NewGuid(), "& { run-snapshot; write-status }");
        using var request = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.SendNotificationAsync("powerShell/executionBusyStatus", true);
        await fixture.WriteResponseAsync(request.RootElement.GetProperty("id").GetInt32());
        await fixture.SendNotificationAsync("powerShell/executionBusyStatus", false);
        Assert.Equal(EditorExecutionStatus.Completed, (await evaluation.WaitAsync(TimeSpan.FromSeconds(5))).Status);

        var prompt = new PromptRestorationSignal(4, requireInvocationEcho: false);
        Assert.False(prompt.Snapshot().Completed);
        prompt.ObserveStatusFileVisible();
        prompt.ObservePrompt(4, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true);
        Assert.True(await prompt.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task StatusBeforeProtocolResponseIsAcceptedOnlyAfterPostStatusPrompt()
    {
        await using var fixture = await PsesAvDeterministicTransportTests.ConnectedFixture.CreateAsync();
        var evaluation = fixture.Client.EvaluateAsync(Guid.NewGuid(), "& { run-snapshot; write-status }");
        using var request = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        var prompt = new PromptRestorationSignal(8, requireInvocationEcho: false);
        prompt.ObserveStatusFileVisible();
        Assert.False(prompt.Snapshot().Completed);
        prompt.ObservePrompt(8, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true);
        Assert.True(prompt.Snapshot().Completed);
        Assert.False(evaluation.IsCompleted);

        await fixture.WriteResponseAsync(request.RootElement.GetProperty("id").GetInt32());
        await fixture.SendNotificationAsync("powerShell/executionBusyStatus", false);
        Assert.Equal(EditorExecutionStatus.Completed, (await evaluation.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task MissingPostEvaluateIdleDoesNotUndoIndependentTerminalEvidence()
    {
        await using var fixture = await PsesAvDeterministicTransportTests.ConnectedFixture.CreateAsync();
        var evaluation = fixture.Client.EvaluateAsync(Guid.NewGuid(), "& { run-snapshot; write-status }");
        using var request = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        var prompt = new PromptRestorationSignal(11, requireInvocationEcho: false);
        prompt.ObserveStatusFileVisible();
        prompt.ObservePrompt(11, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true);
        await fixture.WriteResponseAsync(request.RootElement.GetProperty("id").GetInt32());

        Assert.True(await prompt.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(EditorExecutionStatus.Unknown, (await evaluation.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task ExperimentalPromptVerificationDoesNotRequireExactMultilineEcho()
    {
        var experimental = new PromptRestorationSignal(9, requireInvocationEcho: false);
        experimental.ObserveStatusFileVisible();
        experimental.ObservePrompt(9, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true);
        Assert.True(await experimental.Completion.WaitAsync(TimeSpan.FromSeconds(1)));

        var legacy = new PromptRestorationSignal(9);
        legacy.ObserveStatusFileVisible();
        legacy.ObservePrompt(9, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true);
        Assert.False(legacy.Snapshot().Completed);
    }

    [Fact]
    public async Task CancellationOfPendingExperimentalInvocationSettlesOwnedProtocolRequest()
    {
        await using var fixture = await PsesAvDeterministicTransportTests.ConnectedFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var evaluation = fixture.Client.EvaluateAsync(Guid.NewGuid(), "& { run-snapshot; write-status }", cancellation.Token);
        using var request = await fixture.ServerReadTask.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = new PromptRestorationSignal(12, requireInvocationEcho: false);
        Assert.False(pending.Snapshot().Completed);
        cancellation.Cancel();
        using var cancelRequest = await fixture.ReadNextServerMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("$/cancelRequest", cancelRequest.RootElement.GetProperty("method").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.Equal(0, fixture.Client.PendingRequestCount);
        Assert.False(pending.Snapshot().Completed);
    }

    [Fact]
    public void RuntimeGenerationReplacementCannotSatisfyTerminalEvidence()
    {
        var prompt = new PromptRestorationSignal(12, requireInvocationEcho: false);
        prompt.ObserveStatusFileVisible();
        prompt.ObservePrompt(13, statusFileExists: true, invocationEchoRemoved: false, cursorVisible: true);
        Assert.False(prompt.Snapshot().Completed);
    }
}
