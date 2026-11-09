using TwitchDownloaderCore.Models;
using TwitchDownloaderWeb.Services.Playback;

namespace TwitchDownloaderWeb.Tests
{
    public class HlsPlaylistsTests
    {
        private const string Base = "https://abc123def456gh.cloudfront.net/123abc456def789ghi01_streamer42_12345678901_1234567890/";

        private const string Master = $"""
            #EXTM3U
            #EXT-X-TWITCH-INFO:ORIGIN="s3",B="false",REGION="EU",USER-IP="127.0.0.1",SERVING-ID="x",CLUSTER="cloudfront_vod",USER-COUNTRY="DE",MANIFEST-CLUSTER="cloudfront_vod"
            #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60",AUTOSELECT=NO,DEFAULT=NO
            #EXT-X-STREAM-INF:BANDWIDTH=6000000,CODECS="avc1.64002A,mp4a.40.2",RESOLUTION=1920x1080,VIDEO="chunked",FRAME-RATE=60.000
            {Base}chunked/index-dvr.m3u8
            #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p30",NAME="720p",AUTOSELECT=YES,DEFAULT=YES
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,CODECS="avc1.4D401F,mp4a.40.2",RESOLUTION=1280x720,VIDEO="720p30",FRAME-RATE=30.000
            {Base}720p30/index-dvr.m3u8
            #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="audio_only",NAME="Audio Only",AUTOSELECT=NO,DEFAULT=NO
            #EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS="mp4a.40.2",VIDEO="audio_only"
            {Base}audio_only/index-dvr.m3u8
            """;

        [Fact]
        public void PicksVideoVariantsBestFirstWithoutAudioOnly()
        {
            var variants = VodPlaybackService.PickVariants(M3U8.Parse(Master));

            Assert.Equal([$"{Base}chunked/index-dvr.m3u8", $"{Base}720p30/index-dvr.m3u8"], variants.Select(x => x.PlaylistUrl));
            Assert.Equal(1080u, variants[0].Height);
            Assert.Equal(new Uri($"{Base}chunked/"), variants[0].BaseUrl);
        }

        [Fact]
        public void MasterListsOurUrlsOnly()
        {
            var variants = VodPlaybackService.PickVariants(M3U8.Parse(Master));
            var text = HlsPlaylists.BuildMaster(variants, i => $"/api/vods/1/play/{i}.m3u8");

            Assert.DoesNotContain("cloudfront", text);
            Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080,FRAME-RATE=60.000,CODECS=\"avc1.64002A,mp4a.40.2\"", text);
            Assert.Contains("\n/api/vods/1/play/0.m3u8\n", text);
            Assert.Contains("\n/api/vods/1/play/1.m3u8\n", text);
            // Parses back as a master playlist with the same variants
            var parsed = M3U8.Parse(text);
            Assert.Equal(2, parsed.Streams.Length);
            Assert.Equal(1920u, parsed.Streams[0].StreamInfo.Resolution.Width);
        }

        [Fact]
        public void RewritesSegmentsAndInitSegment()
        {
            const string media = "#EXTM3U\r\n#EXT-X-VERSION:6\r\n#EXT-X-MAP:URI=\"init-0.mp4\"\r\n#EXTINF:10.000,\r\n0.mp4\r\n#EXTINF:10.000,\r\n1-muted.mp4\r\n#EXT-X-ENDLIST\r\n";
            var baseUrl = new Uri($"{Base}chunked/");

            var direct = HlsPlaylists.RewriteMedia(media, baseUrl, x => x.AbsoluteUri);
            Assert.Contains($"#EXT-X-MAP:URI=\"{Base}chunked/init-0.mp4\"\n", direct);
            Assert.Contains($"\n{Base}chunked/1-muted.mp4\n", direct);
            Assert.EndsWith("#EXT-X-ENDLIST\n", direct);

            var proxied = HlsPlaylists.RewriteMedia(media, baseUrl, x => $"/p/{HlsPlaylists.ProxyableSegmentName(x, baseUrl)}");
            Assert.Contains("#EXT-X-MAP:URI=\"/p/init-0.mp4\"", proxied);
            Assert.Contains("\n/p/0.mp4\n", proxied);
            Assert.Contains("\n/p/1-muted.mp4\n", proxied);
        }

        [Theory]
        [InlineData("chunked/12.ts", "12.ts")]
        [InlineData("chunked/12-muted.ts", "12-muted.ts")]
        [InlineData("chunked/sub/12.ts", null)]
        [InlineData("720p30/12.ts", null)]
        [InlineData("chunked/12.ts?x=1", null)]
        [InlineData("chunked/index-dvr.m3u8", null)]
        public void OnlyProxiesSegmentsInTheRenditionFolder(string path, string? expected)
        {
            var baseUrl = new Uri($"{Base}chunked/");
            Assert.Equal(expected, HlsPlaylists.ProxyableSegmentName(new Uri(new Uri(Base), path), baseUrl));
            Assert.Null(HlsPlaylists.ProxyableSegmentName(new Uri("https://evil.example/123abc456def789ghi01_streamer42_12345678901_1234567890/chunked/1.ts"), baseUrl));
        }

        [Theory]
        [InlineData("12.ts", true)]
        [InlineData("init-0.mp4", true)]
        [InlineData("..%2F1.ts", false)]
        [InlineData("../1.ts", false)]
        [InlineData("index-dvr.m3u8", false)]
        public void SegmentNameCheck(string name, bool ok) => Assert.Equal(ok, HlsPlaylists.SegmentNameRegex.IsMatch(name));
    }
}
