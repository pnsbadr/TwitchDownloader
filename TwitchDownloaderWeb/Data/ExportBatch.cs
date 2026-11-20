using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchDownloaderWeb.Data
{
    public enum ExportStatus
    {
        Queued,
        Running,
        Ready,
        Failed
    }

    public enum ExportItemKind
    {
        /// <summary>A range of a VOD dropped on the export list: a one-off, never saved as an entry.</summary>
        VodRange,
        /// <summary>A VOD clip (library entry), landscape.</summary>
        Clip,
        /// <summary>A clip's vertical (9:16) render.</summary>
        ClipVertical,
        /// <summary>A video (linked segments): a .zip of one file per segment, or joined into one file.</summary>
        Video,
        /// <summary>One segment.</summary>
        Segment,
        /// <summary>A project (multi-track Player 2) rendered landscape; its content is copied into <see cref="ExportItem.Parts"/>.</summary>
        Project,
        /// <summary>A project rendered 9:16 with its pieces' boxes.</summary>
        ProjectVertical
    }

    /// <summary>
    /// An export (badrfly, 2026-11-15, frontend rework): what a member dropped on the Export tab's list, downloaded in one go. Its items are
    /// made one after another (<see cref="Services.Segments.ExportWorker"/>), each with its own quality and file name. What each item is made
    /// from is copied when it's asked for, like segment downloads, so later edits don't change a running export. Files expire like on-demand files.
    /// </summary>
    public class ExportBatch
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public int CreatedByUserId { get; set; }

        public ExportStatus Status { get; set; }
        public string? Error { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>When its files are deleted (set when it finishes: Downloads:GeneratedFileHours later).</summary>
        public DateTime? ExpiresAtUtc { get; set; }

        public List<ExportItem> Items { get; set; } = [];
    }

    public class ExportItem
    {
        public int Id { get; set; }
        public int BatchId { get; set; }

        /// <summary>Order in the list, from 1.</summary>
        public int Position { get; set; }

        public ExportItemKind Kind { get; set; }

        /// <summary>The download's name without extension (sanitized when served).</summary>
        public required string FileName { get; set; }

        /// <summary>Resolved quality name, null = best (the clip's own for a stored clip that is copied). Not used for vertical renders.</summary>
        public string? Quality { get; set; }

        public long VodId { get; set; }

        /// <summary>The range (VOD seconds) of a VOD range, clip or segment when it was asked for.</summary>
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }

        /// <summary>Cut-out parts of that range (<see cref="CutOuts"/>).</summary>
        public string? Cuts { get; set; }

        /// <summary>A clip's trim mode (VOD ranges, segments and videos are Exact).</summary>
        public TwitchDownloaderCore.Models.VideoTrimMode TrimMode { get; set; } = TwitchDownloaderCore.Models.VideoTrimMode.Exact;

        // What it came from (no FKs: the export outlives them)
        public int? EntryId { get; set; }
        public int? MarkerId { get; set; }
        public int? VideoId { get; set; }

        /// <summary>The saved edit it's made from (Player 2): the item without what the edit removes, copied into the fields above. No FK.</summary>
        public int? EditId { get; set; }

        /// <summary>The project it's made from (<see cref="ExportItemKind.Project"/> / <see cref="ExportItemKind.ProjectVertical"/>). No FK.</summary>
        public int? ProjectId { get; set; }

        /// <summary>The vertical render it copies (<see cref="ExportItemKind.ClipVertical"/>).</summary>
        public string? RenderId { get; set; }

        /// <summary>A video's segments when it was asked for, in play order (<see cref="ExportVideoPart"/> JSON); a project's content
        /// (<see cref="ProjectContent"/> JSON).</summary>
        public string? Parts { get; set; }

        /// <summary>A video joined into one file (the team's "render as one" setting) instead of a .zip of its segments.</summary>
        public bool Joined { get; set; }

        /// <summary>A project (edit) as separate files, one per piece it plays (gaps left out), served as a .zip (badrfly 2026-11-20),
        /// instead of one file. The file path is then a folder.</summary>
        public bool Separate { get; set; }

        public ExportStatus Status { get; set; }
        public string? Error { get; set; }

        /// <summary>Progress: files made so far of <see cref="PartsTotal"/>.</summary>
        public int PartsDone { get; set; }
        public int PartsTotal { get; set; }

        /// <summary>The file (relative to Downloads:OutputFolder), or for a .zip video the folder of its segment files.</summary>
        public string? FilePath { get; set; }
        public long? FileSizeBytes { get; set; }
        public double? AudioRestoredSeconds { get; set; }
        public double? AudioMutedSeconds { get; set; }

        public const int MaxFileNameLength = 200;
    }

    /// <summary>One segment of an exported video: its label and range, and every part cut out of it (its own and the video's).</summary>
    public sealed record ExportVideoPart(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("cuts")] IReadOnlyList<CutRange> Cuts)
    {
        /// <summary>The segment's VOD when it isn't the item's (a video linking segments of synced rows, badrfly 2026-11-20); null = the item's.</summary>
        [JsonPropertyName("vodId")]
        public long? VodId { get; init; }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static IReadOnlyList<ExportVideoPart> Read(string? json) =>
            string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<ExportVideoPart>>(json, Json) ?? [];

        public static string Write(IReadOnlyList<ExportVideoPart> parts) => JsonSerializer.Serialize(parts, Json);

        public double KeptSeconds => CutOuts.KeptSeconds(Start, End, Cuts);
    }
}
