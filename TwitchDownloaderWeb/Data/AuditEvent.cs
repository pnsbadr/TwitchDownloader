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
        /// <summary>The app Owner changed a team channel's audio saving (Save audio now allowed, automatic saving).</summary>
        public const string ChannelAudioChanged = "channel.audio_changed";
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
        public const string TeamRenamed = "team.renamed";
        public const string TeamMemberJoined = "team.member_joined";
        public const string TeamMemberRemoved = "team.member_removed";
        public const string LinkCreated = "link.created";
        public const string LinkRevoked = "link.revoked";
        public const string StaffSetUp = "staff.set_up";
        public const string StaffLoggedIn = "staff.logged_in";
        public const string StaffLoginFailed = "staff.login_failed";
        public const string StaffLoggedOut = "staff.logged_out";
        public const string RoleCreated = "role.created";
        public const string RoleChanged = "role.changed";
        public const string RoleDeleted = "role.deleted";
        public const string MemberRoleChanged = "team.member_role_changed";
        public const string TeamSettingsChanged = "team.settings_changed";
        public const string PasswordChanged = "user.password_changed";
        public const string PasswordResetLinkCreated = "user.password_reset_link";
        public const string PasswordReset = "user.password_reset";
        public const string ShortformRequested = "shortform.requested";
        public const string ShortformDeleted = "shortform.deleted";
        public const string MarkerAdded = "marker.added";
        public const string MarkerDeleted = "marker.deleted";
        public const string MarkerChanged = "marker.changed";
        public const string RowDeleted = "row.deleted";
        public const string VideoAdded = "video.added";
        public const string VideoChanged = "video.changed";
        public const string VideoDeleted = "video.deleted";
        public const string EntryMovedRow = "entry.moved_row";
        /// <summary>A live-markers entry's range moved because the VOD's markers changed (not counted as a member's edit).</summary>
        public const string EntryFollowedMarkers = "entry.followed_markers";
        public const string StaffJoined = "staff.joined";
        public const string StaffPermissionsChanged = "staff.permissions_changed";
        public const string StaffDisabled = "staff.disabled";
        public const string StaffEnabled = "staff.enabled";
        public const string StaffPasswordChanged = "staff.password_changed";
        public const string StaffPasswordResetLinkCreated = "staff.password_reset_link";
        public const string StaffPasswordReset = "staff.password_reset";
        public const string UserDisabled = "user.disabled";
        public const string UserEnabled = "user.enabled";
        public const string TeamDisabled = "team.disabled";
        public const string TeamEnabled = "team.enabled";
        public const string SupportViewStarted = "support.started";
        public const string SupportViewEnded = "support.ended";
        /// <summary>Segment downloads (picked segments of a VOD, one file each or joined; never library entries).</summary>
        public const string SegmentsRequested = "segments.requested";
        public const string SegmentsReady = "segments.ready";
        public const string SegmentsFailed = "segments.failed";
        public const string SegmentsDeleted = "segments.deleted";
        public const string SegmentsExpired = "segments.expired";
        /// <summary>Exports (the Export tab's list: VOD ranges, clips, verticals, videos, segments; never library entries).</summary>
        public const string ExportRequested = "export.requested";
        public const string ExportReady = "export.ready";
        public const string ExportFailed = "export.failed";
        public const string ExportDeleted = "export.deleted";
        public const string ExportExpired = "export.expired";
        /// <summary>Saved edits in Player 2 (reference only); applying one sets the item's cut-out parts, reverting puts them back.</summary>
        public const string EditAdded = "edit.added";
        public const string EditDeleted = "edit.deleted";
        public const string EditApplied = "edit.applied";
        public const string EditReverted = "edit.reverted";
        /// <summary>Projects (multi-track Player 2). Saves while working aren't audited.</summary>
        public const string ProjectAdded = "project.added";
        public const string ProjectDeleted = "project.deleted";
        /// <summary>The app Owner turned a team feature (e.g. joined segment downloads) on or off.</summary>
        public const string TeamFeaturesChanged = "team.features_changed";
        /// <summary>The app Owner changed the app's name or the start screen's links (Admin → App).</summary>
        public const string AppSettingsChanged = "app.settings_changed";
        public const string TwitchConnected = "twitch.connected";
        public const string TwitchDisconnected = "twitch.disconnected";
        /// <summary>Staff downloaded or deleted error reports (the Reports tab).</summary>
        public const string ErrorReportsDownloaded = "reports.downloaded";
        public const string ErrorReportsDeleted = "reports.deleted";
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
