using Microsoft.Extensions.Caching.Memory;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderWeb.Services.Playback
{
    public enum PlaybackStatus { Ok, NotFound, Restricted }

    public sealed record PlaybackVariants(PlaybackStatus Status, IReadOnlyList<PlaybackVariant> Variants)
    {
        public static readonly PlaybackVariants NotFound = new(PlaybackStatus.NotFound, []);
        public static readonly PlaybackVariants Restricted = new(PlaybackStatus.Restricted, []);
    }

    /// <summary>Where a VOD's renditions come from. Twitch in the app; a fake in tests.</summary>
    public interface IVodPlaybackSource
    {
        Task<PlaybackVariants> GetVariantsAsync(long vodId, CancellationToken cancellationToken);
    }

    /// <summary>Gets the master playlist the same way Core's VideoDownloader does (GQL playback access token, then usher).</summary>
    public sealed class TwitchVodPlaybackSource : IVodPlaybackSource
    {
        public async Task<PlaybackVariants> GetVariantsAsync(long vodId, CancellationToken cancellationToken)
        {
            // The token and signature only go to Twitch; they are never logged or sent to the browser
            var accessToken = (await TwitchHelper.GetVideoToken(vodId, null)).data?.videoPlaybackAccessToken;
            if (accessToken is null)
                return PlaybackVariants.NotFound;

            var master = await TwitchHelper.GetVideoPlaylist(vodId, accessToken.value, accessToken.signature);
            if (master.Contains("vod_manifest_restricted") || master.Contains("unauthorized_entitlements"))
                return PlaybackVariants.Restricted;
            if (!master.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
                return PlaybackVariants.NotFound; // usher answers a deleted VOD with a JSON error

            return new PlaybackVariants(PlaybackStatus.Ok, VodPlaybackService.PickVariants(M3U8.Parse(master)));
        }
    }

    /// <summary>Serves a VOD's HLS playlists (and, if asked, its segments) to the page's player.</summary>
    public sealed class VodPlaybackService(IVodPlaybackSource source, IMemoryCache cache, IHttpClientFactory httpClientFactory)
    {
        // The master's rendition URLs don't expire for public VODs; re-reading it now and then still picks up a VOD that was
        // deleted, unpublished or finished transcoding.
        private static readonly TimeSpan VariantCacheTime = TimeSpan.FromMinutes(10);

        public const string HttpClientName = nameof(VodPlaybackService);

        /// <summary>
        /// The landscape video renditions, best first. Audio-only is left out (a player picking it on a slow connection would
        /// show a black picture) and so are the portrait renditions of dual-format streams, unless a VOD has nothing else.
        /// </summary>
        public static IReadOnlyList<PlaybackVariant> PickVariants(M3U8 master)
        {
            var qualities = VideoQualities.FromM3U8(master).Qualities.Where(x => x.Item.IsPlaylist).ToList();
            var video = qualities.Where(x => !x.Item.IsAudioOnly()).ToList();
            var landscape = video.Where(x => x.Orientation != VideoOrientation.Portrait).ToList();
            var chosen = landscape.Count > 0 ? landscape : video.Count > 0 ? video : qualities;

            return chosen
                .OrderByDescending(x => x.Resolution.Height)
                .ThenByDescending(x => x.Framerate)
                .Select(x => new PlaybackVariant(x.Name, x.Item.Path, x.Item.StreamInfo?.Bandwidth ?? 0, x.Item.StreamInfo?.Codecs ?? [],
                    x.Resolution.Width, x.Resolution.Height, x.Framerate))
                .ToList();
        }

        public async Task<PlaybackVariants> GetVariantsAsync(long vodId, CancellationToken cancellationToken)
        {
            if (cache.TryGetValue(CacheKey(vodId), out PlaybackVariants? cached) && cached is not null)
                return cached;

            var variants = await source.GetVariantsAsync(vodId, cancellationToken);
            cache.Set(CacheKey(vodId), variants, variants.Status == PlaybackStatus.Ok ? VariantCacheTime : TimeSpan.FromMinutes(1));
            return variants;
        }

        /// <summary>The variant's media playlist with every segment URL rewritten, or null if the VOD or variant is gone.</summary>
        public async Task<string?> GetMediaPlaylistAsync(long vodId, int variantIndex, Func<Uri, Uri, string> segmentUrl, CancellationToken cancellationToken)
        {
            if (await GetVariantAsync(vodId, variantIndex, cancellationToken) is not { } variant)
                return null;

            using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(variant.PlaylistUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Gone since the master was read: read it again next time
                cache.Remove(CacheKey(vodId));
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var baseUrl = variant.BaseUrl;
            return HlsPlaylists.RewriteMedia(text, baseUrl, segment => segmentUrl(segment, baseUrl));
        }

        /// <summary>Fetches one segment of a variant from Twitch for the player (the caller disposes the response).</summary>
        public async Task<HttpResponseMessage?> GetSegmentAsync(long vodId, int variantIndex, string segmentName, CancellationToken cancellationToken)
        {
            if (!HlsPlaylists.SegmentNameRegex.IsMatch(segmentName))
                return null;
            if (await GetVariantAsync(vodId, variantIndex, cancellationToken) is not { } variant)
                return null;

            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(variant.BaseUrl, segmentName));
            return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private async Task<PlaybackVariant?> GetVariantAsync(long vodId, int variantIndex, CancellationToken cancellationToken)
        {
            var variants = await GetVariantsAsync(vodId, cancellationToken);
            return variantIndex >= 0 && variantIndex < variants.Variants.Count ? variants.Variants[variantIndex] : null;
        }

        private static string CacheKey(long vodId) => $"playback:{vodId}";
    }
}
