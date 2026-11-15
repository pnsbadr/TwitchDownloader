namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// A "video" (badrfly, 2026-11-15, frontend rework): one or more segments from the same video row of one VOD, linked in order with the
    /// link tool, with a title. It follows its segments: trimming one changes the video, deleting one removes it from the video, and a video
    /// with no segments left is deleted. Shared with the team; editable by its owner (AddEntries) or with EditAnyEntries.
    /// </summary>
    public class Video
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodId { get; set; }

        /// <summary>The video row its segments are on.</summary>
        public int RowId { get; set; }

        /// <summary>Owner: the member whose row it is (also when someone with EditAnyEntries edits it).</summary>
        public int UserId { get; set; }

        public required string Title { get; set; }

        /// <summary>From Twitch when it was made, for the VIDEOS cards.</summary>
        public string? ChannelName { get; set; }
        public DateTime StreamDateUtc { get; set; }
        public string? ThumbnailUrl { get; set; }

        /// <summary>A copy of another member's video (with its segments copied onto the owner's row). No FK.</summary>
        public int? CopiedFromVideoId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public List<VideoSegment> Segments { get; set; } = [];

        public const int MaxTitleLength = 500;
        public const int MaxSegments = 100;
    }

    /// <summary>A segment's place in a video. Deleting the segment (marker) deletes this link.</summary>
    public class VideoSegment
    {
        public int Id { get; set; }
        public int VideoId { get; set; }
        public int MarkerId { get; set; }
        public VodMarker? Marker { get; set; }

        /// <summary>Order in the video (0 first).</summary>
        public int Position { get; set; }
    }
}
