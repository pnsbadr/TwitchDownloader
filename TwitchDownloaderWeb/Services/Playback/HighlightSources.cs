using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace TwitchDownloaderWeb.Services.Playback
{
    /// <summary>
    /// Which broadcast a highlight was cut from (badrfly 2026-11-21: highlights show under their VOD in the library). Twitch's video list
    /// doesn't say, but a highlight's playlists live in its broadcast's folder on Twitch's CDN, named "{hash}_{login}_{streamId}_{epoch}",
    /// where epoch is the second the stream started (the broadcast VOD's createdAt). The page puts the highlight under the broadcast that
    /// started then. Unknown (sub-only, gone, another folder shape) = null; the page then uses the broadcast streamed last before it.
    /// </summary>
    public sealed partial class HighlightSources(VodPlaybackService playback, IMemoryCache cache, ILogger<HighlightSources> logger)
    {
        private static readonly TimeSpan KnownFor = TimeSpan.FromDays(7);
        private static readonly TimeSpan UnknownFor = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim Gate = new(4);

        // A folder like "a1b2c3d4e5f6a7b8c9d0_arky_41234567890_1700000000": the stream ID and its start (Unix seconds) at the end
        [GeneratedRegex(@"_(\d+)_(\d{9,11})$")]
        private static partial Regex FolderRegex { get; }

        /// <summary>The stream start in a rendition URL's folder, or null.</summary>
        public static DateTime? StartFromPlaylistUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;
            foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = FolderRegex.Match(segment);
                if (match.Success && long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            return null;
        }

        /// <summary>When the highlight's broadcast started, or null when it can't be told.</summary>
        public async Task<DateTime?> GetSourceStartAsync(long highlightId, CancellationToken cancellationToken)
        {
            var key = $"highlight-source:{highlightId}";
            if (cache.TryGetValue(key, out DateTime? known))
                return known;
            DateTime? start = null;
            await Gate.WaitAsync(cancellationToken);
            try
            {
                var variants = await playback.GetVariantsAsync(highlightId, cancellationToken);
                start = variants.Variants.Select(x => StartFromPlaylistUrl(x.PlaylistUrl)).FirstOrDefault(x => x is not null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Couldn't read highlight {VodId}'s playlist: {Error}", highlightId, ex.Message);
            }
            finally
            {
                Gate.Release();
            }
            cache.Set(key, start, start is null ? UnknownFor : KnownFor);
            return start;
        }
    }
}
