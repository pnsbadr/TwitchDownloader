namespace TwitchDownloaderWeb.Data
{
    public static class AuditActions
    {
        public const string EntryAdded = "entry.added";
        public const string EntryRenamed = "entry.renamed";
        public const string EntryRetrimmed = "entry.retrimmed";
        public const string EntryDeleted = "entry.deleted";
        public const string DownloadStarted = "download.started";
        public const string DownloadCompleted = "download.completed";
        public const string DownloadFailed = "download.failed";
        public const string DownloadInterrupted = "download.interrupted";
        public const string GenerationRequested = "generation.requested";
        public const string GeneratedFileExpired = "generation.expired";
        public const string FileServed = "file.served";
        public const string EntryVodGone = "entry.vod_gone";
        public const string EntryVodBack = "entry.vod_back";
        public const string ChannelAdded = "channel.added";
        public const string ChannelRemoved = "channel.removed";
        public const string AudioRescueStarted = "audio.rescue_started";
        public const string AudioRescueSaved = "audio.rescue_saved";
        public const string AudioRescueFailed = "audio.rescue_failed";
        public const string AudioRescuePruned = "audio.rescue_pruned";
        public const string AudioRescueRemoved = "audio.rescue_removed";
        public const string AudioRestored = "audio.restored";
        public const string AudioRestoreFailed = "audio.restore_failed";
        public const string AudioNotRestored = "audio.not_restored";
        public const string ShortformRendered = "shortform.rendered";
        public const string ShortformFailed = "shortform.failed";
        public const string UserSignedUp = "user.signed_up";
        public const string UserLoggedIn = "user.logged_in";
        public const string UserLoginFailed = "user.login_failed";
        public const string UserLoggedOut = "user.logged_out";
        public const string TeamCreated = "team.created";
        public const string TeamOwnerChanged = "team.owner_changed";
        public const string TeamMemberJoined = "team.member_joined";
        public const string TeamMemberRemoved = "team.member_removed";
        public const string LinkCreated = "link.created";
        public const string LinkRevoked = "link.revoked";
        public const string StaffSetUp = "staff.set_up";
        public const string StaffLoggedIn = "staff.logged_in";
        public const string StaffLoginFailed = "staff.login_failed";
        public const string StaffLoggedOut = "staff.logged_out";
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

        /// <summary>The <see cref="UserAccount"/> ID (as text) that did it; null for background jobs and staff actions.</summary>
        public string? UserId { get; set; }

        /// <summary>The team it happened in, if any. Entry events get their entry's team.</summary>
        public int? TeamId { get; set; }

        /// <summary>The <see cref="StaffAccount"/> that did it, for app staff actions.</summary>
        public int? StaffId { get; set; }
        public string? Details { get; set; }
    }
}
