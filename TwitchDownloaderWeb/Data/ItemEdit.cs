using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchDownloaderWeb.Data
{
    public enum ItemEditKind
    {
        Clip,
        Segment,
        Video
    }

    /// <summary>
    /// A saved edit (badrfly, 2026-11-16, Player 2): one version of a clip, segment or video made in Player 2, like a sequence made from a
    /// source file. It is reference only (what it removes and a vertical layout); files are made only when it's exported. Every edit is made
    /// from the item's <b>full</b> range, never from what's applied to it, so one item can have many and applying one (<see cref="ItemApply"/>)
    /// changes none of the others. Exactly one of <see cref="EntryId"/> / <see cref="MarkerId"/> / <see cref="VideoId"/> is set (cascade).
    /// </summary>
    public class ItemEdit
    {
        public int Id { get; set; }
        public int TeamId { get; set; }

        /// <summary>The item's VOD (for the hidden-VOD filter and lists).</summary>
        public long VodId { get; set; }

        public int? EntryId { get; set; }
        public int? MarkerId { get; set; }
        public int? VideoId { get; set; }

        public int CreatedByUserId { get; set; }

        /// <summary>May be blank (the page shows "Edit N").</summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Clip or segment: everything the edit removes (<see cref="CutOuts"/> JSON, VOD seconds), trimmed ends included. Read through
        /// <see cref="ItemEdits.ReadCuts"/>: a cut that reached the item's start or end when saved is stored open-ended, so the edit's ends stay
        /// put when the item is made longer, and everything is clipped to the item's current range (a shorter item cuts the edit down to fit).
        /// </summary>
        public string? Cuts { get; set; }

        /// <summary>Video: the cuts per segment (<see cref="ItemEditPart"/> JSON), stored like <see cref="Cuts"/>.</summary>
        public string? Parts { get; set; }

        /// <summary>The vertical editor's layout for this edit (the page's JSON object, the shape <c>POST /shortform</c> takes); null = none.</summary>
        public string? Layout { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public ItemEditKind Kind => VideoId is not null ? ItemEditKind.Video : MarkerId is not null ? ItemEditKind.Segment : ItemEditKind.Clip;

        public const int MaxTitleLength = 200;
        public const int MaxLayoutLength = 100_000;
        public const int MaxEditsPerItemPerMember = 50;
    }

    /// <summary>One segment's cuts in a video edit.</summary>
    public sealed record ItemEditPart(
        [property: JsonPropertyName("markerId")] int MarkerId,
        [property: JsonPropertyName("cuts")] IReadOnlyList<CutRange>? Cuts)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static IReadOnlyList<ItemEditPart> Read(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<ItemEditPart>>(json, Json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        public static string? Write(IReadOnlyList<ItemEditPart> parts) =>
            parts.Count == 0 ? null : JsonSerializer.Serialize(parts, Json);
    }

    /// <summary>
    /// One "Apply to Player 1" of an edit: the item's cut-out parts before and after, so it can always be undone ("Undo last apply" = back to
    /// the newest row's <see cref="CutsBefore"/>, "Revert to original" = the oldest row's). The oldest row and the newest 19 are kept.
    /// </summary>
    public class ItemApply
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodId { get; set; }

        public int? EntryId { get; set; }
        public int? MarkerId { get; set; }
        public int? VideoId { get; set; }

        /// <summary>No FK: the edit may be deleted later.</summary>
        public int? EditId { get; set; }
        public string EditTitle { get; set; } = "";

        public int UserId { get; set; }
        public DateTime AppliedAtUtc { get; set; }

        /// <summary>Clip/segment: the item's <c>Cuts</c> JSON; video: <see cref="ItemEditPart"/> JSON of every segment's <c>VideoSegments.Cuts</c>.</summary>
        public string? CutsBefore { get; set; }
        public string? CutsAfter { get; set; }

        public const int MaxPerItem = 20;
    }

    /// <summary>Reading and writing edits' cuts (see <see cref="ItemEdit.Cuts"/>).</summary>
    public static class ItemEdits
    {
        /// <summary>Stands for "the item's end, wherever it is" in stored edit cuts (past any VOD).</summary>
        public const double OpenEnd = 10_000_000;

        /// <summary>The edit's cuts against the item's current range: clipped, merged, slivers cut (like every cut-out part).</summary>
        public static IReadOnlyList<CutRange> ReadCuts(string? json, double start, double end) => CutOuts.Normalize(CutOuts.Read(json), start, end);

        /// <summary>
        /// Normalized cuts for storing: a cut from the item's start is stored from 0 and one to its end to <see cref="OpenEnd"/>, so the
        /// edit's own ends stay where they are if the item is made longer later.
        /// </summary>
        public static IReadOnlyList<CutRange> ForStoring(IEnumerable<CutRange>? cuts, double start, double end) =>
            CutOuts.Normalize(cuts, start, end)
                .Select(x => new CutRange(x.Start <= start ? 0 : x.Start, x.End >= end ? OpenEnd : x.End))
                .ToList();

        /// <summary>Same check as other cut-out parts, except a video's segment may be cut out whole (<paramref name="mayLeaveNothing"/>).</summary>
        public static string? Validate(IReadOnlyList<CutRange>? cuts, double start, double end, bool mayLeaveNothing)
        {
            if (cuts is null || cuts.Count == 0)
                return null;
            if (cuts.Count > CutOuts.MaxCuts)
                return $"At most {CutOuts.MaxCuts} cut-out parts.";
            if (cuts.Any(x => !double.IsFinite(x.Start) || !double.IsFinite(x.End) || x.End <= x.Start))
                return "Each cut-out part needs an end after its start.";
            if (!mayLeaveNothing && CutOuts.KeptSeconds(start, end, cuts) < CutOuts.MinKeptPartSeconds)
                return "The edit leaves nothing.";
            return null;
        }
    }
}
