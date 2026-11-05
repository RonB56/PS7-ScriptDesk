using System.Text;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StreamingUtf8DecoderTests
{
    [Fact]
    public void MultibyteCharacterSplitAcrossReadsKeepsNativeAndAnsiOutputInOrder()
    {
        var decoder = new StreamingUtf8Decoder();
        var bytes = Encoding.UTF8.GetBytes("native \u001b[32m界🙂\u001b[0m done");
        var output = new StringBuilder();
        foreach (var value in bytes)
        {
            output.Append(decoder.Decode(new[] { value }, 1));
        }
        output.Append(decoder.Flush());

        Assert.Equal("native \u001b[32m界🙂\u001b[0m done", output.ToString());
    }

    [Fact]
    public void IncompleteFinalSequenceIsFlushedOnce()
    {
        var decoder = new StreamingUtf8Decoder();
        Assert.Empty(decoder.Decode(new byte[] { 0xE7, 0x95 }, 2));
        Assert.Equal("\uFFFD", decoder.Flush());
        Assert.Empty(decoder.Flush());
    }
}
