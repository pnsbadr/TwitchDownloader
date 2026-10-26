using System.Globalization;
using Microsoft.Extensions.Options;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Exact trims without Core's stream-copy cut. Core cuts Exact trims with <c>-ss</c> and <c>-c copy</c>, and copied video can
    /// only start on a keyframe (Twitch puts one every ~2 s), so the clip's audio starts on time while the first frame is
    /// held frozen until the next keyframe. Instead, Core downloads the segments in Safe mode and we cut that file with a
    /// re-encode, which starts on the exact frame.
    /// </summary>
    public sealed class ExactTrimService
    {
        private readonly VodDownloadRunner _runner;
        private readonly LibraryStorage _storage;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IVodPlaylistSource _playlists;
        private readonly IConfiguration _configuration;
        private readonly IOptions<DownloadSettings> _settings;

        public ExactTrimService(VodDownloadRunner runner, LibraryStorage storage, IHttpClientFactory httpClientFactory, IVodPlaylistSource playlists,
            IConfiguration configuration, IOptions<DownloadSettings> settings)
        {
            _runner = runner;
            _storage = storage;
            _httpClientFactory = httpClientFactory;
            _playlists = playlists;
            _configuration = configuration;
            _settings = settings;
        }

        /// <summary>Downloads an Exact entry: Safe download, then a frame-exact re-encoded cut into <paramref name="outputPath"/>.</summary>
        public async Task DownloadAsync(LibraryEntry entry, string outputPath, ILogger logger, CancellationToken cancellationToken)
        {
            var http = _httpClientFactory.CreateClient(nameof(ExactTrimService));
            var rendition = await _playlists.GetRenditionAsync(http, entry.VodId, entry.Quality, cancellationToken);
            if (rendition is null)
            {
                // Can't tell where the Safe file starts; fall back to Core's own Exact cut
                logger.LogWarning("Entry {EntryId}: couldn't read the playlist of VOD {VodId}; using Core's Exact trim.", entry.Id, entry.VodId);
                await _runner.DownloadAsync(entry.VodId, entry.Quality, VideoTrimMode.Exact, entry.StartTime, entry.EndTime,
                    outputPath, info => info, logger, cancellationToken);
                return;
            }

            var segments = rendition.Segments;
            var range = VodPlaylist.GetTrimRange(segments, entry.StartTime, entry.EndTime);
            var startSegment = segments[range.GetOffsetAndLength(segments.Count).Offset];

            var work = _storage.CreateWorkFolder($"exact-{entry.Id}");
            try
            {
                var safeFile = Path.Combine(work, "safe.mp4");
                await _runner.DownloadAsync(entry.VodId, entry.Quality, VideoTrimMode.Safe, entry.StartTime, entry.EndTime,
                    safeFile, info => info, logger, cancellationToken);

                var (start, length) = CutWithin(segments, startSegment, entry);
                await TrimAsync(safeFile, start, length, outputPath, work, logger, cancellationToken);
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException ex)
                {
                    logger.LogWarning("Couldn't delete exact trim work folder {Path}: {Message}", work, ex.Message);
                }
            }
        }

        /// <summary>Where the entry's trim sits inside a Safe download that starts at <paramref name="startSegment"/>.</summary>
        public static (double Start, double Length) CutWithin(IReadOnlyList<VodSegment> segments, VodSegment startSegment, LibraryEntry entry)
        {
            var end = entry.EndTime ?? TimeSpan.FromSeconds((double)segments.Sum(x => x.Duration));
            var start = entry.StartTime - TimeSpan.FromSeconds((double)startSegment.Offset);
            return (Math.Max(0, start.TotalSeconds), (end - entry.StartTime).TotalSeconds);
        }

        /// <summary>
        /// Cuts <paramref name="lengthSeconds"/> from <paramref name="startSeconds"/> of <paramref name="inputFile"/>, re-encoding so
        /// both video and audio start on the exact frame/sample.
        /// </summary>
        public Task TrimAsync(string inputFile, double startSeconds, double lengthSeconds, string outputFile, string workDirectory,
            ILogger logger, CancellationToken cancellationToken)
        {
            var settings = _settings.Value;
            var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
            return TrimAsync(ffmpegPath, inputFile, startSeconds, lengthSeconds, outputFile, workDirectory,
                settings.ExactTrimPreset, settings.ExactTrimCrf, logger, cancellationToken);
        }

        public static Task TrimAsync(string ffmpegPath, string inputFile, double startSeconds, double lengthSeconds, string outputFile,
            string workDirectory, string preset, int crf, ILogger logger, CancellationToken cancellationToken)
        {
            var invariant = CultureInfo.InvariantCulture;
            var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
            // Input seeking with a re-encode is frame-accurate: ffmpeg decodes from the keyframe before and drops what's before the cut
            if (startSeconds > 0)
                args.AddRange(["-ss", startSeconds.ToString("0.######", invariant)]);
            args.AddRange([
                "-i", inputFile,
                "-t", lengthSeconds.ToString("0.######", invariant),
                "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0",
                "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(invariant), "-pix_fmt", "yuv420p",
                // Audio is re-encoded too: copied AAC would start on a packet boundary before the cut instead of at it
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                outputFile,
            ]);

            return FfmpegRunner.RunAsync(ffmpegPath, args, workDirectory, logger, cancellationToken);
        }
    }
}
