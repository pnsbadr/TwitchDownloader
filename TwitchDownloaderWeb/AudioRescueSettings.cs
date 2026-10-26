namespace TwitchDownloaderWeb
{
    /// <summary>Bound from the "AudioRescue" configuration section.</summary>
    public sealed class AudioRescueSettings
    {
        public const string SectionName = "AudioRescue";

        /// <summary>
        /// Master switch for saving VOD audio. Off: no new rescues (automatic or from the page), and unfinished ones pause
        /// (no grabbing, no pruning) until it is turned back on. Already saved audio is kept and still used to restore downloads.
        /// Temporary: becomes a per-team setting once teams exist (phase 3).
        /// </summary>
        public bool Enabled { get; set; }

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
