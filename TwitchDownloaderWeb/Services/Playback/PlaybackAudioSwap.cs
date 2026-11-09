using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Services.Playback
{
    /// <summary>
    /// Plays saved clean audio over the parts Twitch muted. When the app player loads a VOD, every muted segment
    /// ("123-muted.ts") that has clean audio saved is pointed at our server instead, which fetches the muted video segment
    /// from Twitch and swaps its audio track for the saved one with ffmpeg (stream copy, ~10 s of video in well under a
    /// second). The saved audio is from the audio-only rendition, which shares the stream's clock, so with the timestamps
    /// kept as they are it lines up with the video exactly as it did live (the same fact the download restore relies on).
    /// Results are kept in downloads/work/play/ for a couple of hours (the worker clears work/ at startup).
    /// </summary>
    public sealed partial class PlaybackAudioSwap(VodPlaybackService playback, LibraryStorage storage, IServiceScopeFactory scopeFactory,
        IConfiguration configuration, ILogger<PlaybackAudioSwap> logger)
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromHours(2);
        private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _running = new();
        private DateTime _lastPruneUtc = DateTime.MinValue;

        [GeneratedRegex(@"^[A-Za-z0-9_]{1,40}$")]
        public static partial Regex KeyRegex { get; }

        // "123-muted.ts" → "123"
        [GeneratedRegex(@"^(?<key>[A-Za-z0-9_]{1,40})-muted\.ts$", RegexOptions.IgnoreCase)]
        private static partial Regex MutedTsRegex { get; }

        /// <summary>The segment key if a playlist entry is a muted MPEG-TS segment, else null.</summary>
        public static string? MutedKey(string segmentName) => MutedTsRegex.Match(segmentName) is { Success: true } m ? m.Groups["key"].Value : null;

        private string PlayFolder => Path.Combine(storage.RootFolder, LibraryStorage.WorkFolderName, "play");

        /// <summary>Keys of the VOD's segments that have clean audio saved.</summary>
        public async Task<HashSet<string>> GetCleanKeysAsync(long vodId, CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var keys = await db.RescuedAudioSegments.AsNoTracking()
                .Where(x => x.VodId == vodId && x.FilePath != null && !x.Unrecoverable)
                .Select(x => x.Key)
                .ToListAsync(cancellationToken);
            return keys.ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// The full path of the muted segment <paramref name="key"/> of a variant with the clean audio swapped in, or null if it
        /// can't be made (no saved audio, Twitch didn't return the segment, ffmpeg failed). The caller then serves the muted one.
        /// </summary>
        public async Task<string?> GetSegmentAsync(long vodId, int variant, string key, CancellationToken cancellationToken)
        {
            if (!KeyRegex.IsMatch(key) || variant < 0)
                return null;

            var output = Path.Combine(PlayFolder, vodId.ToString(), variant.ToString(), $"{key}.ts");
            if (File.Exists(output))
                return output;

            PruneOldFiles();
            // One swap per segment even if the player asks twice; it isn't cancelled with a single request
            var job = _running.GetOrAdd(output, _ => new Lazy<Task<string?>>(() => SwapAsync(vodId, variant, key, output)));
            try
            {
                return await job.Value.WaitAsync(cancellationToken);
            }
            finally
            {
                if (job.Value.IsCompleted)
                    _running.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(output, job));
            }
        }

        private async Task<string?> SwapAsync(long vodId, int variant, string key, string output)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var cancellationToken = timeout.Token;
            string? mutedPath = null;
            try
            {
                string? cleanPath;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                    var relative = await db.RescuedAudioSegments.AsNoTracking()
                        .Where(x => x.VodId == vodId && x.Key == key && x.FilePath != null && !x.Unrecoverable)
                        .Select(x => x.FilePath)
                        .FirstOrDefaultAsync(cancellationToken);
                    cleanPath = relative is null ? null : storage.Resolve(relative);
                }

                if (cleanPath is null || !File.Exists(cleanPath))
                    return null;

                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                mutedPath = output + ".muted";
                using (var response = await playback.GetSegmentAsync(vodId, variant, $"{key}-muted.ts", cancellationToken))
                {
                    if (response is not { IsSuccessStatusCode: true })
                        return null;

                    await using var file = File.Create(mutedPath);
                    await response.Content.CopyToAsync(file, cancellationToken);
                }

                var ffmpegPath = configuration["Ffmpeg:Path"] is { Length: > 0 } configured ? configured : "ffmpeg";
                var temp = output + ".tmp";
                await SwapAudioAsync(ffmpegPath, mutedPath, cleanPath, temp, logger, cancellationToken);
                File.Move(temp, output, overwrite: true);
                return output;
            }
            catch (Exception ex)
            {
                // Never log URLs here: segment URLs can carry signed tokens
                logger.LogWarning("Couldn't swap in clean audio for VOD {VodId} segment {Key}: {Error}", vodId, key, ex.Message);
                return null;
            }
            finally
            {
                TryDelete(mutedPath);
                TryDelete(output + ".tmp");
            }
        }

        /// <summary>
        /// Writes <paramref name="output"/> (MPEG-TS) with the video of <paramref name="videoTs"/> and the audio of
        /// <paramref name="audioTs"/>, both copied with their original timestamps.
        /// </summary>
        /// <remarks>
        /// The player decodes the whole VOD with one audio setup, so if the saved audio has a different sample rate or channel
        /// count than the muted segment's (seen in testing, not on Twitch so far), it is re-encoded to match. That costs the AAC
        /// encoder's ~21 ms priming delay, so the audio is otherwise copied.
        /// </remarks>
        public static async Task SwapAudioAsync(string ffmpegPath, string videoTs, string audioTs, string output, ILogger logger, CancellationToken cancellationToken)
        {
            var target = AdtsFormat.Read(await ReadHeadAsync(videoTs, cancellationToken));
            var clean = AdtsFormat.Read(await ReadHeadAsync(audioTs, cancellationToken));
            List<string> audioCodec = target is { SampleRate: > 0, Channels: > 0 } t && clean is { } c && (c.SampleRate != t.SampleRate || c.Channels != t.Channels)
                ? ["-c:a", "aac", "-b:a", "192k", "-ar", t.SampleRate.ToString(), "-ac", t.Channels.ToString()]
                : ["-c:a", "copy"];

            await FfmpegRunner.RunAsync(ffmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-copyts",
                "-i", videoTs,
                "-i", audioTs,
                "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "copy",
                .. audioCodec,
                "-muxdelay", "0", "-muxpreload", "0",
                "-mpegts_copyts", "1",
                "-f", "mpegts", output,
            ], Path.GetDirectoryName(output)!, logger, cancellationToken);
        }

        private static async Task<byte[]> ReadHeadAsync(string path, CancellationToken cancellationToken)
        {
            await using var file = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(file.Length, 256 * 1024)];
            await file.ReadExactlyAsync(buffer, cancellationToken);
            return buffer;
        }

        private void PruneOldFiles()
        {
            var now = DateTime.UtcNow;
            if (now - _lastPruneUtc < PruneEvery)
                return;
            _lastPruneUtc = now;

            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(PlayFolder))
                        return;
                    foreach (var file in Directory.EnumerateFiles(PlayFolder, "*", SearchOption.AllDirectories))
                    {
                        if (now - File.GetLastWriteTimeUtc(file) > KeepFor)
                            TryDelete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Couldn't prune played audio swaps");
                }
            });
        }

        private static void TryDelete(string? path)
        {
            if (path is null) return;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next prune or the startup clear
            }
        }
    }
}
