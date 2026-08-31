using System.Text;

namespace PS7ScriptDesk.Shell.Controls;

internal readonly record struct TerminalOutputControlSummary(
    int CarriageReturnCount,
    int LineFeedCount,
    int CarriageReturnLineFeedPairCount,
    int EscapeCount,
    int CsiCount,
    int CsiCursorUpCount,
    int CsiCursorDownCount,
    int CsiCursorForwardCount,
    int CsiCursorBackwardCount,
    int CsiCursorPositionCount,
    int CsiEraseLineCount,
    int CsiEraseDisplayCount,
    int CsiSaveCursorCount,
    int CsiRestoreCursorCount,
    int CsiInsertLineCount,
    int CsiDeleteLineCount,
    int CsiScrollUpCount,
    int CsiScrollDownCount,
    int CsiSgrCount,
    int CsiOtherCount,
    int OscCount,
    int OtherEscapeCount,
    int OtherControlCount,
    int PrintableCharacterCount)
{
    public static TerminalOutputControlSummary Empty { get; } = new();

    public TerminalOutputControlSummary Add(TerminalOutputControlSummary other) =>
        new(
            CarriageReturnCount + other.CarriageReturnCount,
            LineFeedCount + other.LineFeedCount,
            CarriageReturnLineFeedPairCount + other.CarriageReturnLineFeedPairCount,
            EscapeCount + other.EscapeCount,
            CsiCount + other.CsiCount,
            CsiCursorUpCount + other.CsiCursorUpCount,
            CsiCursorDownCount + other.CsiCursorDownCount,
            CsiCursorForwardCount + other.CsiCursorForwardCount,
            CsiCursorBackwardCount + other.CsiCursorBackwardCount,
            CsiCursorPositionCount + other.CsiCursorPositionCount,
            CsiEraseLineCount + other.CsiEraseLineCount,
            CsiEraseDisplayCount + other.CsiEraseDisplayCount,
            CsiSaveCursorCount + other.CsiSaveCursorCount,
            CsiRestoreCursorCount + other.CsiRestoreCursorCount,
            CsiInsertLineCount + other.CsiInsertLineCount,
            CsiDeleteLineCount + other.CsiDeleteLineCount,
            CsiScrollUpCount + other.CsiScrollUpCount,
            CsiScrollDownCount + other.CsiScrollDownCount,
            CsiSgrCount + other.CsiSgrCount,
            CsiOtherCount + other.CsiOtherCount,
            OscCount + other.OscCount,
            OtherEscapeCount + other.OtherEscapeCount,
            OtherControlCount + other.OtherControlCount,
            PrintableCharacterCount + other.PrintableCharacterCount);

    public string ToDiagnosticString()
    {
        var builder = new StringBuilder();
        Append(builder, "CR", CarriageReturnCount);
        Append(builder, "LF", LineFeedCount);
        Append(builder, "CRLF", CarriageReturnLineFeedPairCount);
        Append(builder, "ESC", EscapeCount);
        Append(builder, "CSI", CsiCount);
        Append(builder, "CSI_CursorUp", CsiCursorUpCount);
        Append(builder, "CSI_CursorDown", CsiCursorDownCount);
        Append(builder, "CSI_CursorForward", CsiCursorForwardCount);
        Append(builder, "CSI_CursorBackward", CsiCursorBackwardCount);
        Append(builder, "CSI_CursorPosition", CsiCursorPositionCount);
        Append(builder, "CSI_EraseLine", CsiEraseLineCount);
        Append(builder, "CSI_EraseDisplay", CsiEraseDisplayCount);
        Append(builder, "CSI_SaveCursor", CsiSaveCursorCount);
        Append(builder, "CSI_RestoreCursor", CsiRestoreCursorCount);
        Append(builder, "CSI_InsertLine", CsiInsertLineCount);
        Append(builder, "CSI_DeleteLine", CsiDeleteLineCount);
        Append(builder, "CSI_ScrollUp", CsiScrollUpCount);
        Append(builder, "CSI_ScrollDown", CsiScrollDownCount);
        Append(builder, "SGR", CsiSgrCount);
        Append(builder, "CSI_Other", CsiOtherCount);
        Append(builder, "OSC", OscCount);
        Append(builder, "ESC_Other", OtherEscapeCount);
        Append(builder, "OtherControl", OtherControlCount);
        Append(builder, "Printable", PrintableCharacterCount);
        return builder.Length == 0 ? "(none)" : builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, int value)
    {
        if (value <= 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(name).Append('=').Append(value);
    }
}

internal static class TerminalOutputControlClassifier
{
    public static TerminalOutputControlSummary Summarize(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return TerminalOutputControlSummary.Empty;
        }

        var cr = 0;
        var lf = 0;
        var crlf = 0;
        var esc = 0;
        var csi = 0;
        var csiCursorUp = 0;
        var csiCursorDown = 0;
        var csiCursorForward = 0;
        var csiCursorBackward = 0;
        var csiCursorPosition = 0;
        var csiEraseLine = 0;
        var csiEraseDisplay = 0;
        var csiSaveCursor = 0;
        var csiRestoreCursor = 0;
        var csiInsertLine = 0;
        var csiDeleteLine = 0;
        var csiScrollUp = 0;
        var csiScrollDown = 0;
        var csiSgr = 0;
        var csiOther = 0;
        var osc = 0;
        var otherEscape = 0;
        var otherControl = 0;
        var printable = 0;

        for (var index = 0; index < data.Length; index++)
        {
            var ch = data[index];
            if (ch == '\r')
            {
                cr++;
                if (index + 1 < data.Length && data[index + 1] == '\n')
                {
                    crlf++;
                }

                continue;
            }

            if (ch == '\n')
            {
                lf++;
                continue;
            }

            if (ch != '\x1b')
            {
                if (char.IsControl(ch))
                {
                    otherControl++;
                }
                else
                {
                    printable++;
                }

                continue;
            }

            esc++;
            if (index + 1 >= data.Length)
            {
                otherEscape++;
                continue;
            }

            var next = data[index + 1];
            if (next == '[')
            {
                var end = FindCsiEnd(data, index + 2);
                if (end < 0)
                {
                    otherEscape++;
                    continue;
                }

                csi++;
                switch (data[end])
                {
                    case 'A':
                        csiCursorUp++;
                        break;
                    case 'B':
                        csiCursorDown++;
                        break;
                    case 'C':
                        csiCursorForward++;
                        break;
                    case 'D':
                        csiCursorBackward++;
                        break;
                    case 'H':
                    case 'f':
                    case 'G':
                    case 'd':
                        csiCursorPosition++;
                        break;
                    case 'J':
                        csiEraseDisplay++;
                        break;
                    case 'K':
                        csiEraseLine++;
                        break;
                    case 's':
                        csiSaveCursor++;
                        break;
                    case 'u':
                        csiRestoreCursor++;
                        break;
                    case 'L':
                        csiInsertLine++;
                        break;
                    case 'M':
                        csiDeleteLine++;
                        break;
                    case 'S':
                        csiScrollUp++;
                        break;
                    case 'T':
                        csiScrollDown++;
                        break;
                    case 'm':
                        csiSgr++;
                        break;
                    default:
                        csiOther++;
                        break;
                }

                index = end;
                continue;
            }

            if (next == ']')
            {
                osc++;
                index = FindOscEnd(data, index + 2);
                continue;
            }

            switch (next)
            {
                case '7':
                    csiSaveCursor++;
                    break;
                case '8':
                    csiRestoreCursor++;
                    break;
                default:
                    otherEscape++;
                    break;
            }

            index++;
        }

        return new TerminalOutputControlSummary(
            cr,
            lf,
            crlf,
            esc,
            csi,
            csiCursorUp,
            csiCursorDown,
            csiCursorForward,
            csiCursorBackward,
            csiCursorPosition,
            csiEraseLine,
            csiEraseDisplay,
            csiSaveCursor,
            csiRestoreCursor,
            csiInsertLine,
            csiDeleteLine,
            csiScrollUp,
            csiScrollDown,
            csiSgr,
            csiOther,
            osc,
            otherEscape,
            otherControl,
            printable);
    }

    private static int FindCsiEnd(string data, int start)
    {
        for (var index = start; index < data.Length; index++)
        {
            if (data[index] >= '@' && data[index] <= '~')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindOscEnd(string data, int start)
    {
        for (var index = start; index < data.Length; index++)
        {
            if (data[index] == '\a')
            {
                return index;
            }

            if (data[index] == '\x1b' &&
                index + 1 < data.Length &&
                data[index + 1] == '\\')
            {
                return index + 1;
            }
        }

        return data.Length - 1;
    }
}
