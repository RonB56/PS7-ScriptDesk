using System.Text;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredStatusMarkerContractTests
{
    [Fact]
    public async Task FragmentedStatusMarker_IsRemovedWithoutLosingAdjacentOutput()
    {
        const string marker = "__PS7SD_STATUS_END_123__";
        var state = new PsesOwnedInteractiveSessionService.StructuredExecutionState(
            Guid.NewGuid(), 1, string.Empty, marker, "private invocation");
        var visible = new StringBuilder();

        visible.Append(string.Concat(state.Process("before\r\n__PS7SD_STATUS_EN")));
        visible.Append(string.Concat(state.Process("D_123__OK\r")));
        visible.Append(string.Concat(state.Process("\nafter\r\n")));
        visible.Append(state.Flush());

        Assert.True(state.Completion.Task.IsCompletedSuccessfully);
        Assert.Equal("OK", (await state.Completion.Task).Status);
        Assert.Contains("before\r\n", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("after\r\n", visible.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SimilarUserOutput_DoesNotCompletePrivateStatusMarker()
    {
        const string marker = "__PS7SD_STATUS_END_123__";
        var state = new PsesOwnedInteractiveSessionService.StructuredExecutionState(
            Guid.NewGuid(), 1, string.Empty, marker, "private invocation");

        var visible = string.Concat(state.Process("__PS7SD_STATUS_END_456__OK\r\n")) + state.Flush();

        Assert.False(state.Completion.Task.IsCompleted);
        Assert.Contains("__PS7SD_STATUS_END_456__OK", visible, StringComparison.Ordinal);
    }
}
