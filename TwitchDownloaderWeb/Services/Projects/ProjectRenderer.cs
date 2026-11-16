using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Segments;
using TwitchDownloaderWeb.Services.Shortform;

namespace TwitchDownloaderWeb.Services.Projects
{
    /// <summary>
    /// Renders a project (<see cref="ProjectContent"/>) into one mp4, landscape or 9:16. Each stretch of what it plays
    /// (<see cref="ProjectTimeline.Flatten"/>) is cut from its VOD like every other range (<see cref="IVodCutter"/>: Exact, saved clean audio
    /// swapped in), then encoded to the same size, frame rate and audio format (vertical: with the piece's boxes and moving keyframes, landscape:
    /// scaled and letterboxed), gaps become black silence, and the files are joined with a stream copy (<see cref="SegmentJoiner"/>).
    /// </summary>
    public sealed partial class ProjectRenderer
    {
        private readonly IVodCutter _cutter;
        private readonly LibraryStorage _storage;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;

        public ProjectRenderer(IVodCutter cutter, LibraryStorage storage, IConfiguration configuration, IOptions<DownloadSettings> settings)
        {
            _cutter = cutter;
            _storage = storage;
            _configuration = configuration;
            _settings = settings;
        }

        /// <summary>A 9:16 box in the middle of a 16:9 frame, for pieces without a layout.</summary>
        public static readonly ProjectLayout CenterLayout = new("single", new CropBox((1 - 81.0 / 256) / 2, 0, 81.0 / 256, 1), null, null);

        /// <summary>The output frame: 1080x1920 vertical; landscape at the quality's height (1080 when best or unknown).</summary>
        public static (int Width, int Height, int Fps) OutputFormat(string? quality, bool vertical)
        {
            var match = quality is null ? null : QualityPattern().Match(quality);
            var height = match is { Success: true } && int.TryParse(match.Groups[1].Value, out var h) && h is >= 144 and <= 1080 ? h : 1080;
            var fps = match is { Success: true } && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var f) && f > 0 ? Math.Min(f, 60)
                : match is { Success: true } ? 30 : 60;
            if (vertical)
                return (ShortformLayout.OutputWidth, ShortformLayout.OutputHeight, fps);
            var width = (int)Math.Round(height * 16 / 9.0 / 2) * 2;
            return (width, height, fps);
        }

        [GeneratedRegex(@"^(\d{3,4})p(\d{2})?")]
        private static partial Regex QualityPattern();

        /// <param name="quality">Requested for every VOD; a VOD without it gives its best (the output is scaled to one size anyway).</param>
        /// <param name="onStretchDone">Called with the number of stretches done.</param>
        /// <returns>How many stretches there were, and the saved-audio results added up (null when no VOD had saved audio).</returns>
        public async Task<(int Stretches, AudioRestoreResult? Restore)> RenderAsync(LibraryDbContext db, int teamId, ProjectContent content, bool vertical,
            string? quality, string outputPath, string workName, ILogger logger, Action<string> onRestoreFailed, Func<int, int, Task> onStretchDone,
            CancellationToken cancellationToken)
        {
            var stretches = ProjectTimeline.Flatten(content);
            if (stretches.Count == 0)
                throw new InvalidOperationException("The project has nothing on its visible rows.");

            var settings = _settings.Value;
            var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
            var format = OutputFormat(quality, vertical);
            var work = _storage.CreateWorkFolder(workName);
            var restores = new List<AudioRestoreResult>();
            try
            {
                var files = new List<string>();
                for (var i = 0; i < stretches.Count; i++)
                {
                    var stretch = stretches[i];
                    var file = Path.Combine(work, $"{i + 1}.mp4");
                    string[] args;
                    if (stretch.Piece is not { } piece)
                    {
                        args = GapArgs(stretch.Duration, format);
                    }
                    else
                    {
                        var raw = Path.Combine(work, $"{i + 1}.cut.mp4");
                        var cut = new LibraryEntry
                        {
                            Title = piece.Label ?? $"Piece {piece.Id}",
                            VodId = piece.VodId,
                            Quality = quality,
                            TrimMode = VideoTrimMode.Exact,
                            StartTime = TimeSpan.FromSeconds(stretch.VodStart),
                            EndTime = TimeSpan.FromSeconds(stretch.VodEnd),
                            TeamId = teamId,
                        };
                        if (await _cutter.CutAsync(db, cut, raw, logger, onRestoreFailed, cancellationToken) is { } restore)
                            restores.Add(restore);
                        args = PieceArgs(raw, stretch, vertical, format);
                    }

                    await FfmpegRunner.RunAsync(ffmpegPath, [.. args, .. Encoding(settings), file], work, logger, cancellationToken);
                    files.Add(file);
                    if (stretch.Piece is not null)
                        _storage.TryDelete(Path.Combine(work, $"{i + 1}.cut.mp4"));
                    await onStretchDone(i + 1, stretches.Count);
                }

                if (files.Count == 1)
                    File.Move(files[0], outputPath, overwrite: true);
                else
                    await SegmentJoiner.JoinAsync(ffmpegPath, files, outputPath, work, settings.ExactTrimPreset, settings.ExactTrimCrf, logger, cancellationToken);

                return (stretches.Count, restores.Count == 0
                    ? null
                    : new AudioRestoreResult(restores.Sum(x => x.RestoredSeconds), restores.Sum(x => x.StillMutedSeconds), restores.Any(x => x.Downloaded)));
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Couldn't delete {Path}: {Message}; it is cleared at the next start.", work, ex.Message);
                }
            }
        }

        private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Same video and audio settings for every stretch, so they join with a stream copy.</summary>
        private static string[] Encoding(DownloadSettings settings) =>
        [
            "-map_chapters", "-1", "-dn", "-sn",
            "-c:v", "libx264", "-preset", settings.ExactTrimPreset, "-crf", settings.ExactTrimCrf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
            "-video_track_timescale", "90000",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart",
        ];

        private static string[] GapArgs(double duration, (int Width, int Height, int Fps) format) =>
        [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s={format.Width}x{format.Height}:r={format.Fps}",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", N(duration), "-map", "0:v", "-map", "1:a",
        ];

        /// <summary>The cut stretch in the output format. Muted: its sound is replaced by silence.</summary>
        internal static string[] PieceArgs(string raw, ProjectStretch stretch, bool vertical, (int Width, int Height, int Fps) format)
        {
            string graph;
            if (vertical)
            {
                var layout = stretch.Piece!.Layout ?? CenterLayout;
                // Keyframe times are VOD seconds; in the filter t counts from the stretch's start (it may be past some keys already)
                var keys = layout.Keys?.Select(x => x with { Time = x.Time - stretch.VodStart }).ToList();
                var shortform = new ShortformLayout(layout.Mode, layout.Top, layout.Bottom, layout.SplitRatio, Keys: keys);
                graph = $"{shortform.BuildFilterGraph()};[v]fps={format.Fps},setsar=1[vo]";
            }
            else
            {
                graph = $"[0:v]scale={format.Width}:{format.Height}:force_original_aspect_ratio=decrease,"
                        + $"pad={format.Width}:{format.Height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={format.Fps},format=yuv420p[vo]";
            }

            return
            [
                "-hide_banner", "-nostdin", "-y",
                "-i", raw,
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-filter_complex", graph,
                "-t", N(stretch.Duration),
                "-map", "[vo]", "-map", stretch.Muted ? "1:a" : "0:a:0?",
            ];
        }
    }
}
