using System.Globalization;
using System.Text;

namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>A clean audio segment file and its #EXTINF duration in the video rendition's playlist.</summary>
    public sealed record CleanAudioSegment(string FilePath, decimal Duration);

    /// <summary>Consecutive muted segments whose clean audio is spliced in together.</summary>
    /// <param name="PositionSeconds">Where the run's first clean audio sample belongs, on the downloaded file's timeline.</param>
    public sealed record CleanAudioRun(IReadOnlyList<CleanAudioSegment> Segments, double PositionSeconds)
    {
        public double DurationSeconds => (double)Segments.Sum(x => x.Duration);
    }

    /// <summary>
    /// Swaps clean audio into a downloaded VOD file. The video is copied untouched; the audio is the original with the
    /// muted ranges silenced, mixed with the clean audio delayed into place, and re-encoded once. Mixing rather than cutting
    /// avoids AAC frame-boundary seams, and copes with the clean audio having a different sample rate.
    /// </summary>
    public static class AudioSplicer
    {
        private const int SampleRate = 48_000;

        /// <param name="inputFile">An mp4 Core wrote in Safe mode, so its timeline starts at a known segment.</param>
        /// <param name="inputAudioStartSeconds">Where the input's audio starts on its timeline (the first segment's audio vs. its start).</param>
        public static async Task SpliceAsync(string ffmpegPath, string inputFile, double inputAudioStartSeconds, IReadOnlyList<CleanAudioRun> runs,
            string outputFile, string workDirectory, ILogger logger, CancellationToken cancellationToken)
        {
            if (runs.Count == 0)
                throw new ArgumentException("Nothing to splice.", nameof(runs));

            Directory.CreateDirectory(workDirectory);
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", inputFile };

            for (var i = 0; i < runs.Count; i++)
            {
                // Mirror Core's concat list: each file is placed by its #EXTINF duration, as Core places the video segments
                var listPath = Path.Combine(workDirectory, $"run{i}.ffconcat");
                var list = new StringBuilder("ffconcat version 1.0\n");
                foreach (var segment in runs[i].Segments)
                {
                    list.Append("file '").Append(Path.GetFullPath(segment.FilePath).Replace("'", @"'\''")).Append("'\n");
                    list.Append("duration ").Append(segment.Duration.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }

                await File.WriteAllTextAsync(listPath, list.ToString(), cancellationToken);
                args.AddRange(["-f", "concat", "-safe", "0", "-i", listPath]);
            }

            args.AddRange(["-filter_complex", BuildFilter(inputAudioStartSeconds, runs), "-map", "0:v?", "-map", "[a]",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), outputFile]);

            await FfmpegRunner.RunAsync(ffmpegPath, args, workDirectory, logger, cancellationToken);
        }

        /// <remarks>
        /// amix lines its inputs up sample-by-sample from their first samples, while volume's enable expression sees the
        /// input's timestamps. So the delays are measured from where the input's audio starts, and the gates from zero.
        /// </remarks>
        internal static string BuildFilter(double inputAudioStartSeconds, IReadOnlyList<CleanAudioRun> runs)
        {
            var invariant = CultureInfo.InvariantCulture;
            var gates = string.Join(",", runs.Select(r =>
                string.Format(invariant, "volume=0:enable='between(t,{0:0.######},{1:0.######})'", r.PositionSeconds, r.PositionSeconds + r.DurationSeconds)));

            var sb = new StringBuilder();
            sb.Append(invariant, $"[0:a]aresample={SampleRate},{gates}[base];");
            for (var i = 0; i < runs.Count; i++)
            {
                var delaySamples = Math.Max(0, (long)Math.Round((runs[i].PositionSeconds - inputAudioStartSeconds) * SampleRate));
                sb.Append(invariant, $"[{i + 1}:a]aresample={SampleRate},adelay={delaySamples}S:all=1[r{i}];");
            }

            sb.Append("[base]");
            for (var i = 0; i < runs.Count; i++)
                sb.Append(invariant, $"[r{i}]");
            sb.Append(invariant, $"amix=inputs={runs.Count + 1}:normalize=0:duration=first[a]");

            return sb.ToString();
        }

        /// <summary>The Exact trim, done the way Core does it (-ss/-t with stream copy), on the file whose audio is already fixed.</summary>
        public static Task TrimAsync(string ffmpegPath, string inputFile, double startSeconds, double lengthSeconds, string outputFile, string workDirectory,
            ILogger logger, CancellationToken cancellationToken)
        {
            var invariant = CultureInfo.InvariantCulture;
            var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
            if (startSeconds > 0)
                args.AddRange(["-ss", startSeconds.ToString("0.######", invariant)]);
            args.AddRange(["-t", lengthSeconds.ToString("0.######", invariant), "-avoid_negative_ts", "make_zero", "-i", inputFile,
                "-map", "0", "-c", "copy", outputFile]);

            return FfmpegRunner.RunAsync(ffmpegPath, args, workDirectory, logger, cancellationToken);
        }
    }
}
