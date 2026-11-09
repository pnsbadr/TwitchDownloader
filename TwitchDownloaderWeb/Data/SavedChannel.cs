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
    }
}
