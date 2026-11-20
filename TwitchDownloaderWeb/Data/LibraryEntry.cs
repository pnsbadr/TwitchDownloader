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

    public enum EntryKind
    {
        /// <summary>A cut of a VOD, made by us from the VOD's playlist.</summary>
        Vod,

        /// <summary>
        /// A clips.twitch.tv clip. Always reference-only (Twitch keeps the file); its file is made on demand from the clip,
        /// or from its source VOD when saved clean audio can replace muted parts.
        /// </summary>
        Clip
    }

    public class LibraryEntry
    {
        public int Id { get; set; }

        public required string Title { get; set; }

        public EntryKind Kind { get; set; }

        /// <summary>The clip's slug (clips.twitch.tv/{slug}) for <see cref="EntryKind.Clip"/> entries.</summary>
        public string? ClipSlug { get; set; }

        /// <summary>For clips, the VOD the clip was cut from, or 0 if Twitch didn't say (e.g. the VOD was already deleted).</summary>
        public long VodId { get; set; }
        public string? ChannelName { get; set; }

        /// <summary>Requested quality, e.g. "1080p60". Null means best available.</summary>
        public string? Quality { get; set; }
        public VideoTrimMode TrimMode { get; set; }

        /// <summary>Trim start, as an offset from the start of the VOD. For clips, where the clip starts in its VOD (whole seconds, from Twitch).</summary>
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

        /// <summary>The clip's color on the timeline (one of <see cref="VodMarker.Colors"/>); null = the default violet (badrfly 2026-11-20).</summary>
        public string? Color { get; set; }

        /// <summary>The team's workflow status (<see cref="Data.TeamStatus"/>, badrfly 2026-11-20); null = none. Not <see cref="Status"/>, the download's.</summary>
        public int? TeamStatusId { get; set; }
        public TeamStatus? TeamStatus { get; set; }

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

        /// <summary>Seconds of muted audio replaced with rescued clean audio in the last file made for this entry. Null if none.</summary>
        public double? AudioRestoredSeconds { get; set; }

        /// <summary>
        /// Seconds of the last file made for this entry that are still muted because no clean audio was saved for them
        /// (e.g. Twitch had already muted them when the rescue started). Null if none, or if the VOD has no audio rescue.
        /// </summary>
        public double? AudioMutedSeconds { get; set; }

        /// <summary>The <see cref="UserAccount"/> ID (as text) of whoever added it. Null for entries added before accounts existed.</summary>
        public string? AddedByUserId { get; set; }

        /// <summary>
        /// Made from the team's markers on a live VOD: its range is kept at 30 s before the first marker/segment to 30 s after the
        /// last one (capped at what has been streamed) whenever the VOD's markers change. A manual re-trim turns this off.
        /// </summary>
        public bool FollowsMarkers { get; set; }

        /// <summary>
        /// Made by <see cref="Services.LiveMarkerEntryService"/> (stays set after it stops following). A team gets one such entry per
        /// VOD: once it stops following, later markers don't make another one.
        /// </summary>
        public bool MadeFromMarkers { get; set; }

        /// <summary>The team whose library this is in. Requests only see their own team's entries (query filter in <see cref="LibraryDbContext"/>).</summary>
        public int TeamId { get; set; }

        /// <summary>
        /// The clip row (of the member who added it) it sits on in the VOD's timeline. Filled by SaveChanges with the adder's first clip
        /// row on the VOD when not set; null for entries without a known adder or VOD.
        /// </summary>
        public int? RowId { get; set; }
        public TimelineRow? Row { get; set; }

        /// <summary>The clip this is a copy of (a member copied someone else's clip to their own row). No FK: the original may be deleted.</summary>
        public int? CopiedFromEntryId { get; set; }

        /// <summary>Cut-out parts (<see cref="CutOuts"/>), JSON in VOD seconds; null = none.</summary>
        public string? Cuts { get; set; }

        /// <summary>
        /// The cut-out parts left out of the entry's file (stored or generated), or null when the file covers the whole range. Files are made
        /// of the whole range since 2026-11-16 (so changing cut-out parts needs no new download; they're left out when a file is made from it
        /// and in preview); only files made before that have this set, until they are made again.
        /// </summary>
        public string? FileCuts { get; set; }
    }
}
