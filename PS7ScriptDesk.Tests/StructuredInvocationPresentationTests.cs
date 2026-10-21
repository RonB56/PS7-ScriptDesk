using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredInvocationPresentationTests
{
    [Fact]
    public async Task RunFileInvocation_IsRemoved_WhilePromptOutputAndMarkerRemain()
    {
        var invocation = "& 'C:\\Users\\rbarn\\AppData\\Local\\PS7ScriptDesk\\StructuredExecution\\run.wrapper.ps1'";
        var begin = "BEGIN_run";
        var end = "END_run_";
        var state = CreateState(invocation, begin, end);

        var visible = Process(state, $"PS C:\\> {invocation}\r\n{begin}\r\nPHASE2C_RUN_FILE_OK\r\n{end}OK\r\nPS C:\\> ");

        Assert.Equal("PS C:\\> \r\nPHASE2C_RUN_FILE_OK\r\nPS C:\\> ", visible);
        Assert.Equal("OK", (await state.Completion.Task).Status);
    }

    [Fact]
    public void RunSelectionInvocation_IsRemoved_Exactly()
    {
        var invocation = ". 'C:\\Users\\rbarn\\AppData\\Local\\PS7ScriptDesk\\StructuredExecution\\selection.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_selection", "END_selection_");

        var visible = Process(state, $"PS C:\\> {invocation}\nBEGIN_selection\nRUN_SELECTION_STATE=AUTHORITATIVE_SESSION\nEND_selection_OK\nPS C:\\> ");

        Assert.Equal("PS C:\\> \nRUN_SELECTION_STATE=AUTHORITATIVE_SESSION\nPS C:\\> ", visible);
    }

    [Fact]
    public void InvocationSplitAcrossChunks_IsRemovedWithoutLeakingFragments()
    {
        var invocation = "& 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_split", "END_split_");
        var input = $"PS C:\\> {invocation}\r\nBEGIN_split\r\noutput\r\nEND_split_OK\r\nPS C:\\> ";
        var visible = string.Concat(input.Chunk(3).Select(chunk => Process(state, new string(chunk))));

        Assert.Equal("PS C:\\> \r\noutput\r\nPS C:\\> ", visible);
        Assert.DoesNotContain(invocation, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void InvocationWithInterleavedAnsiControlSequences_IsRemovedWithoutLeakingFragments()
    {
        var invocation = "& 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_ansi_echo", "END_ansi_echo_");
        var echoedInvocation = "& \u001b[?25l'\u001b[?25hC:\\structured.\u001b[0mwrapper.ps1'";

        var visible = Process(state, $"PS C:\\> {echoedInvocation}\r\nBEGIN_ansi_echo\r\noutput\r\nEND_ansi_echo_OK\r\nPS C:\\> ");

        Assert.Equal("PS C:\\> \r\noutput\r\nPS C:\\> ", visible);
    }

    [Fact]
    public void RepeatedRedrawEchoes_AreAllRemovedWithoutRemovingLineTransitions()
    {
        var invocation = "& 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_redraw", "END_redraw_");
        var input = $"PS C:\\> {invocation}\r\n\u001b[2KPS C:\\> \u001b[1G{invocation}\r\nBEGIN_redraw\r\noutput\r\nEND_redraw_OK\r\nPS C:\\> ";

        var visible = Process(state, input);

        Assert.DoesNotContain(invocation, visible, StringComparison.Ordinal);
        Assert.Contains("PS C:\\> \r\n", visible, StringComparison.Ordinal);
        Assert.Contains("output\r\nPS C:\\> ", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void InvocationImmediatelyAdjacentToBeginMarker_IsRemovedWithoutRemovingUserOutput()
    {
        var invocation = "& 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_adjacent", "END_adjacent_");

        var visible = Process(state, $"PS C:\\> {invocation}BEGIN_adjacent\r\noutput\r\nEND_adjacent_OK\r\nPS C:\\> ");

        Assert.Equal("PS C:\\> output\r\nPS C:\\> ", visible);
    }

    [Fact]
    public void UserAmpersandCommand_RemainsVisible()
    {
        var invocation = "& 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_user", "END_user_");
        var userCommand = "& cmd.exe /c \"echo USER_COMMAND_VISIBLE\"";

        var visible = Process(state, $"PS C:\\> {userCommand}\r\nBEGIN_user\r\noutput\r\nEND_user_OK\r\nPS C:\\> ");

        Assert.Contains(userCommand, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void UserDotSourceCommandAndWrapperLikeOutput_RemainVisible()
    {
        var invocation = ". 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_user2", "END_user2_");
        var userCommand = ". 'C:\\user.wrapper.ps1'";
        var userOutput = "user output mentions .wrapper.ps1";

        var visible = Process(state, $"PS C:\\> {userCommand}\r\nBEGIN_user2\r\n{userOutput}\r\nEND_user2_OK\r\nPS C:\\> ");

        Assert.Contains(userCommand, visible, StringComparison.Ordinal);
        Assert.Contains(userOutput, visible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnsiAndErrorsArePreserved_AndCompletionOccursOnce()
    {
        var invocation = "& 'C:\\structured.wrapper.ps1'";
        var state = CreateState(invocation, "BEGIN_ansi", "END_ansi_");
        var ansi = "\u001b[31mERROR\u001b[0m";

        var visible = Process(state, $"PS C:\\> {invocation}\r\nBEGIN_ansi\r\n{ansi}\r\nEND_ansi_ERROR\r\nPS C:\\> ");

        Assert.Contains(ansi, visible, StringComparison.Ordinal);
        var completion = await state.Completion.Task;
        Assert.Equal("ERROR", completion.Status);
    }

    [Fact]
    public void CancellationStateRemainsAvailableAfterPresentationFiltering()
    {
        var state = CreateState("& 'C:\\structured.wrapper.ps1'", "BEGIN_cancel", "END_cancel_");

        state.RequestCancellation();

        Assert.True(state.CancellationRequested);
        Assert.False(state.Completion.Task.IsCompleted);
    }

    private static PsesOwnedInteractiveSessionService.StructuredExecutionState CreateState(
        string invocation,
        string begin,
        string endPrefix)
        => new(Guid.NewGuid(), 7, begin, endPrefix, invocation);

    private static string Process(PsesOwnedInteractiveSessionService.StructuredExecutionState state, string input)
        => string.Concat(state.Process(input));
}
