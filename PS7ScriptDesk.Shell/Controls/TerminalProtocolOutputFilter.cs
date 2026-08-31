using System.Text;
using System.Text.RegularExpressions;

namespace PS7ScriptDesk.Shell.Controls;

internal readonly record struct TerminalProtocolFilterResult(
    string VisibleText,
    int FilteredCharacters,
    int FilteredRecordCount,
    TerminalOutputControlSummary RemovedProtocolControlSummary,
    TerminalOutputControlSummary PreservedProtocolControlSummary);

/// <summary>
/// Removes ScriptDesk-owned dispatch records before they enter the xterm buffer.
/// The filter is deliberately independent of execution state because a renderer
/// can be recreated after the service has already cleared that state.
/// </summary>
internal sealed class TerminalProtocolOutputFilter
{
    private const int MaximumCarryCharacters = 64 * 1024;
    private const string ExecStartPrefix = "##PSSTUDIO_EXEC_START_";
    private const string ExecDonePrefix = "##PSSTUDIO_EXEC_DONE_";
    private const string LocationPrefix = "##PSSTUDIO_LOCATION_";
    private const string DispatchDiagnosticPrefix = "##PSSTUDIO_DISPATCH_DIAG##";

    private static readonly Regex TerminalControlRegex = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\].*?(?:\x07|\x1B\\)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex GeneratedDispatchCommandRegex = new(
        @"^(?:PS\s+.+?>\s*)?(?:(?:&|\.)\s+'[^']*[\\/]TerminalSnapshots[\\/]psh-[0-9a-f]{32}\.ps1'\s+'[^']*[\\/]TerminalSnapshots[\\/]psi-[0-9a-f]{32}\.ps1'|(?:&|\.)\s+'[^']*[\\/]TerminalSnapshots[\\/]psd-[0-9a-f]{32}\.ps1')$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProtocolRecordRegex = new(
        @"^(?:##PSSTUDIO_EXEC_(?:START|DONE)_[0-9a-f]{32}|##PSSTUDIO_LOCATION_[0-9a-f]{32}_[A-Za-z0-9+/=]+|##PSSTUDIO_DISPATCH_DIAG##(?: begin pid=[0-9]+ apartment=[A-Za-z]+| finally pid=[0-9]+))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ProtocolPrefixes =
    {
        ExecStartPrefix,
        ExecDonePrefix,
        LocationPrefix,
        DispatchDiagnosticPrefix
    };

    private readonly object _syncRoot = new();
    private readonly StringBuilder _carry = new();

    public TerminalProtocolFilterResult Process(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return new TerminalProtocolFilterResult(
                string.Empty,
                0,
                0,
                TerminalOutputControlSummary.Empty,
                TerminalOutputControlSummary.Empty);
        }

        lock (_syncRoot)
        {
            _carry.Append(chunk);
            return DrainCompleteLines();
        }
    }

    /// <summary>
    /// Releases an incomplete ordinary line. A partial private record is dropped
    /// at the lifecycle boundary rather than being carried into a new renderer.
    /// </summary>
    public TerminalProtocolFilterResult Flush()
    {
        lock (_syncRoot)
        {
            if (_carry.Length == 0)
            {
                return new TerminalProtocolFilterResult(
                    string.Empty,
                    0,
                    0,
                    TerminalOutputControlSummary.Empty,
                    TerminalOutputControlSummary.Empty);
            }

            var remainder = _carry.ToString();
            _carry.Clear();
            return ClassifyLine(remainder);
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _carry.Clear();
        }
    }

    private TerminalProtocolFilterResult DrainCompleteLines()
    {
        var visible = new StringBuilder();
        var filteredCharacters = 0;
        var filteredRecordCount = 0;
        var removedProtocolControlSummary = TerminalOutputControlSummary.Empty;
        var preservedProtocolControlSummary = TerminalOutputControlSummary.Empty;

        while (TryReadLine(out var line))
        {
            AppendClassifiedLine(
                line,
                visible,
                ref filteredCharacters,
                ref filteredRecordCount,
                ref removedProtocolControlSummary,
                ref preservedProtocolControlSummary);
        }

        // Interactive PowerShell prompts normally have no trailing newline. Do
        // not delay ordinary partial text until the next user Enter; only retain
        // an incomplete suffix that can still become a private protocol record.
        if (_carry.Length > 0 && !IsPotentialPrivateLine(_carry.ToString()))
        {
            visible.Append(_carry.ToString());
            _carry.Clear();
        }

        // Normal native output is allowed to be a very long unterminated line.
        // Keep only a small suffix so a malformed or interrupted protocol frame
        // cannot grow memory without bound. The suffix is long enough to retain
        // every framing prefix across the next ConPTY chunk boundary.
        if (_carry.Length > MaximumCarryCharacters)
        {
            if (IsPotentialPrivateLine(_carry.ToString()))
            {
                filteredCharacters += _carry.Length;
                filteredRecordCount++;
                removedProtocolControlSummary = removedProtocolControlSummary.Add(
                    TerminalOutputControlClassifier.Summarize(_carry.ToString()));
                _carry.Clear();
            }
            else
            {
                var releaseLength = _carry.Length - MaximumCarryCharacters;
                visible.Append(_carry.ToString(0, releaseLength));
                _carry.Remove(0, releaseLength);
            }
        }

        return new TerminalProtocolFilterResult(
            visible.ToString(),
            filteredCharacters,
            filteredRecordCount,
            removedProtocolControlSummary,
            preservedProtocolControlSummary);
    }

    private void AppendClassifiedLine(
        string line,
        StringBuilder visible,
        ref int filteredCharacters,
        ref int filteredRecordCount,
        ref TerminalOutputControlSummary removedProtocolControlSummary,
        ref TerminalOutputControlSummary preservedProtocolControlSummary)
    {
        var result = ClassifyLine(line);
        if (result.FilteredRecordCount > 0)
        {
            visible.Append(result.VisibleText);
            filteredCharacters += result.FilteredCharacters;
            filteredRecordCount += result.FilteredRecordCount;
            removedProtocolControlSummary = removedProtocolControlSummary.Add(result.RemovedProtocolControlSummary);
            preservedProtocolControlSummary = preservedProtocolControlSummary.Add(result.PreservedProtocolControlSummary);
            return;
        }

        visible.Append(result.VisibleText);
    }

    private static TerminalProtocolFilterResult ClassifyLine(string line)
    {
        var normalized = NormalizeForMatch(line);
        if (IsPrivateProtocolRecord(normalized) || IsGeneratedDispatchCommand(normalized))
        {
            // A ConPTY stream is stateful. Removing a private line outright also
            // removes its CR/LF and VT cursor operations, which lets xterm's cursor
            // diverge from the pseudo console. Remove only ScriptDesk-owned printable
            // payload while preserving every terminal control and line terminator.
            // Dispatch commands are deliberately kept shorter than one terminal row,
            // so hidden printable text cannot introduce an unrepresented wrap.
            var controls = ExtractTerminalStateControls(line, out var filteredCharacters);
            return new TerminalProtocolFilterResult(
                controls,
                filteredCharacters,
                1,
                TerminalOutputControlClassifier.Summarize(line),
                TerminalOutputControlClassifier.Summarize(controls));
        }

        return new TerminalProtocolFilterResult(
            line,
            0,
            0,
            TerminalOutputControlSummary.Empty,
            TerminalOutputControlSummary.Empty);
    }

    private bool TryReadLine(out string line)
    {
        for (var index = 0; index < _carry.Length; index++)
        {
            if (_carry[index] != '\r' && _carry[index] != '\n')
            {
                continue;
            }

            // A CRLF pair can be divided between two ConPTY reads. Keep the
            // trailing CR until the next chunk tells us whether an LF follows.
            if (_carry[index] == '\r' && index + 1 == _carry.Length)
            {
                line = string.Empty;
                return false;
            }

            var terminatorLength = 1;
            if (_carry[index] == '\r' &&
                index + 1 < _carry.Length &&
                _carry[index + 1] == '\n')
            {
                terminatorLength = 2;
            }

            var totalLength = index + terminatorLength;
            line = _carry.ToString(0, totalLength);
            _carry.Remove(0, totalLength);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private static bool IsPrivateProtocolRecord(string normalized)
    {
        return ProtocolRecordRegex.IsMatch(normalized);
    }

    private static bool IsGeneratedDispatchCommand(string normalized)
    {
        return GeneratedDispatchCommandRegex.IsMatch(normalized);
    }

    private static bool IsPotentialPrivateLine(string value)
    {
        var normalized = NormalizeForMatch(value);
        if (normalized.Length > 0 &&
            ProtocolPrefixes.Any(prefix => prefix.StartsWith(normalized, StringComparison.Ordinal)))
        {
            return true;
        }

        if (normalized.StartsWith(ExecStartPrefix, StringComparison.Ordinal) ||
            normalized.StartsWith(ExecDonePrefix, StringComparison.Ordinal) ||
            normalized.StartsWith(LocationPrefix, StringComparison.Ordinal) ||
            normalized.StartsWith(DispatchDiagnosticPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        var trimmed = normalized.TrimStart();
        if (trimmed.StartsWith("& '", StringComparison.Ordinal) ||
            trimmed.StartsWith(". '", StringComparison.Ordinal) ||
            (trimmed.StartsWith("PS ", StringComparison.Ordinal) &&
             (trimmed.Contains("> & '", StringComparison.Ordinal) ||
              trimmed.Contains("> . '", StringComparison.Ordinal))))
        {
            return true;
        }

        return (trimmed.StartsWith("&", StringComparison.Ordinal) ||
                trimmed.StartsWith(".", StringComparison.Ordinal)) &&
               trimmed.Contains("TerminalSnapshots", StringComparison.OrdinalIgnoreCase) &&
               (trimmed.Contains("psh-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("psi-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("psd-", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForMatch(string value)
    {
        return TerminalControlRegex.Replace(value, string.Empty)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string ExtractTerminalStateControls(string value, out int maskedCharacters)
    {
        maskedCharacters = 0;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var masked = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\x1b')
            {
                var escapeEnd = FindEscapeSequenceEnd(value, index);
                masked.Append(value, index, escapeEnd - index + 1);
                index = escapeEnd;
                continue;
            }

            if (char.IsControl(character))
            {
                masked.Append(character);
                continue;
            }

            maskedCharacters++;
            // Printable ScriptDesk-owned payload is intentionally omitted.
        }

        return masked.ToString();
    }

    private static int FindEscapeSequenceEnd(string value, int escapeIndex)
    {
        if (escapeIndex + 1 >= value.Length)
        {
            return escapeIndex;
        }

        var next = value[escapeIndex + 1];
        if (next == '[')
        {
            for (var index = escapeIndex + 2; index < value.Length; index++)
            {
                var code = value[index];
                if (code >= '@' && code <= '~')
                {
                    return index;
                }
            }

            return value.Length - 1;
        }

        if (next == ']')
        {
            for (var index = escapeIndex + 2; index < value.Length; index++)
            {
                if (value[index] == '\a')
                {
                    return index;
                }

                if (value[index] == '\x1b' && index + 1 < value.Length && value[index + 1] == '\\')
                {
                    return index + 1;
                }
            }

            return value.Length - 1;
        }

        return Math.Min(escapeIndex + 1, value.Length - 1);
    }

    private static string ExtractTerminalControls(string value)
    {
        var controls = new StringBuilder();
        foreach (Match match in TerminalControlRegex.Matches(value))
        {
            controls.Append(match.Value);
        }

        return controls.ToString();
    }
}
