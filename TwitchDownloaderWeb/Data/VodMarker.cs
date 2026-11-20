namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// A team member's marker on a VOD's timeline: a moment, or a segment ("intro", "Arky meets Sunny") when it has an end.
    /// Shared with the team.
    /// </summary>
    public class VodMarker
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodId { get; set; }

        /// <summary>Offset from the start of the VOD.</summary>
        public double Seconds { get; set; }

        /// <summary>End of a segment (offset from the start of the VOD, after <see cref="Seconds"/>); null for a single moment.</summary>
        public double? EndSeconds { get; set; }

        public required string Label { get; set; }

        /// <summary>One of <see cref="Colors"/>.</summary>
        public required string Color { get; set; }

        public int CreatedByUserId { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>The creator's video row it sits on. Filled by SaveChanges with their first video row on the VOD when not set.</summary>
        public int? RowId { get; set; }
        public TimelineRow? Row { get; set; }

        /// <summary>The segment/moment this is a copy of (a member copied someone else's to their own row). No FK: the original may be deleted.</summary>
        public int? CopiedFromMarkerId { get; set; }

        /// <summary>The clip this segment was made from (Alt+drag of a clip onto a video row). No FK.</summary>
        public int? CopiedFromEntryId { get; set; }

        /// <summary>Cut-out parts (<see cref="CutOuts"/>), JSON in VOD seconds; null = none.</summary>
        public string? Cuts { get; set; }

        /// <summary>
        /// Deleted from its timeline but kept for the videos that link it (badrfly 2026-11-20: deleting a segment asks "unlink from videos" or
        /// "keep videos as they are"). Such a segment is left out of timelines, lists, copies and exports of segments; its videos still play it.
        /// Deleted for real once no video links it (<see cref="Endpoints.VideoEndpoints.DeleteEmptyAsync"/>). Null = on its timeline.
        /// </summary>
        public DateTime? RemovedAtUtc { get; set; }

        public const int MaxLabelLength = 200;
        // New colors go at the end (the page lists them in this order); the first is the default
        public static readonly IReadOnlyList<string> Colors = ["yellow", "blue", "pink", "orange", "red", "lime", "teal", "violet", "gray"];
    }
}
