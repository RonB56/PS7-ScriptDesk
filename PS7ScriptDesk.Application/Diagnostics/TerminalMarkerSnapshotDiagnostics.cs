using System.Text.Json;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Normalizes the bounded marker payload produced by the xterm host without
/// retaining arbitrary terminal row text.
/// </summary>
public static class TerminalMarkerSnapshotDiagnostics
{
    public const int MaxRows = 32;
    public const int MaxJsonLength = 8_192;

    public static bool TryNormalize(
        string? markerRowsJson,
        out string normalizedRowsJson,
        out MarkerSnapshotSummary summary)
    {
        normalizedRowsJson = "[]";
        summary = MarkerSnapshotSummary.Empty;
        if (string.IsNullOrWhiteSpace(markerRowsJson) || markerRowsJson.Length > MaxJsonLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(markerRowsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var rows = new List<MarkerRow>(MaxRows);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (rows.Count >= MaxRows || element.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                rows.Add(new MarkerRow(
                    GetString(element, "markerIdentity"),
                    GetString(element, "markerMatchKind"),
                    GetInt(element, "markerPrefixLength", 0),
                    GetInt(element, "markerStartColumn", -1),
                    GetInt(element, "markerEndColumn", -1),
                    GetNullableInt(element, "numberedSequence"),
                    GetInt(element, "absoluteRow", -1),
                    GetInt(element, "viewportRelativeRow", -1),
                    GetBool(element, "isWrapped")));
            }

            normalizedRowsJson = JsonSerializer.Serialize(rows);
            summary = MarkerSnapshotSummary.From(rows);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public sealed record MarkerRow(
        string MarkerIdentity,
        string MarkerMatchKind,
        int MarkerPrefixLength,
        int MarkerStartColumn,
        int MarkerEndColumn,
        int? NumberedSequence,
        int AbsoluteRow,
        int ViewportRelativeRow,
        bool IsWrapped);

    public sealed record MarkerSnapshotSummary(
        bool HasStart,
        int? LowestNumberedMarker,
        int? HighestNumberedMarker,
        int NumberedMarkerCount,
        string NumberedSequenceJson,
        bool HasEnd,
        int MarkerRowCount)
    {
        public static MarkerSnapshotSummary Empty { get; } = new(false, null, null, 0, "[]", false, 0);

        public static MarkerSnapshotSummary From(IReadOnlyList<MarkerRow> rows)
        {
            var numbers = rows
                .Where(row => row.NumberedSequence is >= 1 and <= 999)
                .Select(row => row.NumberedSequence!.Value)
                .Distinct()
                .Order()
                .ToArray();
            return new MarkerSnapshotSummary(
                rows.Any(row => string.Equals(row.MarkerIdentity, "completion-start-7b3e", StringComparison.Ordinal)),
                numbers.FirstOrDefault() is var lowest && numbers.Length > 0 ? lowest : null,
                numbers.LastOrDefault() is var highest && numbers.Length > 0 ? highest : null,
                numbers.Length,
                JsonSerializer.Serialize(numbers),
                rows.Any(row => string.Equals(row.MarkerIdentity, "completion-end-4a91", StringComparison.Ordinal)),
                rows.Count);
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "unknown"
            : "unknown";

    private static int GetInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static int? GetNullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
