using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;
using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Thumbnails for clip and video cards: one frame from the middle of what the clip or video plays (its kept parts), instead of the
    /// VOD's thumbnail. Taken from the clip's own file when it has one, else from one segment of the VOD's HLS (a low rendition), so
    /// reference-only clips and videos get one too.
    /// </summary>
    /// <remarks>
    /// A frame depends only on the VOD and the time in it, so files are "thumbs/{vodId}/{tenths of a second}.jpg": a re-trim or cut
    /// that moves the middle gives a new URL and a new frame. Unused files are deleted after <see cref="KeepFor"/> (they're made again
    /// when shown).
    /// </remarks>
    public sealed partial class CardThumbnailService(VodPlaybackService playback, LibraryStorage storage, IConfiguration configuration,
        IMemoryCache cache, ILogger<CardThumbnailService> logger)
    {
        public const string FolderName = "thumbs";
        public const int Width = 320;

        /// <summary>Files not served for this long are deleted by the cleanup service.</summary>
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

        private static readonly TimeSpan FailureCacheTime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PlaylistCacheTime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan MakeTimeout = TimeSpan.FromSeconds(60);

        /// <summary>How long a frame may wait for its turn; it isn't a failure when it runs out (the next showing asks again).</summary>
        private static readonly TimeSpan QueueTimeout = TimeSpan.FromMinutes(5);

        // Each frame needs a Twitch request and an ffmpeg run; a library page asks for many at once
        private readonly SemaphoreSlim _gate = new(3);
        private readonly ConcurrentDictionary<string, Task<bool>> _running = new();

        [GeneratedRegex(@"#EXT-X-MAP:.*?URI=""(?<uri>[^""]+)""")]
        private static partial Regex MapRegex { get; }

        // --- where the frame is ---

        /// <summary>The VOD second in the middle of what the parts play (in order), or null if they're empty.</summary>
        public static double? Middle(IEnumerable<(double Start, double End)> parts)
        {
            var list = parts.Where(x => x.End > x.Start).ToList();
            var total = list.Sum(x => x.End - x.Start);
            if (list.Count == 0)
                return null;

            var left = total / 2;
            foreach (var (start, end) in list)
            {
                if (left <= end - start)
                    return start + left;
                left -= end - start;
            }

            return list[^1].End;
        }

        /// <summary>Where a VOD second is in a file that plays <paramref name="fileParts"/> back to back (a second in a gap: the next part's start).</summary>
        public static double FileSeconds(IEnumerable<(double Start, double End)> fileParts, double vodSeconds)
        {
            var at = 0.0;
            foreach (var (start, end) in fileParts)
            {
                if (vodSeconds < end)
                    return at + Math.Max(0, vodSeconds - start);
                at += end - start;
            }
            return at;
        }

        /// <summary>A VOD clip's kept parts (Twitch clip entries have Twitch's own thumbnail, so none).</summary>
        public static IReadOnlyList<(double Start, double End)> EntryParts(LibraryEntry entry)
        {
            if (entry.Kind != EntryKind.Vod)
                return [];
            var start = entry.StartTime.TotalSeconds;
            var end = entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds;
            return end > start ? CutOuts.Kept(start, end, CutOuts.Read(entry.Cuts)) : [];
        }

        /// <summary>Tenths of a second into the VOD: the frame's name and the URL's version.</summary>
        public static long? EntryFrame(LibraryEntry entry) => Tenths(Middle(EntryParts(entry)));

        /// <param name="parts">What the video plays, in order (see <c>VideoEndpoints.KeptParts</c>).</param>
        public static long? VideoFrame(IEnumerable<(double Start, double End)> parts) => Tenths(Middle(parts));

        private static long? Tenths(double? seconds) => seconds is { } s ? (long)Math.Round(s * 10) : null;

        public static string? EntryUrl(LibraryEntry entry) => EntryFrame(entry) is { } t ? $"/api/entries/{entry.Id}/thumbnail?t={t}" : null;

        public static string? VideoUrl(int videoId, IEnumerable<(double Start, double End)> parts) =>
            VideoFrame(parts) is { } t ? $"/api/videos/{videoId}/thumbnail?t={t}" : null;

        // --- making it ---

        /// <summary>The frame of a VOD clip (from its file if it has one), or null if it couldn't be made.</summary>
        public Task<string?> GetForEntryAsync(LibraryEntry entry, CancellationToken cancellationToken)
        {
            var parts = EntryParts(entry);
            if (Middle(parts) is not { } middle)
                return Task.FromResult<string?>(null);

            // The file plays its range without the parts left out of it (none for files made since 2026-11-16) back to back (a Safe cut
            // starts a little early, close enough for a thumbnail)
            var file = new[] { entry.FilePath, entry.Status == EntryStatus.ReferenceOnly ? entry.GeneratedFilePath : null }
                .Where(x => !string.IsNullOrEmpty(x)).Select(x => storage.Resolve(x!)).FirstOrDefault(File.Exists);
            var fileSeconds = FileSeconds(CutOuts.Kept(entry.StartTime.TotalSeconds, entry.EndTime?.TotalSeconds ?? entry.VodLengthSeconds,
                CutOuts.Read(entry.FileCuts)), middle);
            return GetAsync(entry.VodId, middle, file is null ? null : (file, fileSeconds), cancellationToken);
        }

        public Task<string?> GetForVideoAsync(long vodId, IEnumerable<(double Start, double End)> parts, CancellationToken cancellationToken) =>
            Middle(parts) is { } middle ? GetAsync(vodId, middle, null, cancellationToken) : Task.FromResult<string?>(null);

        private async Task<string?> GetAsync(long vodId, double vodSeconds, (string Path, double Seconds)? file, CancellationToken cancellationToken)
        {
            var tenths = (long)Math.Round(vodSeconds * 10);
            var path = Path.Combine(storage.RootFolder, FolderName, vodId.ToString(CultureInfo.InvariantCulture), $"{tenths}.jpg");
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }

            if (cache.TryGetValue(FailureKey(path), out _))
                return null;

            // One maker per frame; it isn't cancelled when the request that started it goes away
            var task = _running.GetOrAdd(path, _ => MakeAsync(vodId, tenths / 10.0, file, path));
            _ = task.ContinueWith(t => _running.TryRemove(new KeyValuePair<string, Task<bool>>(path, t)), TaskScheduler.Default);
            return await task.WaitAsync(cancellationToken) ? path : null;
        }

        private async Task<bool> MakeAsync(long vodId, double vodSeconds, (string Path, double Seconds)? file, string path)
        {
            await Task.Yield();
            // Only time the work itself: a library page asks for many frames at once, and frames waiting their turn used to run out of
            // their 60 s in the queue and be remembered as failed for 10 minutes (the cards then showed the VOD's thumbnail)
            if (!await _gate.WaitAsync(QueueTimeout))
                return false;

            using var timeout = new CancellationTokenSource(MakeTimeout);
            var token = timeout.Token;
            string? work = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp.jpg";
                var ffmpeg = configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";

                var made = false;
                if (file is { } f)
                {
                    try
                    {
                        await ExtractFrameAsync(ffmpeg, f.Path, f.Seconds, seekOnInput: true, temp, logger, token);
                        made = File.Exists(temp);
                    }
                    catch (InvalidOperationException ex)
                    {
                        logger.LogWarning(ex, "Could not take a card thumbnail from {Path}; trying the VOD.", f.Path);
                    }
                }

                if (!made)
                {
                    work = storage.CreateWorkFolder("thumb");
                    if (await DownloadSegmentAsync(vodId, vodSeconds, work, token) is not { } segment)
                    {
                        Fail(path);
                        return false;
                    }

                    await ExtractFrameAsync(ffmpeg, segment.Path, segment.Seconds, seekOnInput: false, temp, logger, token);
                    made = File.Exists(temp);
                }

                if (!made)
                {
                    Fail(path);
                    return false;
                }

                File.Move(temp, path, true);
                return true;
            }
            catch (Exception ex)
            {
                // Nobody may be waiting any more, so nothing is thrown from here
                // No URLs in the message: Twitch's carry signed tokens
                logger.LogWarning("Could not make the card thumbnail for VOD {VodId} at {Seconds} s: {Error}", vodId, vodSeconds,
                    ex is HttpRequestException ? "Twitch couldn't be reached" : ex.Message);
                Fail(path);
                return false;
            }
            finally
            {
                _gate.Release();
                if (work is not null)
                {
                    try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>
        /// Saves the segment of a low (≥ 360p if there is one) rendition that holds the time, with its init segment in front for fMP4.
        /// Returns the file and the time inside it, or null if the VOD or the segment can't be read.
        /// </summary>
        private async Task<(string Path, double Seconds)?> DownloadSegmentAsync(long vodId, double vodSeconds, string folder, CancellationToken cancellationToken)
        {
            var variants = await playback.GetVariantsAsync(vodId, cancellationToken);
            if (variants.Status != PlaybackStatus.Ok || variants.Variants.Count == 0)
                return null;

            var index = PickVariant(variants.Variants);
            var playlistKey = $"thumbs:playlist:{vodId}:{index}";
            if (!cache.TryGetValue(playlistKey, out string? playlist) || playlist is null)
            {
                playlist = await playback.GetRawMediaPlaylistAsync(vodId, index, cancellationToken);
                if (playlist is null)
                    return null;
                cache.Set(playlistKey, playlist, PlaylistCacheTime);
            }

            if (FindSegment(playlist, vodSeconds) is not { } found)
                return null;

            var output = Path.Combine(folder, Path.GetExtension(found.Name) is ".ts" ? "segment.ts" : "segment.mp4");
            await using (var stream = File.Create(output))
            {
                if (found.InitName is { } init && !await CopySegmentAsync(vodId, index, init, stream, cancellationToken))
                    return null;
                if (!await CopySegmentAsync(vodId, index, found.Name, stream, cancellationToken))
                    return null;
            }

            return (output, found.Seconds);
        }

        private async Task<bool> CopySegmentAsync(long vodId, int variant, string name, Stream to, CancellationToken cancellationToken)
        {
            using var response = await playback.GetSegmentAsync(vodId, variant, name, cancellationToken);
            if (response is not { IsSuccessStatusCode: true })
                return false;
            await response.Content.CopyToAsync(to, cancellationToken);
            return true;
        }

        /// <summary>The smallest rendition at least 360 high (variants are best first), else the smallest.</summary>
        public static int PickVariant(IReadOnlyList<PlaybackVariant> variants)
        {
            for (var i = variants.Count - 1; i >= 0; i--)
                if (variants[i].Height >= 360)
                    return i;
            return variants.Count - 1;
        }

        /// <summary>The segment holding the time (the last one if it's past the end), its init segment, and the time inside it.</summary>
        public static (string Name, string? InitName, double Seconds)? FindSegment(string playlistText, double vodSeconds)
        {
            var segments = VodPlaylist.ParseSegments(playlistText);
            if (segments.Count == 0)
                return null;

            var segment = segments.FirstOrDefault(x => (double)(x.Offset + x.Duration) > vodSeconds) ?? segments[^1];
            var name = SegmentName(segment.Path);
            if (name is null)
                return null;

            string? init = null;
            if (MapRegex.Match(playlistText) is { Success: true } map && (init = SegmentName(map.Groups["uri"].Value)) is null)
                return null;

            var inside = Math.Clamp(vodSeconds - (double)segment.Offset, 0, Math.Max(0, (double)segment.Duration - 0.05));
            return (name, init, inside);
        }

        private static string? SegmentName(string path) => HlsPlaylists.SegmentNameRegex.IsMatch(path) ? path : null;

        // --- full-size frames for the players' screenshot button (badrfly 2026-11-21) ---

        /// <summary>
        /// One frame of the VOD at full size as a PNG, from one segment of the best rendition (or <paramref name="variant"/> when it
        /// exists): the screenshot button's way when the page can't read the picture itself (Twitch's player). Nothing is kept on disk.
        /// Null when the VOD or the segment can't be read.
        /// </summary>
        public async Task<byte[]?> GrabFrameAsync(long vodId, double vodSeconds, int? variant, CancellationToken cancellationToken)
        {
            var variants = await playback.GetVariantsAsync(vodId, cancellationToken);
            if (variants.Status != PlaybackStatus.Ok || variants.Variants.Count == 0)
                return null;
            // Variants are best first; the page passes the one it plays, if any
            var index = variant is { } v && v >= 0 && v < variants.Variants.Count ? v : 0;

            if (!await _gate.WaitAsync(QueueTimeout, cancellationToken))
                return null;
            var work = storage.CreateWorkFolder("frame");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(MakeTimeout);
                var playlist = await playback.GetRawMediaPlaylistAsync(vodId, index, timeout.Token);
                if (playlist is null || FindSegment(playlist, vodSeconds) is not { } found)
                    return null;

                var input = Path.Combine(work, Path.GetExtension(found.Name) is ".ts" ? "segment.ts" : "segment.mp4");
                await using (var stream = File.Create(input))
                {
                    if (found.InitName is { } init && !await CopySegmentAsync(vodId, index, init, stream, timeout.Token))
                        return null;
                    if (!await CopySegmentAsync(vodId, index, found.Name, stream, timeout.Token))
                        return null;
                }

                var output = Path.Combine(work, "frame.png");
                var ffmpeg = configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                var at = found.Seconds.ToString("0.###", CultureInfo.InvariantCulture);
                await FfmpegRunner.RunAsync(ffmpeg, ["-hide_banner", "-y", "-i", input, "-ss", at, "-frames:v", "1", "-an", "-f", "image2", "-c:v", "png", output],
                    work, logger, timeout.Token);
                return File.Exists(output) ? await File.ReadAllBytesAsync(output, cancellationToken) : null;
            }
            finally
            {
                _gate.Release();
                try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <param name="seekOnInput">True for a whole file (fast seek); false for one HLS segment, whose timestamps don't start at 0:
        /// the output seek decodes from the segment's start (a keyframe) and counts from there.</param>
        public static Task ExtractFrameAsync(string ffmpeg, string input, double seconds, bool seekOnInput, string output, ILogger logger,
            CancellationToken cancellationToken)
        {
            var at = seconds.ToString("0.###", CultureInfo.InvariantCulture);
            List<string> args = ["-hide_banner", "-y"];
            if (seekOnInput)
                args.AddRange(["-ss", at, "-i", input]);
            else
                args.AddRange(["-i", input, "-ss", at]);
            args.AddRange(["-frames:v", "1", "-an", "-vf", $"scale={Width}:-2", "-q:v", "4", "-f", "image2", output]);
            return FfmpegRunner.RunAsync(ffmpeg, args, Path.GetDirectoryName(output)!, logger, cancellationToken);
        }

        private void Fail(string path) => cache.Set(FailureKey(path), true, FailureCacheTime);

        private static string FailureKey(string path) => "thumbs:failed:" + path;

        // Keeps a shown thumbnail from being cleaned up (the write time is "last shown", updated at most daily)
        private static void Touch(string path)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Deletes thumbnails not shown for <see cref="KeepFor"/>, leftovers of failed runs and empty VOD folders.</summary>
        public void Cleanup()
        {
            var root = Path.Combine(storage.RootFolder, FolderName);
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow - KeepFor;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var old = File.GetLastWriteTimeUtc(file) < cutoff;
                    var leftover = file.EndsWith(".tmp.jpg", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1);
                    if (old || leftover)
                        storage.TryDelete(file);
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
