using System.Text;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredEchoFilterContractTests
{
    [Fact]
    public void PromptPrefixedMultilineEcho_IsSuppressedWithoutSuppressingOutput()
    {
        const string script = "Write-Output \"CLEAN_START_20\"\n# clean filler 1\n# clean filler 2\nWrite-Output \"CLEAN_DONE_20\"";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var raw = "PS C:\\Work> " + script.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nCLEAN_START_20\r\nCLEAN_DONE_20\r\nPS C:\\Work> ";

        var visible = new StringBuilder(filter.Filter(raw));
        visible.Append(filter.Flush());

        Assert.DoesNotContain("CLEAN_START_20\"", visible.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("# clean filler", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("CLEAN_START_20\r\nCLEAN_DONE_20", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("PS C:\\Work> ", visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptPrefixedEchoSplitAcrossChunks_IsSuppressed()
    {
        const string script = "Write-Output \"START\"\n# filler\nWrite-Output \"DONE\"";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var raw = "PS C:\\Work> " + script.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nSTART\r\nDONE\r\n";
        var split = raw.IndexOf("# filler", StringComparison.Ordinal);
        var visible = new StringBuilder(filter.Filter(raw[..split]));
        visible.Append(filter.Filter(raw[split..]));
        visible.Append(filter.Flush());

        Assert.DoesNotContain("# filler", visible.ToString(), StringComparison.Ordinal);
        Assert.Contains("START\r\nDONE", visible.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptPrefixedEchoWithCollapsedLineWhitespace_IsSuppressed()
    {
        const string script = "param(\n    [int]$DurationSeconds = 3\n)\nWrite-Output 'DONE'";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var echoed = "PS C:\\Work> param( [int]$DurationSeconds = 3 ) Write-Output 'DONE'\r\nDONE\r\n";

        var visible = filter.Filter(echoed) + filter.Flush();

        Assert.DoesNotContain("DurationSeconds", visible, StringComparison.Ordinal);
        Assert.Contains("DONE\r\n", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptPrefixedWrappedEcho_IsHeldUntilTheWholeEchoCanBeRemoved()
    {
        const string script = "param(\n    [int]$DurationSeconds = 3\n)\nWrite-Output 'DONE'";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);
        var firstChunk = "PS C:\\Work> param( [int]$DurationSeconds =\r\n 3 ) Write-Output 'DONE'\r\n";

        Assert.Empty(filter.Filter(firstChunk));
        var visible = filter.Filter("DONE\r\n") + filter.Flush();

        Assert.DoesNotContain("DurationSeconds", visible, StringComparison.Ordinal);
        Assert.Contains("DONE\r\n", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIdenticalLegitimateOutput_RemainsVisibleWithoutPromptPrefix()
    {
        const string script = "Write-Output 'source-identical'";
        var filter = new PsesOwnedInteractiveSessionService.StructuredEchoFilter(Guid.NewGuid(), script);

        var visible = filter.Filter("source-identical\r\n") + filter.Flush();

        Assert.Equal("source-identical\r\n", visible);
    }
}
