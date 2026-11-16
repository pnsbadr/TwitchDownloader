using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Projects;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>What a project plays (higher row wins, gaps black) and the render's output format.</summary>
    public sealed class ProjectTimelineTests
    {
        private static ProjectTrack Track(string id, bool hidden = false, bool muted = false) => new(id, null, hidden, muted, false);

        private static ProjectPiece Piece(string id, string track, double at, double start, double end, long vod = 1) => new(id, track, at, vod, start, end);

        [Fact]
        public void HigherRowsCoverLowerOnesAndGapsAreBlack()
        {
            var content = new ProjectContent(
                [Track("top"), Track("bottom", muted: true)],
                [Piece("a", "bottom", 1, 100, 110), Piece("b", "top", 4, 500, 503)]);

            var stretches = ProjectTimeline.Flatten(content);

            Assert.Collection(stretches,
                x => { Assert.True(x.IsGap); Assert.Equal(0, x.At); Assert.Equal(1, x.Duration); },
                x => { Assert.Equal("a", x.Piece!.Id); Assert.Equal(100, x.VodStart); Assert.Equal(103, x.VodEnd); Assert.True(x.Muted); },
                x => { Assert.Equal("b", x.Piece!.Id); Assert.Equal(500, x.VodStart); Assert.Equal(503, x.VodEnd); Assert.False(x.Muted); },
                // a carries on where b ends, from the matching place in its VOD
                x => { Assert.Equal("a", x.Piece!.Id); Assert.Equal(106, x.VodStart); Assert.Equal(110, x.VodEnd); });
            Assert.Equal(11, stretches.Sum(x => x.Duration));
        }

        [Fact]
        public void HiddenRowsAreSkippedAndSliversJoinTheStretchBefore()
        {
            var content = new ProjectContent(
                [Track("top", hidden: true), Track("main")],
                [Piece("hidden", "top", 0, 0, 50), Piece("a", "main", 0, 10, 15), Piece("b", "main", 5.05, 20, 25)]);

            var stretches = ProjectTimeline.Flatten(content);

            // The 0.05 s gap between a and b goes to a
            Assert.Equal(2, stretches.Count);
            Assert.Equal("a", stretches[0].Piece!.Id);
            Assert.Equal(5.05, stretches[0].Duration, 3);
            Assert.Equal(15.05, stretches[0].VodEnd, 3);
            Assert.Equal(20, stretches[1].VodStart);
            Assert.Empty(ProjectTimeline.Flatten(new ProjectContent([Track("t", hidden: true)], [Piece("x", "t", 0, 0, 5)])));
        }

        [Fact]
        public void BackToBackPiecesStayApartEvenFromOneVod()
        {
            var content = new ProjectContent([Track("t")], [Piece("a", "t", 0, 10, 12), Piece("b", "t", 2, 12, 14)]);
            Assert.Equal(["a", "b"], ProjectTimeline.Flatten(content).Select(x => x.Piece!.Id));
        }

        [Theory]
        [InlineData(null, false, 1920, 1080, 60)]
        [InlineData("720p60", false, 1280, 720, 60)]
        [InlineData("480p30", false, 854, 480, 30)]
        [InlineData("720p", false, 1280, 720, 30)]
        [InlineData("720p60", true, 1080, 1920, 60)]
        [InlineData("chunked", true, 1080, 1920, 60)]
        public void OutputFormatFollowsTheQuality(string? quality, bool vertical, int width, int height, int fps) =>
            Assert.Equal((width, height, fps), ProjectRenderer.OutputFormat(quality, vertical));
    }
}
