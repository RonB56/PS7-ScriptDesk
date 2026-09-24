using System.Security.Cryptography;
using System.Text;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Privacy-preserving metadata helpers for the xterm screen-state forensic trace.
/// The helper deliberately retains no arbitrary terminal row or prompt content.
/// </summary>
public static class TerminalScreenStateDiagnostics
{
    public const string InterruptMarker = "PHASEB_INTERRUPT_TICK";
    private const int MaxRowsInspected = 64;

    public static bool ContainsInterruptMarker(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(InterruptMarker, StringComparison.Ordinal);

    public static IReadOnlyDictionary<string, object?> CreateMarkerRowMetadata(
        string? rowText,
        int absoluteRow,
        int viewportRow,
        int baseY,
        int cursorY,
        bool isWrapped,
        int rendererGeneration,
        long snapshotSequence)
    {
        var text = rowText ?? string.Empty;
        var markerIndex = text.IndexOf(InterruptMarker, StringComparison.Ordinal);
        var partialPrefixLength = markerIndex >= 0 ? InterruptMarker.Length : FindPartialPrefixLength(text);
        var exact = markerIndex >= 0;

        return new Dictionary<string, object?>
        {
            ["markerIdentity"] = StableMarkerIdentity,
            ["markerMatchKind"] = exact ? "exact" : partialPrefixLength > 0 ? "partial" : "none",
            ["markerPrefixLength"] = partialPrefixLength,
            ["markerStartColumn"] = exact ? markerIndex : -1,
            ["markerEndColumn"] = exact ? markerIndex + InterruptMarker.Length : -1,
            ["absoluteRow"] = absoluteRow,
            ["viewportRelativeRow"] = viewportRow,
            ["baseY"] = baseY,
            ["cursorY"] = cursorY,
            ["isWrapped"] = isWrapped,
            ["rendererGeneration"] = rendererGeneration,
            ["snapshotSequence"] = snapshotSequence,
            ["contentOmitted"] = true
        };
    }

    public static int MaxInspectedRows => MaxRowsInspected;

    public static string StableMarkerIdentity { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(InterruptMarker))).ToLowerInvariant()[..16];

    private static int FindPartialPrefixLength(string text)
    {
        var max = Math.Min(InterruptMarker.Length - 1, text.Length);
        for (var length = max; length >= 4; length--)
        {
            if (text.EndsWith(InterruptMarker[..length], StringComparison.Ordinal) ||
                text.StartsWith(InterruptMarker[^length..], StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }
}
