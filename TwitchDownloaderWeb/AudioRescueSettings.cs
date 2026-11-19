namespace TwitchDownloaderWeb
{
    /// <summary>Bound from the "AudioRescue" configuration section.</summary>
    public sealed class AudioRescueSettings
    {
        public const string SectionName = "AudioRescue";

        /// <summary>
        /// App-wide switch for automatic saving of VOD audio (the app Owner's kill switch). Automatic saving itself is set per team channel by
        /// the app Owner (<see cref="Data.SavedChannel.WatchMutedAudio"/>, off by default). Rescues not started by hand pause (no grabbing, no
        /// pruning) while no channel row with it on watches their channel, or while this is off. The page's Save audio button works either
        /// way where the Owner allows it (<see cref="Data.SavedChannel.AllowSaveAudio"/>). Already saved audio is kept and still used to restore downloads.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>How often saved channels are checked for new VODs.</summary>
        public double PollMinutes { get; set; } = 5;

        /// <summary>How often a live (still recording) VOD's new audio segments are fetched.</summary>
        public double LiveRecheckMinutes { get; set; } = 5;

        /// <summary>How long after a stream ends the full audio is kept before keeping only the muted parts.</summary>
        public double PruneAfterHours { get; set; } = 48;

        /// <summary>
        /// How often VODs with saved audio are checked on Twitch. Once a VOD has been gone for VodGoneGraceDays its audio is
        /// deleted. Runs even when Enabled is off.
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
