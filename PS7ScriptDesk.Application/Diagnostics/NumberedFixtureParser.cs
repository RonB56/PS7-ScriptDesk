using System.Text;
using System.Text.RegularExpressions;

namespace PS7ScriptDesk.Application.Diagnostics;

internal readonly record struct NumberedFixtureRecord(int SequenceNumber);

/// <summary>Bounded, text-free recognizer for the Phase C.2C numbered fixture.</summary>
internal sealed class NumberedFixtureParser
{
    private const string Prefix = "PHASEB_INTERRUPT_TICK ";
    private const int MaxPendingCharacters = 256;
    private static readonly Regex FixtureLine = new(
        @"^(?:\x1b\][^\a]*(?:\a|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~])*PHASEB_INTERRUPT_TICK (?<number>[0-9]{1,10})(?=\r|\n)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private readonly StringBuilder _pending = new();

    public IReadOnlyList<NumberedFixtureRecord> Process(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return Array.Empty<NumberedFixtureRecord>();
        var combined = _pending.ToString() + chunk;
        _pending.Clear();
        var records = new List<NumberedFixtureRecord>();
        foreach (Match match in FixtureLine.Matches(combined))
        {
            if (int.TryParse(match.Groups["number"].Value, out var number))
            {
                records.Add(new NumberedFixtureRecord(number));
            }
        }

        var lastLineBreak = Math.Max(combined.LastIndexOf('\r'), combined.LastIndexOf('\n'));
        var suffix = lastLineBreak >= 0 ? combined[(lastLineBreak + 1)..] : combined;
        if (suffix.Contains(Prefix, StringComparison.Ordinal) ||
            (suffix.Length > 0 && Prefix.StartsWith(suffix, StringComparison.Ordinal)))
        {
            if (suffix.Length > MaxPendingCharacters) suffix = suffix[^MaxPendingCharacters..];
            _pending.Append(suffix);
        }
        return records;
    }

    public IReadOnlyList<NumberedFixtureRecord> Flush()
    {
        var records = Process(_pending.Append('\n').ToString());
        _pending.Clear();
        return records;
    }
}
