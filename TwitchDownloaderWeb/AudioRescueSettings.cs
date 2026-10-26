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

        /// <summary>
        /// How often VODs with saved audio are checked on Twitch. Once a VOD has been gone for VodGoneGraceDays its audio is
        /// deleted, unless the library has entries for that VOD. Runs even when Enabled is off.
        /// </summary>
        public double VodCheckHours { get; set; } = 12;

        /// <summary>
        /// How long a VOD must stay missing before its audio is deleted. Unpublished VODs look deleted from outside, and the
        /// streamer can republish them.
        /// </summary>
        public double VodGoneGraceDays { get; set; } = 14;

        /// <summary>Parallel segment downloads per VOD.</summary>
        public int Threads { get; set; } = 4;
    }
}
