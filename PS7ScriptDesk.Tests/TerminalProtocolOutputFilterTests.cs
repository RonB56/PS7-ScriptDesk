using System.Reflection;
using System.Text;
using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalProtocolOutputFilterTests
{
    [Fact]
    public void GetDateStyleOutput_DropsPrivateFramesAndForwardsPromptImmediately()
    {
        const string start = "##PSSTUDIO_EXEC_START_0123456789abcdef0123456789abcdef";
        const string done = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";
        const string location = "##PSSTUDIO_LOCATION_0123456789abcdef0123456789abcdef_QzpcV29yaw==";
        const string prompt = "PS C:\\Users\\rbarn> ";

        var visible = Filter(
            "& 'C:\\TerminalSnapshots\\psh-0123456789abcdef0123456789abcdef.ps1' 'C:\\TerminalSnapshots\\psi-fedcba9876543210fedcba9876543210.ps1'\r\n",
            start + "\r\n",
            "Sunday, August 30, 2026 10:25:58 AM\r\n",
            location + "\r\n",
            done + "\r\n",
            prompt);

        Assert.Equal("\r\n\r\nSunday, August 30, 2026 10:25:58 AM\r\n\r\n\r\n" + prompt, visible);
        Assert.DoesNotContain("PSSTUDIO", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalSnapshots", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptWithoutTrailingNewline_IsNotHeldForManualEnter()
    {
        var filter = new TerminalProtocolOutputFilter();

        var result = filter.Process("Sunday, August 30, 2026\r\nPS C:\\> ");

        Assert.Equal("Sunday, August 30, 2026\r\nPS C:\\> ", result.VisibleText);
        Assert.Equal(string.Empty, filter.Flush().VisibleText);
    }

    [Fact]
    public void ExecDoneAndPromptInOneChunk_KeepNormalSpacing()
    {
        const string done = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";

        var visible = Filter("output\r\n" + done + "\r\nPS C:\\> ");

        Assert.Equal("output\r\n\r\nPS C:\\> ", visible);
        Assert.Equal(2, visible.Count(character => character == '\n'));
    }

    [Fact]
    public void ExecDoneSplitAcrossChunks_IsConsumedBeforePromptArrives()
    {
        const string done = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";
        var filter = new TerminalProtocolOutputFilter();

        var first = filter.Process("output\r\n" + done[..17]).VisibleText;
        var second = filter.Process(done[17..] + "\r\nPS C:\\> ").VisibleText;

        Assert.Equal("output\r\n", first);
        Assert.Equal("\r\nPS C:\\> ", second);
    }

    [Fact]
    public void CarriageReturnProgressOutput_RemainsCharacterAccurate()
    {
        const string progress = "Progress 10%\rProgress 50%\rProgress 100%\r\nPS C:\\> ";

        Assert.Equal(progress, Filter(progress));
    }

    [Fact]
    public void ProtocolRecordsAndGeneratedSnapshotCommand_AreRemovedBeforeRendering()
    {
        const string start = "##PSSTUDIO_EXEC_START_0123456789abcdef0123456789abcdef";
        const string done = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";
        const string location = "##PSSTUDIO_LOCATION_0123456789abcdef0123456789abcdef_QzpcV29yaw==";
        const string diagnostic = "##PSSTUDIO_DISPATCH_DIAG## begin pid=42 apartment=STA";
        const string command = "& 'C:\\Users\\rbarn\\AppData\\Local\\PS7ScriptDesk\\Temp\\TerminalSnapshots\\psh-0123456789abcdef0123456789abcdef.ps1' 'C:\\Users\\rbarn\\AppData\\Local\\PS7ScriptDesk\\Temp\\TerminalSnapshots\\psi-fedcba9876543210fedcba9876543210.ps1'";

        var visible = Filter(
            $"{command}\r\n{start}\r\n{diagnostic}\r\n{location}\r\n{done}\r\nuser output\r\n");

        Assert.Equal(string.Concat(Enumerable.Repeat("\r\n", 5)) + "user output\r\n", visible);
        Assert.DoesNotContain("PSSTUDIO", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalSnapshots", visible, StringComparison.Ordinal);
    }


    [Fact]
    public void ShortDispatchSnapshotCommand_IsHiddenButPreservesLineAdvance()
    {
        const string command = "& 'C:\\Users\\rbarn\\AppData\\Local\\PS7ScriptDesk\\Temp\\TerminalSnapshots\\psd-0123456789abcdef0123456789abcdef.ps1'";

        var visible = Filter(command + "\r\nuser output\r\n");

        Assert.Equal("\r\nuser output\r\n", visible);
        Assert.DoesNotContain("TerminalSnapshots", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCharacterBoundaryAcrossProtocolFrames_IsSafe()
    {
        var frames = new[]
        {
            "##PSSTUDIO_EXEC_START_0123456789abcdef0123456789abcdef\r\n",
            "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef\n",
            "##PSSTUDIO_DISPATCH_DIAG## finally pid=42\r",
            "##PSSTUDIO_LOCATION_0123456789abcdef0123456789abcdef_QzpcV29yaw==\r\n"
        };

        foreach (var frame in frames)
        {
            for (var split = 1; split < frame.Length; split++)
            {
                var filter = new TerminalProtocolOutputFilter();
                var output = new StringBuilder();
                output.Append(filter.Process("before\r\n" + frame[..split]).VisibleText);
                output.Append(filter.Process(frame[split..] + "after\r\n").VisibleText);
                output.Append(filter.Flush().VisibleText);

                var rendered = output.ToString();
                Assert.StartsWith("before\r\n", rendered, StringComparison.Ordinal);
                Assert.EndsWith("after\r\n", rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("PSSTUDIO", rendered, StringComparison.Ordinal);
                Assert.True(rendered.Length > "before\r\nafter\r\n".Length);
            }
        }
    }

    [Fact]
    public void FramesCanBeAdjacentToAnsiAndNormalOutput_WithoutLeakingOrReordering()
    {
        const string frame = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";
        var input = $"before\x1b[31mred\x1b[0m\r\n\x1b[32m{frame}\x1b[0m\r\nafter\x1b[34mblue\x1b[0m";

        var visible = Filter(input);

        Assert.Equal("before\x1b[31mred\x1b[0m\r\n\x1b[32m\x1b[0m\r\nafter\x1b[34mblue\x1b[0m", visible);
        Assert.DoesNotContain(frame, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateProtocolFiltering_CharacterizesRemovedAndPreservedControls()
    {
        const string frame = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";
        var filter = new TerminalProtocolOutputFilter();

        var result = filter.Process($"\u001b[32m{frame}\u001b[0m\r\n");

        Assert.Equal("\u001b[32m\u001b[0m\r\n", result.VisibleText);
        Assert.Equal(1, result.FilteredRecordCount);
        Assert.True(result.FilteredCharacters > 0);
        Assert.Equal(1, result.RemovedProtocolControlSummary.CarriageReturnCount);
        Assert.Equal(1, result.RemovedProtocolControlSummary.LineFeedCount);
        Assert.Equal(1, result.RemovedProtocolControlSummary.CarriageReturnLineFeedPairCount);
        Assert.Equal(2, result.RemovedProtocolControlSummary.CsiSgrCount);
        Assert.Equal(2, result.PreservedProtocolControlSummary.CsiSgrCount);
        Assert.Equal(1, result.PreservedProtocolControlSummary.CarriageReturnCount);
        Assert.Equal(1, result.PreservedProtocolControlSummary.LineFeedCount);
        Assert.Equal(1, result.PreservedProtocolControlSummary.CarriageReturnLineFeedPairCount);
    }

    [Fact]
    public void SimilarUserTextUnicodeAndLargeOutput_ArePreserved()
    {
        const string userText = "PSSTUDIO is legitimate user output\r\nprefix ##PSSTUDIO_EXEC_DONE_not-a-frame\r\n";
        const string unicode = "αβ 世界 ����\r\n";
        var largeText = new string('x', 200_000);
        var filter = new TerminalProtocolOutputFilter();
        var visible = new StringBuilder();

        foreach (var chunk in new[] { userText, unicode, largeText })
        {
            visible.Append(filter.Process(chunk).VisibleText);
            Assert.True(GetCarryLength(filter) <= 64 * 1024);
        }

        visible.Append(filter.Flush().VisibleText);

        Assert.Equal(userText + unicode + largeText, visible.ToString());
    }

    [Fact]
    public void RendererBoundaryFiltersBeforeFlowControllerAndResizeOnlyReportsGeometry()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        var filterIndex = source.IndexOf("_terminalProtocolOutputFilter.Process(data)", StringComparison.Ordinal);
        var enqueueIndex = source.IndexOf("_outputFlowController.Enqueue(generation, data)", StringComparison.Ordinal);

        Assert.True(filterIndex >= 0);
        Assert.True(enqueueIndex > filterIndex);
        Assert.Contains("private readonly TerminalProtocolOutputFilter", source, StringComparison.Ordinal);
        Assert.Contains("term.onResize(function (e)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("term.onResize(function (e) { term.write", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionBoundaryReset_DoesNotLeakAnIncompleteProtocolCarry()
    {
        var filter = new TerminalProtocolOutputFilter();
        filter.Process("##PSSTUDIO_EXEC_DONE_0123456789abcdef");
        filter.Reset();

        var visible = filter.Process("normal output\r\n").VisibleText + filter.Flush().VisibleText;

        Assert.Equal("normal output\r\n", visible);
        Assert.DoesNotContain("PSSTUDIO", visible, StringComparison.Ordinal);
    }

    private static string Filter(params string[] chunks)
    {
        var filter = new TerminalProtocolOutputFilter();
        var output = new StringBuilder();
        foreach (var chunk in chunks)
        {
            output.Append(filter.Process(chunk).VisibleText);
        }

        output.Append(filter.Flush().VisibleText);
        return output.ToString();
    }

    private static int GetCarryLength(TerminalProtocolOutputFilter filter)
    {
        var field = typeof(TerminalProtocolOutputFilter).GetField(
            "_carry",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<StringBuilder>(field!.GetValue(filter)).Length;
    }

    private static string ReadRepositoryFile(params string[] parts)
        => TestRepositoryPaths.ReadFile(parts);
}
