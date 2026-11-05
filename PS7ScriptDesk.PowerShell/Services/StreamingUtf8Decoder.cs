using System.Text;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Preserves partial UTF-8 sequences across ConPTY stream reads.</summary>
internal sealed class StreamingUtf8Decoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _characters = new char[Encoding.UTF8.GetMaxCharCount(8192)];

    public string Decode(byte[] buffer, int count)
    {
        var length = _decoder.GetChars(buffer, 0, count, _characters, 0, flush: false);
        return new string(_characters, 0, length);
    }

    public string Flush()
    {
        var length = _decoder.GetChars(Array.Empty<byte>(), 0, 0, _characters, 0, flush: true);
        return new string(_characters, 0, length);
    }
}
