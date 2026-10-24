namespace TwitchDownloaderWeb.Data
{
    public enum AudioRescueStatus
    {
        /// <summary>Downloading the VOD's audio (repeats while the stream is live).</summary>
        Grabbing,

        /// <summary>All audio saved; waiting for Twitch to finish muting before keeping only the muted parts.</summary>
        WaitingToPrune,

        /// <summary>Only the clean audio of muted segments is kept.</summary>
        Pruned,

        Failed
    }

    /// <summary>
    /// Clean audio saved for one VOD. The whole audio-only rendition is saved while Twitch still serves unmuted copies,
    /// then pruned to the segments that ended up muted. Files live in "rescue/{vodId}/{segmentKey}.ts".
    /// </summary>
    public class VodAudioRescue
    {
        public long VodId { get; set; }
        public string? ChannelLogin { get; set; }
        public string? Title { get; set; }

        /// <summary>When the stream started (Twitch createdAt), UTC.</summary>
        public DateTime StreamStartUtc { get; set; }

        public AudioRescueStatus Status { get; set; }
        public string? Error { get; set; }

        /// <summary>The rendition the audio came from, normally "Audio".</summary>
        public string? SourceQuality { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime NextCheckAtUtc { get; set; }
        public DateTime? GrabCompletedAtUtc { get; set; }
        public DateTime? PruneAfterUtc { get; set; }

        public int SegmentCount { get; set; }
        public int SavedCount { get; set; }
        public int MutedCount { get; set; }

        /// <summary>Muted segments whose clean copy Twitch no longer served (403/404).</summary>
        public int UnrecoverableCount { get; set; }
        public long StoredBytes { get; set; }

        /// <summary>Linked to users in phase 3.</summary>
        public string? RequestedByUserId { get; set; }
    }

    public class RescuedAudioSegment
    {
        public long Id { get; set; }
        public long VodId { get; set; }

        /// <summary>The segment name without "-muted"/"-unmuted" or extension, the same in every rendition.</summary>
        public required string Key { get; set; }
        public decimal OffsetSeconds { get; set; }
        public decimal DurationSeconds { get; set; }

        /// <summary>Whether the segment was muted in the playlist when its audio was saved (or at prune time).</summary>
        public bool WasMuted { get; set; }

        /// <summary>Muted, and Twitch no longer had the clean copy. No file.</summary>
        public bool Unrecoverable { get; set; }

        /// <summary>Relative to Downloads:OutputFolder. Null if unrecoverable.</summary>
        public string? FilePath { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>PTS (90 kHz) of the file's first audio packet, used to place it exactly when restoring.</summary>
        public long? FirstAudioPts { get; set; }

        public DateTime SavedAtUtc { get; set; }
    }
}
