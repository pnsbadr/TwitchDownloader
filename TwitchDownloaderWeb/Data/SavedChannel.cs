namespace TwitchDownloaderWeb.Data
{
    /// <summary>A Twitch channel saved for the team, shown in the VOD browser's channel list.</summary>
    public class SavedChannel
    {
        public int Id { get; set; }

        /// <summary>Twitch login, lower-case. Unique.</summary>
        public required string Login { get; set; }
        public required string DisplayName { get; set; }
        public string? TwitchUserId { get; set; }

        /// <summary>Whether the muted audio rescue watches this channel's new VODs (used by the audio rescue feature).</summary>
        public bool WatchMutedAudio { get; set; } = true;

        public DateTime AddedAtUtc { get; set; }

        /// <summary>Linked to users in phase 3.</summary>
        public string? AddedByUserId { get; set; }
    }
}
