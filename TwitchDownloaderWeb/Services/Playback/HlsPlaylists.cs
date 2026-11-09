using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TwitchDownloaderWeb.Services.Playback
{
    /// <summary>One video rendition of a VOD, as listed in Twitch's master playlist.</summary>
    /// <param name="PlaylistUrl">The rendition's media playlist on Twitch's CDN. Never log it (Twitch URLs can carry signed tokens).</param>
    public sealed record PlaybackVariant(string Name, string PlaylistUrl, int Bandwidth, IReadOnlyList<string> Codecs, uint Width, uint Height, decimal Framerate)
    {
        /// <summary>The folder the rendition's segments are in; segment paths in its playlist are relative to it.</summary>
        public Uri BaseUrl => new(PlaylistUrl[..(PlaylistUrl.LastIndexOf('/') + 1)], UriKind.Absolute);
    }

    /// <summary>
    /// Builds the HLS playlists the page's player loads. The master lists our own media playlist URLs (so the browser never
    /// needs a Twitch access token and Twitch's usher, which has no CORS headers for us, is only called by the server); the media
    /// playlists are Twitch's, with segment URLs either pointing straight at Twitch's CDN or back through the server.
    /// </summary>
    /// <remarks>
    /// The media playlist rewrite is also where saved clean audio can later be swapped in for muted segments, since every
    /// segment URL the player loads passes through <see cref="RewriteMedia"/>.
    /// </remarks>
    public static partial class HlsPlaylists
    {
        public const string ContentType = "application/vnd.apple.mpegurl";

        // Segment file names Twitch uses ("123.ts", "123-muted.ts", "init-0.mp4", "45.mp4"). Nothing with a slash or "..".
        [GeneratedRegex(@"^[A-Za-z0-9_-]{1,80}\.(?:ts|mp4|m4s)$")]
        public static partial Regex SegmentNameRegex { get; }

        [GeneratedRegex(@"URI=""(?<uri>[^""]*)""")]
        private static partial Regex UriAttributeRegex { get; }

        /// <param name="variantUrl">Our URL for the media playlist of the variant at the given index.</param>
        public static string BuildMaster(IReadOnlyList<PlaybackVariant> variants, Func<int, string> variantUrl)
        {
            var sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n");
            for (var i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                sb.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(Math.Max(v.Bandwidth, 1).ToString(CultureInfo.InvariantCulture));
                if (v.Width > 0 && v.Height > 0)
                    sb.Append(",RESOLUTION=").Append(v.Width.ToString(CultureInfo.InvariantCulture)).Append('x').Append(v.Height.ToString(CultureInfo.InvariantCulture));
                if (v.Framerate > 0)
                    sb.Append(",FRAME-RATE=").Append(v.Framerate.ToString("0.000", CultureInfo.InvariantCulture));
                if (v.Codecs.Count > 0)
                    sb.Append(",CODECS=\"").Append(string.Join(',', v.Codecs.Select(Clean))).Append('"');
                sb.Append(",NAME=\"").Append(Clean(v.Name)).Append("\"\n");
                sb.Append(variantUrl(i)).Append('\n');
            }

            return sb.ToString();

            static string Clean(string value) => value.Replace("\"", "").Replace("\n", "").Replace("\r", "");
        }

        /// <summary>
        /// Rewrites every segment (and #EXT-X-MAP init segment) URL in a media playlist. <paramref name="segmentUrl"/> gets the
        /// absolute URL of the segment on Twitch and returns the URL the player should load.
        /// </summary>
        public static string RewriteMedia(string playlistText, Uri baseUrl, Func<Uri, string> segmentUrl)
        {
            var sb = new StringBuilder(playlistText.Length + 1024);
            foreach (var rawLine in playlistText.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                    line = UriAttributeRegex.Replace(line, m => $"URI=\"{segmentUrl(new Uri(baseUrl, m.Groups["uri"].Value))}\"");
                else if (!line.StartsWith('#'))
                    line = segmentUrl(new Uri(baseUrl, line.Trim()));

                sb.Append(line).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>The segment's file name if it sits directly in the rendition's folder (so the server may fetch it for the player), else null.</summary>
        public static string? ProxyableSegmentName(Uri segment, Uri baseUrl)
        {
            if (!string.Equals(segment.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(segment.Authority, baseUrl.Authority, StringComparison.OrdinalIgnoreCase) ||
                !segment.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
                return null;

            var name = segment.AbsolutePath[baseUrl.AbsolutePath.Length..];
            return SegmentNameRegex.IsMatch(name) && segment.Query.Length == 0 ? name : null;
        }
    }
}
