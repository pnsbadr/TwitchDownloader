using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>
    /// Makes a library file with rescued clean audio in place of Twitch's muted parts:
    /// 1. Core downloads the range in Safe mode, so the file starts exactly on a known segment.
    /// 2. The clean audio is placed by the segments' own timestamps and mixed in; the video is copied untouched.
    /// 3. For an Exact entry, the fixed file is trimmed the same way Core trims, so the new audio stays in sync.
    /// </summary>
    public sealed class AudioRestoreService
    {
        private const int HeadBytes = 256 * 1024;

        private readonly VodDownloadRunner _runner;
        private readonly LibraryStorage _storage;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public AudioRestoreService(VodDownloadRunner runner, LibraryStorage storage, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _runner = runner;
            _storage = storage;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        /// <returns>Seconds of audio restored, or null if no rescued audio covers the entry's muted parts (download it normally).</returns>
        public async Task<double?> TryDownloadRestoredAsync(LibraryDbContext db, LibraryEntry entry, string outputPath, ILogger logger, CancellationToken cancellationToken)
        {
            var clean = await db.RescuedAudioSegments.AsNoTracking()
                .Where(x => x.VodId == entry.VodId && x.FilePath != null && x.FirstAudioPts != null)
                .ToDictionaryAsync(x => x.Key, cancellationToken);
            if (clean.Count == 0)
                return null;

            var http = _httpClientFactory.CreateClient(nameof(AudioRestoreService));
            var rendition = await VodPlaylist.GetRenditionAsync(http, entry.VodId, entry.Quality, cancellationToken);
            if (rendition is null)
                return null;

            var segments = rendition.Segments;
            var range = VodPlaylist.GetTrimRange(segments, entry.StartTime, entry.EndTime);
            var runs = RestorePlanner.FindRuns(segments, range, key => clean.ContainsKey(key) && File.Exists(_storage.Resolve(clean[key].FilePath!)));
            if (runs.Count == 0)
                return null;

            if (segments.Any(x => !x.Path.Contains(".ts", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("Entry {EntryId}: VOD {VodId} isn't MPEG-TS; downloading without restoring audio.", entry.Id, entry.VodId);
                return null;
            }

            var ffmpegPath = _configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
            var work = _storage.CreateWorkFolder($"restore-{entry.Id}");
            try
            {
                var safeFile = Path.Combine(work, "safe.mp4");
                await _runner.DownloadAsync(entry.VodId, entry.Quality, VideoTrimMode.Safe, entry.StartTime, entry.EndTime,
                    safeFile, info => info, logger, cancellationToken);

                var startSegment = segments[range.GetOffsetAndLength(segments.Count).Offset];
                var firstHead = await ReadHeadAsync(http, rendition, startSegment, cancellationToken);
                var runHeads = new List<TsTimestamps>(runs.Count);
                foreach (var run in runs)
                    runHeads.Add(run[0] == startSegment ? firstHead : await ReadHeadAsync(http, rendition, run[0], cancellationToken));

                var (audioStart, placed) = RestorePlanner.Place(segments, range, runs,
                    key => new CleanAudioFile(_storage.Resolve(clean[key].FilePath!), clean[key].FirstAudioPts!.Value), firstHead, runHeads);

                var restoredFile = Path.Combine(work, "restored.mp4");
                await AudioSplicer.SpliceAsync(ffmpegPath, safeFile, audioStart, placed, restoredFile, work, logger, cancellationToken);

                if (entry.TrimMode == VideoTrimMode.Exact)
                {
                    var end = entry.EndTime ?? TimeSpan.FromSeconds((double)segments.Sum(x => x.Duration));
                    var start = entry.StartTime - TimeSpan.FromSeconds((double)startSegment.Offset);
                    await AudioSplicer.TrimAsync(ffmpegPath, restoredFile, start.TotalSeconds, (end - entry.StartTime).TotalSeconds, outputPath, work, logger, cancellationToken);
                }
                else
                {
                    File.Move(restoredFile, outputPath, true);
                }

                return RestoredSeconds(runs, entry);
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException ex)
                {
                    logger.LogWarning("Couldn't delete restore work folder {Path}: {Message}", work, ex.Message);
                }
            }
        }

        /// <summary>Muted time inside the entry's trim that got clean audio.</summary>
        private static double RestoredSeconds(IEnumerable<IReadOnlyList<VodSegment>> runs, LibraryEntry entry)
        {
            var start = (decimal)entry.StartTime.TotalSeconds;
            var end = entry.EndTime is { } e ? (decimal)e.TotalSeconds : decimal.MaxValue;
            return (double)runs.SelectMany(x => x).Sum(s => Math.Max(0, Math.Min(end, s.Offset + s.Duration) - Math.Max(start, s.Offset)));
        }

        /// <summary>The first bytes of a segment in the download's quality, enough to read its starting timestamps.</summary>
        private static async Task<TsTimestamps> ReadHeadAsync(HttpClient http, VodRendition rendition, VodSegment segment, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, rendition.SegmentUri(segment.Path));
            request.Headers.Range = new RangeHeaderValue(0, HeadBytes - 1);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[HeadBytes];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
                read += n;

            var timestamps = TsTimestamps.Read(buffer.AsSpan(0, read));
            if (timestamps.StartPts is null)
                throw new InvalidOperationException($"No timestamps found at the start of segment {segment.Key}.");

            return timestamps;
        }
    }
}
