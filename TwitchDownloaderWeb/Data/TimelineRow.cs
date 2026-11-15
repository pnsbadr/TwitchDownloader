namespace TwitchDownloaderWeb.Data
{
    public enum TimelineRowKind
    {
        /// <summary>Holds segments and moment markers (<see cref="VodMarker"/>).</summary>
        Video,

        /// <summary>Holds clips (<see cref="LibraryEntry"/> cut from the VOD).</summary>
        Clip,
    }

    /// <summary>
    /// A lane on a VOD's timeline owned by one team member (badrfly, 2026-11-15, frontend rework): video rows hold their segments and
    /// moments, clip rows their clips. Rows belong to one VOD and don't carry over to others. Everyone in the team sees and plays every
    /// row; an item always sits on a row of the member who made it.
    /// </summary>
    public class TimelineRow
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodId { get; set; }

        /// <summary>The member who owns the row (and every item on it).</summary>
        public int UserId { get; set; }

        public TimelineRowKind Kind { get; set; }

        /// <summary>Order among this member's rows on this VOD (0 first).</summary>
        public int Position { get; set; }

        /// <summary>Optional name the member gave it.</summary>
        public string? Name { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public const int MaxNameLength = 60;

        /// <summary>At most this many rows per member per VOD.</summary>
        public const int MaxRowsPerMember = 20;
    }

    /// <summary>
    /// A member opened a VOD's timeline for the first time: that's when they get their first video row and clip row on it
    /// (later loads don't bring back rows they deleted).
    /// </summary>
    public class MemberVod
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public int UserId { get; set; }
        public long VodId { get; set; }
        public DateTime FirstOpenedAtUtc { get; set; }
    }
}
