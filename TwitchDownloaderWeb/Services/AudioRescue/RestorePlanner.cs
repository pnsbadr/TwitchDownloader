namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>A rescued clean audio segment on disk.</summary>
    /// <param name="FirstAudioPts">PTS of its first audio packet, read when it was saved.</param>
    public sealed record CleanAudioFile(string FilePath, long FirstAudioPts);

    /// <summary>
    /// Works out where rescued audio goes in a file Core downloaded in Safe mode.
    ///
    /// Core concatenates the segments with each one placed by its #EXTINF duration, and keeps the timing inside each
    /// segment relative to that segment's own start (its lowest timestamp). So a muted segment's audio sits at
    /// (its playlist offset − the first segment's offset) + (audio PTS − segment start PTS). The clean audio comes from
    /// another rendition, which shares the stream's clock, so the same formula with the clean file's PTS puts it in
    /// exactly the same place. The playlist durations alone are not enough: they can be off by an audio frame or so.
    /// </summary>
    public static class RestorePlanner
    {
        /// <summary>Consecutive muted segments inside the trim that have clean audio saved.</summary>
        public static List<IReadOnlyList<VodSegment>> FindRuns(IReadOnlyList<VodSegment> segments, Range trim, Func<string, bool> hasCleanAudio)
        {
            var (start, length) = trim.GetOffsetAndLength(segments.Count);
            var runs = new List<IReadOnlyList<VodSegment>>();
            List<VodSegment>? current = null;

            for (var i = start; i < start + length; i++)
            {
                var segment = segments[i];
                if (segment.IsMuted && hasCleanAudio(segment.Key))
                {
                    current ??= [];
                    current.Add(segment);
                }
                else if (current is not null)
                {
                    runs.Add(current);
                    current = null;
                }
            }

            if (current is not null)
                runs.Add(current);

            return runs;
        }

        /// <param name="firstSegment">Timestamps from the start of the first downloaded segment (in the download's quality).</param>
        /// <param name="runStartSegments">Timestamps from the start of each run's first segment (in the download's quality), in run order.</param>
        /// <returns>Where the input file's audio starts, and the runs placed on its timeline.</returns>
        public static (double InputAudioStartSeconds, List<CleanAudioRun> Runs) Place(IReadOnlyList<VodSegment> segments, Range trim,
            IReadOnlyList<IReadOnlyList<VodSegment>> runs, Func<string, CleanAudioFile> cleanAudio, TsTimestamps firstSegment,
            IReadOnlyList<TsTimestamps> runStartSegments)
        {
            if (runStartSegments.Count != runs.Count)
                throw new ArgumentException("Need the timestamps of each run's first segment.", nameof(runStartSegments));

            var startOffset = segments[trim.GetOffsetAndLength(segments.Count).Offset].Offset;

            var audioStart = firstSegment is { FirstAudioPts: { } a, StartPts: { } s } ? TsTimestamps.DiffSeconds(a, s) : 0;

            var placed = new List<CleanAudioRun>(runs.Count);
            for (var i = 0; i < runs.Count; i++)
            {
                var first = runs[i][0];
                var clean = cleanAudio(first.Key);
                var segmentStart = runStartSegments[i].StartPts
                                   ?? throw new InvalidOperationException($"No timestamps found in segment {first.Key}.");

                var position = (double)(first.Offset - startOffset) + TsTimestamps.DiffSeconds(clean.FirstAudioPts, segmentStart);
                placed.Add(new CleanAudioRun(runs[i].Select(x => new CleanAudioSegment(cleanAudio(x.Key).FilePath, x.Duration)).ToArray(), position));
            }

            return (audioStart, placed);
        }
    }
}
