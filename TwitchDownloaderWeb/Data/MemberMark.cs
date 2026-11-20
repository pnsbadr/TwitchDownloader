namespace TwitchDownloaderWeb.Data
{
    public enum MarkKind
    {
        Clip,
        Video,
        Vod,
    }

    /// <summary>
    /// A member's own marks on a clip, video or VOD (badrfly 2026-11-20, "Redesign the library for large teams", step 5): a star, and
    /// when they last opened it (for Recent). Personal: nobody else sees them. <see cref="ItemId"/> has no FK (a VOD isn't a row), so
    /// marks of deleted items are left out when read.
    /// </summary>
    public class MemberMark
    {
        /// <summary>Recently opened items kept per member and kind.</summary>
        public const int MaxRecent = 50;

        public int Id { get; set; }
        public int TeamId { get; set; }
        public int UserId { get; set; }
        public MarkKind Kind { get; set; }
        public long ItemId { get; set; }
        public DateTime? StarredAtUtc { get; set; }
        public DateTime? OpenedAtUtc { get; set; }
    }
}
