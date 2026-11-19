namespace TwitchDownloaderWeb.Data
{
    /// <summary>A Twitch channel saved by a team, shown in the VOD browser's channel list.</summary>
    public class SavedChannel
    {
        public int Id { get; set; }

        /// <summary>Twitch login, lower-case. Unique within a team.</summary>
        public required string Login { get; set; }
        public required string DisplayName { get; set; }
        public string? TwitchUserId { get; set; }

        /// <summary>
        /// Automatic audio saving for this channel: the muted audio rescue watches its new VODs (while the stream is live). Set only by the app
        /// Owner on the Admin page (badrfly 2026-11-18: not the team owner any more). Off by default: it fills disks fast.
        /// </summary>
        public bool WatchMutedAudio { get; set; }

        /// <summary>
        /// The team's members (with <see cref="TeamPermission.SaveAudio"/>) may use "Save audio now" on this channel's VODs. Set only by the app
        /// Owner (badrfly 2026-11-18). Off by default; <see cref="WatchMutedAudio"/> allows it too.
        /// </summary>
        public bool AllowSaveAudio { get; set; }

        public DateTime AddedAtUtc { get; set; }

        /// <summary>The <see cref="UserAccount"/> ID (as text) of whoever saved it. Null for channels saved before accounts existed.</summary>
        public string? AddedByUserId { get; set; }

        public int TeamId { get; set; }

        /// <summary>
        /// Set when the app Owner removed the channel with "keep existing work" (badrfly, 2026-11-15): the team keeps the VODs of this channel
        /// it already has entries or markers on, but no other VODs, and the channel no longer shows in its lists. Null = assigned.
        /// Assigning the channel again clears it. Removing it with "revoke now" deletes the row, which closes every VOD of the channel.
        /// </summary>
        public DateTime? AccessEndedAtUtc { get; set; }
    }
}
