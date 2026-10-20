namespace TwitchDownloaderWeb
{
    /// <summary>Bound from the "AudioRescue" configuration section.</summary>
    public sealed class AudioRescueSettings
    {
        public const string SectionName = "AudioRescue";

        /// <summary>Watch saved channels (with WatchMutedAudio) for new VODs and save their audio automatically.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>How often saved channels are checked for new VODs.</summary>
        public double PollMinutes { get; set; } = 5;

        /// <summary>How often a live (still recording) VOD's new audio segments are fetched.</summary>
        public double LiveRecheckMinutes { get; set; } = 5;

        /// <summary>How long after a stream ends the full audio is kept before keeping only the muted parts.</summary>
        public double PruneAfterHours { get; set; } = 48;

        /// <summary>Parallel segment downloads per VOD.</summary>
        public int Threads { get; set; } = 4;
    }
}
