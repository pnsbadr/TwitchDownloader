namespace TwitchDownloaderWeb.Data
{
    public static class AuditActions
    {
        public const string EntryAdded = "entry.added";
        public const string EntryRenamed = "entry.renamed";
        public const string EntryDeleted = "entry.deleted";
        public const string DownloadStarted = "download.started";
        public const string DownloadCompleted = "download.completed";
        public const string DownloadFailed = "download.failed";
        public const string DownloadInterrupted = "download.interrupted";
        public const string GenerationRequested = "generation.requested";
        public const string GeneratedFileExpired = "generation.expired";
        public const string FileServed = "file.served";
        public const string ChannelAdded = "channel.added";
        public const string ChannelRemoved = "channel.removed";
        public const string AudioRescueStarted = "audio.rescue_started";
        public const string AudioRescueSaved = "audio.rescue_saved";
        public const string AudioRescueFailed = "audio.rescue_failed";
        public const string AudioRescuePruned = "audio.rescue_pruned";
        public const string AudioRestored = "audio.restored";
        public const string AudioRestoreFailed = "audio.restore_failed";
        public const string AudioNotRestored = "audio.not_restored";
    }

    /// <summary>
    /// Append-only audit record. Details is free text and must never contain OAuth or session tokens.
    /// </summary>
    public class AuditEvent
    {
        public long Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public required string Action { get; set; }
        public int? EntryId { get; set; }

        /// <summary>Linked to users in phase 3.</summary>
        public string? UserId { get; set; }
        public string? Details { get; set; }
    }
}
