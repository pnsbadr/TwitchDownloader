using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    public class VodPlaylistTests
    {
        private const string Playlist = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #ID3-EQUIV-TDTG:2026-10-20T11:11:00
            #EXT-X-PLAYLIST-TYPE:EVENT
            #EXT-X-MEDIA-SEQUENCE:0
            #EXT-X-TWITCH-ELAPSED-SECS:0.000
            #EXT-X-TWITCH-TOTAL-SECS:38.500
            #EXTINF:10.000,
            0.ts
            #EXTINF:10.000,
            1-muted.ts
            #EXTINF:10.000,
            2-unmuted.ts
            #EXTINF:8.500,
            3-muted.ts
            """;

        [Fact]
        public void ParsesMutedSegmentsAndOffsets()
        {
            var segments = VodPlaylist.ParseSegments(Playlist);

            Assert.Equal(["0", "1", "2", "3"], segments.Select(x => x.Key));
            Assert.Equal([false, true, false, true], segments.Select(x => x.IsMuted));
            Assert.Equal([0m, 10m, 20m, 30m], segments.Select(x => x.Offset));
            Assert.Equal(8.5m, segments[3].Duration);
            Assert.Equal("1.ts", segments[1].UnmutedPath);
            Assert.Equal("2-unmuted.ts", segments[2].UnmutedPath);
        }

        [Fact]
        public void DetectsLivePlaylist()
        {
            Assert.True(VodPlaylist.IsLivePlaylist(Playlist));
            Assert.False(VodPlaylist.IsLivePlaylist(Playlist + "\n#EXT-X-ENDLIST"));
        }

        [Theory]
        [InlineData(0, null, 0, 4)]
        [InlineData(15, null, 1, 4)]
        [InlineData(10, null, 1, 4)]     // exactly on a boundary starts at that segment
        [InlineData(15, 25.0, 1, 3)]
        [InlineData(15, 20.0, 1, 2)]       // end exactly on a boundary doesn't include the next segment
        [InlineData(0, 38.5, 0, 4)]
        public void TrimRangeMatchesCore(double start, double? end, int expectedStart, int expectedEnd)
        {
            var segments = VodPlaylist.ParseSegments(Playlist);
            var range = VodPlaylist.GetTrimRange(segments, TimeSpan.FromSeconds(start), end is { } e ? TimeSpan.FromSeconds(e) : null);

            Assert.Equal(expectedStart, range.Start.Value);
            Assert.Equal(expectedEnd, range.End.Value);
        }
    }
}
