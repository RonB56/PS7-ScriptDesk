namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Converts one-based diagnostic ranges into safe UTF-16 editor offsets.</summary>
public static class ScriptDiagnosticRangeMapper
{
    public static (int StartOffset, int EndOffset) Map(string? text, int startLine, int startColumn, int endLine, int endColumn)
    {
        var source = text ?? string.Empty;
        var starts = GetLineStarts(source);
        var safeStartLine = Math.Clamp(startLine, 1, starts.Count);
        var safeEndLine = Math.Clamp(endLine, safeStartLine, starts.Count);
        var start = ClampColumn(source, starts, safeStartLine, startColumn);
        var end = ClampColumn(source, starts, safeEndLine, endColumn);
        if (end < start)
        {
            end = start;
        }
        return (start, end);
    }

    private static int ClampColumn(string text, IReadOnlyList<int> starts, int line, int column)
    {
        var start = starts[line - 1];
        var next = line < starts.Count ? starts[line] : text.Length;
        var lineLength = next - start;
        while (lineLength > 0 && (text[start + lineLength - 1] == '\r' || text[start + lineLength - 1] == '\n'))
        {
            lineLength--;
        }
        return start + Math.Clamp(Math.Max(0, column - 1), 0, lineLength);
    }

    private static List<int> GetLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n') starts.Add(index + 1);
        }
        return starts;
    }
}
