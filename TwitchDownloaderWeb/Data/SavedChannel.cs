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

        /// <summary>Whether the muted audio rescue watches this channel's new VODs (used by the audio rescue feature).</summary>
        public bool WatchMutedAudio { get; set; } = true;

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
