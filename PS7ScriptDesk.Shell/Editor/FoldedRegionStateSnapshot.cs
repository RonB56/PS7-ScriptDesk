using ICSharpCode.AvalonEdit.Document;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>
/// Captures only folded regions and matches them against regenerated ranges by their
/// structural boundaries, so edits before a region do not invalidate its identity.
/// </summary>
internal sealed class FoldedRegionStateSnapshot
{
    private readonly HashSet<string> _foldedRegionKeys;

    private FoldedRegionStateSnapshot(HashSet<string> foldedRegionKeys)
        => _foldedRegionKeys = foldedRegionKeys;

    public int Count => _foldedRegionKeys.Count;

    public static FoldedRegionStateSnapshot Capture(
        ITextSource text,
        IEnumerable<(FoldingRange Range, bool IsFolded)> regions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regions);

        var source = regions.ToArray();
        var entries = BuildIdentities(text, source.Select(static item => item.Range));
        var foldedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.OriginalIndex >= 0 && entry.OriginalIndex < source.Length &&
                source[entry.OriginalIndex].IsFolded)
            {
                foldedKeys.Add(entry.Key);
            }
        }

        return new FoldedRegionStateSnapshot(foldedKeys);
    }

    public IReadOnlyList<FoldingRange> FindFoldedRanges(ITextSource text, IReadOnlyList<FoldingRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ranges);
        if (_foldedRegionKeys.Count == 0 || ranges.Count == 0) return Array.Empty<FoldingRange>();

        var identities = BuildIdentities(text, ranges);
        return identities
            .Where(entry => _foldedRegionKeys.Contains(entry.Key))
            .Select(entry => ranges[entry.OriginalIndex])
            .ToArray();
    }

    private static List<RegionIdentity> BuildIdentities(ITextSource text, IEnumerable<FoldingRange> sourceRanges)
    {
        var ranges = sourceRanges.ToArray();
        var ordered = ranges
            .Select((range, index) => new RegionNode(range, index))
            .OrderBy(static node => node.Range.StartOffset)
            .ThenByDescending(static node => node.Range.EndOffset)
            .ToArray();

        var containment = new Stack<RegionNode>();
        foreach (var node in ordered)
        {
            while (containment.Count > 0 && node.Range.EndOffset > containment.Peek().Range.EndOffset)
                containment.Pop();
            node.Parent = containment.Count > 0 ? containment.Peek() : null;
            node.BaseKey = CreateBaseKey(text, node.Range);
            containment.Push(node);
        }

        var occurrenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in ordered)
        {
            var parentKey = node.Parent is null ? string.Empty : node.Parent.Key;
            var siblingKey = $"{parentKey}\u001f{node.BaseKey}";
            occurrenceCounts.TryGetValue(siblingKey, out var occurrence);
            occurrenceCounts[siblingKey] = occurrence + 1;
            node.Key = $"{siblingKey}\u001f{occurrence}";
        }

        return ordered.Select(node => new RegionIdentity(node.OriginalIndex, node.Key)).ToList();
    }

    private static string CreateBaseKey(ITextSource text, FoldingRange range)
    {
        var start = Math.Clamp(range.StartOffset, 0, text.TextLength);
        var end = Math.Clamp(range.EndOffset, start, text.TextLength);
        var header = GetBoundaryLine(text, start);
        var footer = GetBoundaryLine(text, Math.Max(start, end - 1));
        return $"{range.Name?.Trim()}\u001f{header}\u001f{footer}";
    }

    private static string GetBoundaryLine(ITextSource text, int offset)
    {
        var start = Math.Clamp(offset, 0, text.TextLength);
        while (start > 0 && text.GetCharAt(start - 1) is not ('\r' or '\n')) start--;
        var end = Math.Clamp(offset, start, text.TextLength);
        while (end < text.TextLength && text.GetCharAt(end) is not ('\r' or '\n')) end++;
        while (start < end && char.IsWhiteSpace(text.GetCharAt(start))) start++;
        const int maxBoundaryLength = 160;
        var length = Math.Min(end - start, maxBoundaryLength);
        var line = text.GetText(start, length).TrimEnd();
        return end - start > maxBoundaryLength ? line + "…" : line;
    }

    private sealed class RegionNode(FoldingRange range, int originalIndex)
    {
        public FoldingRange Range { get; } = range;
        public int OriginalIndex { get; } = originalIndex;
        public RegionNode? Parent { get; set; }
        public string BaseKey { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
    }

    private sealed record RegionIdentity(int OriginalIndex, string Key);
}
