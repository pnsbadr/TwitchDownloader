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

        /// <summary>How often reference-only entries' VODs are checked on Twitch, so deleted ones show as such.</summary>
        public double VodCheckHours { get; set; } = 12;

        /// <summary>How many background download jobs run at once.</summary>
        public int MaxConcurrentJobs { get; set; } = 1;

        /// <summary>libx264 preset for Exact trims, which are re-encoded so the video starts on the exact frame.</summary>
        public string ExactTrimPreset { get; set; } = "veryfast";

        /// <summary>libx264 CRF for Exact trims (lower is better quality and bigger files).</summary>
        public int ExactTrimCrf { get; set; } = 18;

        /// <summary>Most segments one segment download may have.</summary>
        public int MaxSegmentsPerDownload { get; set; } = 50;

        /// <summary>Most video one segment download may make, all segments together (every segment is re-encoded).</summary>
        public int MaxSegmentDownloadSeconds { get; set; } = 3 * 3600;

        /// <summary>Segment downloads a team may have queued or running at once.</summary>
        public int MaxActiveSegmentDownloadsPerTeam { get; set; } = 3;

        /// <summary>Most items one export may have (a clip in both formats counts twice).</summary>
        public int MaxExportItems { get; set; } = 50;

        /// <summary>Most video one export may cut, all items together (copied stored clips and vertical renders don't count).</summary>
        public int MaxExportSeconds { get; set; } = 3 * 3600;

        /// <summary>Exports a team may have queued or running at once.</summary>
        public int MaxActiveExportsPerTeam { get; set; } = 3;
    }
}
