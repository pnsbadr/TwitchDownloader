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

        public int Threads { get; set; } = 4;
    }
}
