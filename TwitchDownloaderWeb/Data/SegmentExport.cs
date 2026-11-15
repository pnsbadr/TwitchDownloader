namespace TwitchDownloaderWeb.Data
{
    public enum SegmentExportStatus
    {
        Queued,
        Running,
        Ready,
        Failed
    }

    public enum SegmentExportItemStatus
    {
        Queued,
        Running,
        Ready,
        Failed
    }

    /// <summary>
    /// A download of some of a VOD's team segments (<see cref="VodMarker"/>s with an end), picked on a card's strip. Each segment becomes its
    /// own file (Exact cut, saved clean audio swapped into muted parts); with <see cref="Combined"/> (a team feature only the app Owner turns on)
    /// the files are then joined into one, in time order. Segments never become library entries. Files expire like on-demand files.
    /// </summary>
    public class SegmentExport
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public long VodId { get; set; }

        /// <summary>The library entry whose card it was started from (for its title and quality), if any. No FK: the export outlives it.</summary>
        public int? EntryId { get; set; }

        /// <summary>Used to name the files: the entry's title, else the VOD's.</summary>
        public required string Title { get; set; }

        /// <summary>Resolved quality name, null = best.</summary>
        public string? Quality { get; set; }

        /// <summary>Also join the segments into one file.</summary>
        public bool Combined { get; set; }

        public SegmentExportStatus Status { get; set; }
        public string? Error { get; set; }

        /// <summary>The combined file (relative to Downloads:OutputFolder) once ready.</summary>
        public string? CombinedFilePath { get; set; }
        public long? CombinedFileSizeBytes { get; set; }

        public int CreatedByUserId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>When its files are deleted (set when it finishes: Downloads:GeneratedFileHours later).</summary>
        public DateTime? ExpiresAtUtc { get; set; }

        public List<SegmentExportItem> Items { get; set; } = [];
    }

    /// <summary>One segment of a <see cref="SegmentExport"/>: a copy of its range and label when it was picked (the marker may change later).</summary>
    public class SegmentExportItem
    {
        public int Id { get; set; }
        public int ExportId { get; set; }

        /// <summary>Order in the export (by start time), from 1. Also the file name.</summary>
        public int Position { get; set; }

        /// <summary>The marker it came from. No FK: deleting the marker doesn't touch the download.</summary>
        public int? MarkerId { get; set; }

        public required string Label { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }

        /// <summary>The segment's cut-out parts when it was picked (<see cref="CutOuts"/>); its file skips them.</summary>
        public string? Cuts { get; set; }

        public SegmentExportItemStatus Status { get; set; }
        public string? Error { get; set; }
        public string? FilePath { get; set; }
        public long? FileSizeBytes { get; set; }
        public double? AudioRestoredSeconds { get; set; }
        public double? AudioMutedSeconds { get; set; }
    }
}
