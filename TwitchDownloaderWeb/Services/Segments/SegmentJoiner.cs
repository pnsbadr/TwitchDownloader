using System.Globalization;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services.Segments
{
    /// <summary>
    /// Joins segment files into one, in the given order. The files are Exact cuts of one VOD rendition (same size, libx264 + AAC from
    /// <see cref="ExactTrimService"/>), so ffmpeg's concat demuxer can copy them without re-encoding. If that fails (e.g. one came from
    /// Core's stream-copy fallback with different parameters) they are joined again with a re-encode.
    /// </summary>
    public static class SegmentJoiner
    {
        public static async Task JoinAsync(string ffmpegPath, IReadOnlyList<string> files, string outputFile, string workDirectory, string preset, int crf,
            ILogger logger, CancellationToken cancellationToken)
        {
            if (files.Count == 0)
                throw new ArgumentException("Nothing to join.", nameof(files));

            var list = Path.Combine(workDirectory, "join.txt");
            // concat demuxer list: one "file '<path>'" per line; a quote inside a path is written as '\''
            await File.WriteAllLinesAsync(list, files.Select(f => $"file '{Path.GetFullPath(f).Replace("'", "'\\''")}'"), cancellationToken);

            try
            {
                await FfmpegRunner.RunAsync(ffmpegPath, Args(list, outputFile, ["-c", "copy"]), workDirectory, logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Joining segments without re-encoding failed ({Message}); re-encoding.", ex.Message);
                await FfmpegRunner.RunAsync(ffmpegPath, Args(list, outputFile,
                [
                    "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "192k",
                ]), workDirectory, logger, cancellationToken);
            }
        }

        private static List<string> Args(string list, string outputFile, IEnumerable<string> codecs)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-map", "0:a:0?" };
            args.AddRange(codecs);
            args.AddRange(["-movflags", "+faststart", outputFile]);
            return args;
        }
    }
}
