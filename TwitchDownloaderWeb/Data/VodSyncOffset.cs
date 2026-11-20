namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// How two VODs of different channels line up (badrfly 2026-11-20, "Sync with"): the same moment is at
    /// <c>bSeconds = aSeconds + (A.createdAt - B.createdAt) + Seconds</c>, with A the lower VOD ID. Stream start times alone are a few
    /// seconds off (each streamer's delay differs), so this is what an audio match or a member's nudge found. One per pair per team.
    /// </summary>
    public class VodSyncOffset
    {
        public const string FromAudio = "audio";
        public const string ByHand = "manual";

        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodA { get; set; }
        public long VodB { get; set; }
        public double Seconds { get; set; }

        /// <summary><see cref="FromAudio"/> or <see cref="ByHand"/>.</summary>
        public string Source { get; set; } = ByHand;

        /// <summary>How sure the audio match was (0–1); null when set by hand.</summary>
        public double? Confidence { get; set; }

        public int? UpdatedByUserId { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
