namespace TwitchDownloaderWeb
{
    /// <summary>Bound from the "Downloads" configuration section.</summary>
    public sealed class DownloadSettings
    {
        public const string SectionName = "Downloads";

        /// <summary>Folder finished files are written to. Relative paths resolve against the content root.</summary>
        public string OutputFolder { get; set; } = "downloads";

        /// <summary>Folder for VOD part caches. Blank uses TwitchDownloaderCore's default.</summary>
        public string? TempFolder { get; set; }

        /// <summary>Download threads per job.</summary>
        public int Threads { get; set; } = 4;

        /// <summary>Clips at or under this length are downloaded and stored; longer ones are reference-only.</summary>
        public int MaxStoredClipSeconds { get; set; } = 180;

        /// <summary>How long on-demand files for reference-only entries are kept before being deleted.</summary>
        public double GeneratedFileHours { get; set; } = 72;

        /// <summary>How many background download jobs run at once.</summary>
        public int MaxConcurrentJobs { get; set; } = 1;
    }
}
