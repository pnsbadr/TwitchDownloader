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

        public const int MaxLabelLength = 200;
        public static readonly IReadOnlyList<string> Colors = ["yellow", "blue", "pink", "orange"];
    }
}
