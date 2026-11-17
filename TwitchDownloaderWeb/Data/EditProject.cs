using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// A project (badrfly, 2026-11-16, multi-track Player 2; plan: Claude Doc "Vertical editor: multi-track timeline plan"): a timeline that
    /// starts at 0:00 with any number of rows (tracks) holding pieces of any VODs. A piece is a VOD range placed at a time on the project's
    /// clock, with its own vertical layout. Clips, segments and videos dropped in are <b>copied</b> as pieces: changing the original later
    /// doesn't change the project. What's on the tracks is one JSON document (<see cref="Content"/>, see <see cref="ProjectContent"/>), saved
    /// whole as the page works; <see cref="Vods"/> lists the VODs it uses, for the hidden-VOD filter. Shared with the team, same rules as videos.
    /// </summary>
    public class EditProject
    {
        public int Id { get; set; }
        public int TeamId { get; set; }

        /// <summary>Owner: the member who made it (also when someone with EditAnyEntries changes it).</summary>
        public int UserId { get; set; }

        /// <summary>May be blank (the page shows "Untitled project").</summary>
        public string Title { get; set; } = "";

        /// <summary><see cref="ProjectContent"/> JSON.</summary>
        public string Content { get; set; } = "";

        /// <summary>Goes up on every change of <see cref="Content"/>, so a page saving an older copy is told (409) instead of overwriting.</summary>
        public int Revision { get; set; }

        /// <summary>From <see cref="Content"/> when saved, for lists: where the last piece ends and how many pieces there are.</summary>
        public double LengthSeconds { get; set; }
        public int PieceCount { get; set; }

        /// <summary>
        /// The VOD this belongs to (badrfly 2026-11-17: "the VOD is the project"; the page calls these <b>edits</b>). New edits only use this
        /// VOD; older multi-VOD projects keep their other VODs. 0 = not known yet (no pieces; set from the first piece when it gets one).
        /// </summary>
        public long VodId { get; set; }

        /// <summary>The clip (library entry) this is an edit of (FK, SetNull: deleting the clip leaves the edit on its VOD).</summary>
        public int? EntryId { get; set; }

        /// <summary>The video this is an edit of (FK, SetNull). At most one of <see cref="EntryId"/> / <see cref="VideoId"/> is set.</summary>
        public int? VideoId { get; set; }

        /// <summary>A copy of another project (no FK).</summary>
        public int? CopiedFromProjectId { get; set; }

        /// <summary>Made from this saved edit (<see cref="ItemEdit"/>, no FK) when edits were turned into projects; see <c>SavedEditConversion</c>.</summary>
        public int? ConvertedFromEditId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public List<ProjectVod> Vods { get; set; } = [];

        public const int MaxTitleLength = 200;
        public const int MaxContentLength = 2_000_000;
        public const int MaxProjectsPerMember = 500;
    }

    /// <summary>A VOD a project's pieces use (rebuilt on every save). Cascade with the project.</summary>
    public class ProjectVod
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public long VodId { get; set; }
    }

    /// <summary>
    /// What's on a project's timeline. <see cref="Tracks"/> are listed top to bottom; where pieces on two tracks overlap, the one on the
    /// higher track (earlier in the list) is what plays and is rendered, and lower ones are covered (silent too). Pieces on one track never
    /// overlap. Empty time is black and silent.
    /// </summary>
    public sealed record ProjectContent(
        [property: JsonPropertyName("tracks")] IReadOnlyList<ProjectTrack>? Tracks,
        [property: JsonPropertyName("pieces")] IReadOnlyList<ProjectPiece>? Pieces)
    {
        public const int MaxTracks = 20;
        public const int MaxPieces = 200;
        public const int MaxIdLength = 32;
        public const int MaxNameLength = 60;
        public const int MaxLabelLength = 200;

        /// <summary>Nothing on the project's clock may end later than this.</summary>
        public const double MaxEndSeconds = 3 * 3600;

        public const double MinPieceSeconds = 0.5;
        private const double Tolerance = 0.01;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>A new project: one empty track.</summary>
        public static ProjectContent Empty => new([new ProjectTrack("t1", null, false, false, false)], []);

        public static ProjectContent Read(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return Empty;
            try
            {
                return JsonSerializer.Deserialize<ProjectContent>(json, Json) ?? Empty;
            }
            catch (JsonException)
            {
                return Empty;
            }
        }

        public string Write() => JsonSerializer.Serialize(this, Json);

        /// <summary>Where the last piece ends on the project's clock.</summary>
        [JsonIgnore]
        public double LengthSeconds => Pieces is { Count: > 0 } pieces ? pieces.Max(x => x.At + (x.End - x.Start)) : 0;

        [JsonIgnore]
        public IReadOnlyList<long> VodIds => (Pieces ?? []).Select(x => x.VodId).Distinct().ToList();

        /// <summary>Returns an error message, or null if the content can be saved.</summary>
        public string? Validate()
        {
            var tracks = Tracks ?? [];
            var pieces = Pieces ?? [];
            if (tracks.Count == 0)
                return "A project needs at least one row.";
            if (tracks.Count > MaxTracks)
                return $"A project can have at most {MaxTracks} rows.";
            if (pieces.Count > MaxPieces)
                return $"A project can have at most {MaxPieces} pieces.";

            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (track is null || !ValidId(track.Id))
                    return $"Row {i + 1} needs an ID (letters and digits, at most {MaxIdLength}).";
                if (track.Name is { Length: > MaxNameLength })
                    return $"Row names can be at most {MaxNameLength} characters.";
            }
            if (tracks.Select(x => x.Id).Distinct().Count() != tracks.Count)
                return "Row IDs must be different.";

            var trackIds = tracks.Select(x => x.Id).ToHashSet();
            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                var name = $"Piece {i + 1}";
                if (piece is null || !ValidId(piece.Id))
                    return $"{name} needs an ID (letters and digits, at most {MaxIdLength}).";
                if (piece.TrackId is null || !trackIds.Contains(piece.TrackId))
                    return $"{name} must be on one of the project's rows.";
                if (piece.VodId <= 0)
                    return $"{name} needs a VOD.";
                if (!double.IsFinite(piece.At) || !double.IsFinite(piece.Start) || !double.IsFinite(piece.End) || piece.At < 0 || piece.Start < 0)
                    return $"{name} needs a place, start and end in seconds.";
                if (piece.End - piece.Start < MinPieceSeconds - Tolerance)
                    return $"{name} must be at least {MinPieceSeconds} seconds long.";
                if (piece.At + (piece.End - piece.Start) > MaxEndSeconds + Tolerance)
                    return $"A project can be at most {MaxEndSeconds / 3600:0} hours long.";
                if (piece.Label is { Length: > MaxLabelLength })
                    return $"{name}'s label can be at most {MaxLabelLength} characters.";
                if (piece.Color is not null && !VodMarker.Colors.Contains(piece.Color))
                    return $"{name} has an unknown color.";
                if (piece.Source is { } source && (!ProjectSource.Kinds.Contains(source.Kind) || source.Id < 0))
                    return $"{name} has an unknown source.";
                if (piece.Layout is { } layout && layout.Validate() is { } layoutError)
                    return $"{name}: {layoutError}";
            }
            if (pieces.Select(x => x.Id).Distinct().Count() != pieces.Count)
                return "Piece IDs must be different.";

            foreach (var track in pieces.GroupBy(x => x.TrackId))
            {
                var ordered = track.OrderBy(x => x.At).ToList();
                for (var i = 1; i < ordered.Count; i++)
                    if (ordered[i].At < ordered[i - 1].At + (ordered[i - 1].End - ordered[i - 1].Start) - Tolerance)
                        return "Pieces on one row can't overlap.";
            }

            return null;
        }

        private static bool ValidId(string? id) =>
            !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    /// <summary>A row. Hidden rows aren't played or rendered; muted rows are silent; locked rows can't be changed on the page.</summary>
    public sealed record ProjectTrack(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("hidden")] bool Hidden,
        [property: JsonPropertyName("muted")] bool Muted,
        [property: JsonPropertyName("locked")] bool Locked);

    /// <summary>
    /// A range of one VOD (<see cref="Start"/>..<see cref="End"/>, VOD seconds) placed on a track at <see cref="At"/> (seconds on the project's
    /// clock). <see cref="Source"/> says what it was copied from (labels only; nothing follows it).
    /// </summary>
    public sealed record ProjectPiece(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("trackId")] string TrackId,
        [property: JsonPropertyName("at")] double At,
        [property: JsonPropertyName("vodId")] long VodId,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("source")] ProjectSource? Source = null,
        [property: JsonPropertyName("label")] string? Label = null,
        [property: JsonPropertyName("color")] string? Color = null,
        [property: JsonPropertyName("layout")] ProjectLayout? Layout = null);

    /// <param name="Kind">clip, segment, video, vod (a range dropped from a VOD) or edit (a saved edit turned into a project).</param>
    public sealed record ProjectSource(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("id")] long Id)
    {
        public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "clip", "segment", "video", "vod", "edit" };
    }

    /// <summary>
    /// A piece's vertical layout, like <see cref="ShortformLayout"/> without trims or parts: one box or two, the split, and moving-box
    /// keyframes whose times are <b>VOD seconds</b>, so they stay on the same moment when the piece is trimmed or split.
    /// </summary>
    public sealed record ProjectLayout(
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("top")] CropBox? Top,
        [property: JsonPropertyName("bottom")] CropBox? Bottom,
        [property: JsonPropertyName("splitRatio")] double? SplitRatio,
        [property: JsonPropertyName("keys")] IReadOnlyList<ShortformKey>? Keys = null)
    {
        public ShortformLayout ToShortform() => new(Mode, Top, Bottom, SplitRatio, Keys: Keys);

        public string? Validate() => ToShortform().ValidateBoxes();
    }
}
