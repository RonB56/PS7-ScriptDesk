namespace PS7ScriptDesk.Shell.Editor;

/// <summary>Chooses the fold associated with the caret, preferring the innermost applicable range.</summary>
internal static class FoldRegionSelector
{
    internal static T? Select<T>(
        IEnumerable<T> regions,
        int caretOffset,
        int caretLine,
        Func<T, int> startOffset,
        Func<T, int> endOffset,
        Func<T, int> headerLine)
        where T : class
    {
        T? headerMatch = null;
        T? containingHeaderMatch = null;
        T? closingBoundaryMatch = null;
        T? containingMatch = null;
        var headerSpan = int.MaxValue;
        var headerDistance = int.MaxValue;
        var containingHeaderSpan = int.MaxValue;
        var closingBoundarySpan = int.MaxValue;
        var containingSpan = int.MaxValue;

        foreach (var region in regions)
        {
            var start = startOffset(region);
            var end = endOffset(region);
            if (end <= start) continue;
            var span = end - start;

            // A preceding declaration line is associated with the fold only up to its
            // opening boundary. A line-only match can otherwise select a nested fold
            // whose header happens to share the line while the caret is still before it.
            if (caretOffset <= start && headerLine(region) == caretLine)
            {
                var distance = start - caretOffset;
                if (headerMatch is null || distance < headerDistance ||
                    (distance == headerDistance && span < headerSpan))
                {
                    headerMatch = region;
                    headerSpan = span;
                    headerDistance = distance;
                }
            }

            // EndOffset is exclusive, but the insertion position immediately after a
            // closing brace or #endregion is still a useful structural boundary.
            if (caretOffset == end &&
                (closingBoundaryMatch is null || span < closingBoundarySpan))
            {
                closingBoundaryMatch = region;
                closingBoundarySpan = span;
            }

            if (start <= caretOffset && caretOffset < end &&
                (containingMatch is null || span < containingSpan || (span == containingSpan && start > startOffset(containingMatch))))
            {
                containingMatch = region;
                containingSpan = span;
            }

            if (start <= caretOffset && caretOffset < end && headerLine(region) == caretLine &&
                (containingHeaderMatch is null || span < containingHeaderSpan))
            {
                containingHeaderMatch = region;
                containingHeaderSpan = span;
            }
        }

        return containingHeaderMatch ?? headerMatch ?? closingBoundaryMatch ?? containingMatch;
    }
}
