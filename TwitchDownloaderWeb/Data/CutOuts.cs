using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchDownloaderWeb.Data
{
    /// <summary>A removed part of a segment, clip or video, in seconds from the start of the VOD (like the item's own range).</summary>
    public sealed record CutRange(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End);

    /// <summary>
    /// Cut-out parts (badrfly, 2026-11-15, frontend rework): ranges removed from a segment, clip or video. They are stored as JSON
    /// (<c>Cuts</c> columns, null = none) in VOD seconds, and always read through <see cref="Normalize"/> against the item's current range,
    /// so a later trim simply drops what falls outside. Files skip them (each kept part is cut, then joined) and lengths, including the
    /// stored-clip limit, count only what's left.
    /// </summary>
    public static class CutOuts
    {
        /// <summary>Kept parts shorter than this (between two cuts, or a cut and an end) are cut too, so no file is made of a sliver.</summary>
        public const double MinKeptPartSeconds = 0.5;

        public const int MaxCuts = 50;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static IReadOnlyList<CutRange> Read(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<CutRange>>(json, Json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        public static string? Write(IReadOnlyList<CutRange> cuts) => cuts.Count == 0 ? null : JsonSerializer.Serialize(cuts, Json);

        /// <summary>
        /// Sorted, merged cuts inside [start, end], rounded to hundredths; cuts that leave a sliver under <see cref="MinKeptPartSeconds"/>
        /// next to them are widened over it. Invalid ranges (end before start, not a number) are dropped; check them with
        /// <see cref="Validate"/> where they come from a request.
        /// </summary>
        public static IReadOnlyList<CutRange> Normalize(IEnumerable<CutRange>? cuts, double start, double end)
        {
            if (cuts is null)
                return [];

            var sorted = cuts
                .Where(x => double.IsFinite(x.Start) && double.IsFinite(x.End) && x.End > x.Start)
                .Select(x => new CutRange(Math.Round(Math.Max(x.Start, start), 2), Math.Round(Math.Min(x.End, end), 2)))
                .Where(x => x.End > x.Start)
                .OrderBy(x => x.Start)
                .ToList();

            var merged = new List<CutRange>();
            foreach (var cut in sorted)
            {
                var from = cut.Start - start < MinKeptPartSeconds ? start : cut.Start;
                var to = end - cut.End < MinKeptPartSeconds ? end : cut.End;
                if (merged.Count > 0 && from - merged[^1].End < MinKeptPartSeconds)
                    merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, to) };
                else
                    merged.Add(new CutRange(from, to));
            }

            return merged;
        }

        /// <summary>What's left of [start, end] after the cuts (each part at least <see cref="MinKeptPartSeconds"/> long, in order).</summary>
        public static IReadOnlyList<(double Start, double End)> Kept(double start, double end, IEnumerable<CutRange>? cuts)
        {
            var kept = new List<(double, double)>();
            var at = start;
            foreach (var cut in Normalize(cuts, start, end))
            {
                if (cut.Start > at)
                    kept.Add((at, cut.Start));
                at = Math.Max(at, cut.End);
            }

            if (end > at)
                kept.Add((at, end));
            return kept;
        }

        public static double KeptSeconds(double start, double end, IEnumerable<CutRange>? cuts) =>
            Kept(start, end, cuts).Sum(x => x.End - x.Start);

        /// <returns>An error for the client, or null. Cuts may reach past the range (they're clipped), but something must be left.</returns>
        public static string? Validate(IReadOnlyList<CutRange>? cuts, double start, double end)
        {
            if (cuts is null || cuts.Count == 0)
                return null;
            if (cuts.Count > MaxCuts)
                return $"At most {MaxCuts} cut-out parts.";
            if (cuts.Any(x => !double.IsFinite(x.Start) || !double.IsFinite(x.End) || x.End <= x.Start))
                return "Each cut-out part needs an end after its start.";
            if (KeptSeconds(start, end, cuts) < MinKeptPartSeconds)
                return "The cut-out parts leave nothing.";
            return null;
        }
    }
}
