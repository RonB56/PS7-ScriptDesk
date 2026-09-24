using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class NumberedFixtureParserTests
{
    [Fact]
    public void ParsesZeroPaddedNumber()
    {
        var parser = new NumberedFixtureParser();
        var result = parser.Process("PHASEB_INTERRUPT_TICK 00000135\r\n");
        Assert.Equal([135], result.Select(item => item.SequenceNumber));
    }

    [Fact]
    public void ParsesEveryRecordInOneChunk()
    {
        var parser = new NumberedFixtureParser();
        var result = parser.Process("PHASEB_INTERRUPT_TICK 00000001\nnoise\nPHASEB_INTERRUPT_TICK 00000002\n");
        Assert.Equal([1, 2], result.Select(item => item.SequenceNumber));
    }

    [Fact]
    public void CompletesRecordSplitAcrossReads()
    {
        var parser = new NumberedFixtureParser();
        Assert.Empty(parser.Process("PHASEB_INTERRUPT_TICK 00000"));
        var result = parser.Process("135\r\n");
        Assert.Equal([135], result.Select(item => item.SequenceNumber));
    }

    [Fact]
    public void AcceptsAnsiAroundFixtureAndCrLf()
    {
        var parser = new NumberedFixtureParser();
        var result = parser.Process("\x1b[2KPHASEB_INTERRUPT_TICK 00000007\r\n\x1b[1A");
        Assert.Equal([7], result.Select(item => item.SequenceNumber));
    }

    [Theory]
    [InlineData("PHASEB_INTERRUPT_TICK x\n")]
    [InlineData("PHASEB_INTERRUPT_TICK 12345678901\n")]
    [InlineData("XPHASEB_INTERRUPT_TICK 4\n")]
    public void RejectsMalformedOrEmbeddedFixtureLines(string input)
    {
        var parser = new NumberedFixtureParser();
        Assert.Empty(parser.Process(input));
    }

    [Fact]
    public void RetainsOnlyParsedNumberMetadata()
    {
        var result = new NumberedFixtureParser().Process("\x1b[2KPHASEB_INTERRUPT_TICK 00000009\n");
        Assert.Single(result);
        Assert.Equal(9, result[0].SequenceNumber);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
