using TwitchDownloaderCore.Models;

namespace TwitchDownloaderWeb.Data
{
    public enum EntryStatus
    {
        Queued,
        Downloading,
        Ready,
        Failed,
        ReferenceOnly,
        VodUnavailable
    }

    /// <summary>State of an on-demand file for a <see cref="EntryStatus.ReferenceOnly"/> entry.</summary>
    public enum GenerationStatus
    {
        None,
        Queued,
        Generating,
        Ready,
        Failed
    }

    public class LibraryEntry
    {
        public int Id { get; set; }

        public required string Title { get; set; }
        public long VodId { get; set; }
        public string? ChannelName { get; set; }

        /// <summary>Requested quality, e.g. "1080p60". Null means best available.</summary>
        public string? Quality { get; set; }
        public VideoTrimMode TrimMode { get; set; }

        /// <summary>Trim start, as an offset from the start of the VOD.</summary>
        public TimeSpan StartTime { get; set; }

        /// <summary>Trim end, as an offset from the start of the VOD. Null means the end of the VOD.</summary>
        public TimeSpan? EndTime { get; set; }

        public double ClipLengthSeconds { get; set; }
        public int VodLengthSeconds { get; set; }

        /// <summary>When the stream started (Twitch createdAt), UTC.</summary>
        public DateTime StreamDateUtc { get; set; }
        public DateTime AddedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public string? ThumbnailUrl { get; set; }

        public EntryStatus Status { get; set; }

        /// <summary>Last failure message, if any.</summary>
        public string? Error { get; set; }

        // Stored file (entries at or under Downloads:MaxStoredClipSeconds)
        public string? FilePath { get; set; }
        public long? FileSizeBytes { get; set; }

        // On-demand file (reference-only entries), deleted after Downloads:GeneratedFileHours
        public GenerationStatus GenerationStatus { get; set; }
        public string? GeneratedFilePath { get; set; }
        public long? GeneratedFileSizeBytes { get; set; }
        public DateTime? GeneratedExpiresAtUtc { get; set; }

        /// <summary>Linked to users in phase 3.</summary>
        public string? AddedByUserId { get; set; }
    }
}
