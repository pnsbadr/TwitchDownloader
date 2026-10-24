using System.Text.RegularExpressions;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Interfaces;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>One segment of a VOD rendition's playlist.</summary>
    /// <param name="Key">The segment's name without "-muted"/"-unmuted" or extension (e.g. "123"), the same in every rendition.</param>
    /// <param name="Path">The playlist's path for the segment, relative to the rendition's base URL.</param>
    /// <param name="Offset">Start of the segment from the start of the VOD (sum of the earlier #EXTINF durations), in seconds.</param>
    public sealed record VodSegment(int Index, string Key, string Path, bool IsMuted, decimal Duration, decimal Offset)
    {
        /// <summary>The path of the clean copy Twitch keeps for a while after muting (the same trick Core uses).</summary>
        public string UnmutedPath => IsMuted ? Path.Replace("-muted", "") : Path;
    }

    public sealed record VodRendition(string QualityName, Uri BaseUrl, IReadOnlyList<VodSegment> Segments, bool IsLive)
    {
        public Uri SegmentUri(string path) => new(BaseUrl, path);
    }

    /// <summary>Reads a VOD's segment list with Core's public Twitch and M3U8 helpers, and finds the muted segments in it.</summary>
    public static partial class VodPlaylist
    {
        // "123-muted.ts" is muted; "123-unmuted.ts" was muted and later restored, so it is clean
        [GeneratedRegex(@"^(?<key>.+?)(?<suffix>-muted|-unmuted)?\.(?:ts|mp4)$", RegexOptions.IgnoreCase)]
        private static partial Regex SegmentNameRegex { get; }

        public static IReadOnlyList<VodSegment> ParseSegments(string playlistText)
        {
            var playlist = M3U8.Parse(playlistText);
            var segments = new List<VodSegment>(playlist.Streams.Length);
            var offset = 0m;
            foreach (var stream in playlist.Streams)
            {
                var name = DownloadTools.RemoveQueryString(stream.Path);
                name = name[(name.LastIndexOf('/') + 1)..];

                var match = SegmentNameRegex.Match(name);
                var key = match.Success ? match.Groups["key"].Value : name;
                var isMuted = match.Groups["suffix"].Value.Equals("-muted", StringComparison.OrdinalIgnoreCase);

                var duration = stream.PartInfo?.Duration ?? 0;
                segments.Add(new VodSegment(segments.Count, key, stream.Path, isMuted, duration, offset));
                offset += duration;
            }

            return segments;
        }

        /// <summary>A live (still recording) VOD's playlist is an EVENT playlist without an end tag.</summary>
        public static bool IsLivePlaylist(string playlistText) => !playlistText.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);

        /// <summary>
        /// The segments Core downloads for a trim, using the same rules as Core's VideoDownloader.GetStreamListTrim:
        /// from the segment containing <paramref name="start"/> up to and including the one containing <paramref name="end"/>.
        /// </summary>
        public static Range GetTrimRange(IReadOnlyList<VodSegment> segments, TimeSpan start, TimeSpan? end)
        {
            var startIndex = 0;
            if (start > TimeSpan.Zero)
            {
                var trimSeconds = (decimal)start.TotalSeconds;
                var time = 0m;
                foreach (var segment in segments)
                {
                    if (time + segment.Duration > trimSeconds)
                        break;

                    startIndex++;
                    time += segment.Duration;
                }
            }

            var endIndex = segments.Count;
            if (end is { } endTime)
            {
                var trimSeconds = (decimal)endTime.TotalSeconds;
                var time = segments.Sum(x => x.Duration);
                for (var i = segments.Count - 1; i >= 0; i--)
                {
                    if (time - segments[i].Duration < trimSeconds)
                        break;

                    endIndex--;
                    time -= segments[i].Duration;
                }
            }

            return new Range(startIndex, Math.Max(startIndex, endIndex));
        }

        /// <param name="quality">A quality name like "720p60"; null or unknown uses the best, as Core does.</param>
        /// <returns>The rendition, or null if the VOD is gone or restricted.</returns>
        public static async Task<VodRendition?> GetRenditionAsync(HttpClient httpClient, long vodId, string? quality, CancellationToken cancellationToken)
        {
            if (await GetQualitiesAsync(vodId) is not { } qualities)
                return null;

            var chosen = qualities.GetQuality(quality) ?? qualities.BestQuality();
            return chosen is null ? null : await LoadRenditionAsync(httpClient, chosen, cancellationToken);
        }

        /// <summary>The audio-only rendition, or the lowest video quality if a VOD has none.</summary>
        public static async Task<VodRendition?> GetAudioRenditionAsync(HttpClient httpClient, long vodId, CancellationToken cancellationToken)
        {
            if (await GetQualitiesAsync(vodId) is not { } qualities)
                return null;

            var audio = qualities.Qualities.FirstOrDefault(x => x.Item.IsAudioOnly()) ?? qualities.WorstQuality();
            return audio is null ? null : await LoadRenditionAsync(httpClient, audio, cancellationToken);
        }

        private static async Task<IVideoQualities<M3U8.Stream>?> GetQualitiesAsync(long vodId)
        {
            var accessToken = (await TwitchHelper.GetVideoToken(vodId, null)).data?.videoPlaybackAccessToken;
            if (accessToken is null)
                return null;

            var master = await TwitchHelper.GetVideoPlaylist(vodId, accessToken.value, accessToken.signature);
            if (master.Contains("vod_manifest_restricted") || master.Contains("unauthorized_entitlements"))
                return null;

            return VideoQualities.FromM3U8(M3U8.Parse(master));
        }

        private static async Task<VodRendition> LoadRenditionAsync(HttpClient httpClient, IVideoQuality<M3U8.Stream> quality, CancellationToken cancellationToken)
        {
            // The URL carries a signed token: never log it
            var playlistUrl = quality.Item.Path;
            var playlistText = await httpClient.GetStringAsync(playlistUrl, cancellationToken);
            var baseUrl = new Uri(playlistUrl[..(playlistUrl.LastIndexOf('/') + 1)], UriKind.Absolute);

            return new VodRendition(quality.Name, baseUrl, ParseSegments(playlistText), IsLivePlaylist(playlistText));
        }
    }
}
