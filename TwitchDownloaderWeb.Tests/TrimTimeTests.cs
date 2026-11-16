using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    public class TrimTimeTests
    {
        [Theory]
        [InlineData("90", 90)]
        [InlineData("1:30", 90)]
        [InlineData("1:00:30.5", 3630.5)]
        [InlineData("47:59:59", 172799)]
        public void ParsesTimes(string text, double seconds)
        {
            Assert.True(TrimTime.TryParse(text, out var value));
            Assert.Equal(seconds, value!.Value.TotalSeconds, 3);
        }

        [Theory]
        [InlineData("1:60")]
        [InlineData("1:60:00")]
        [InlineData("NaN")]
        [InlineData("1:2:3:4")]
        [InlineData("169:00:00")] // over a week
        [InlineData("999999999:00:00")] // used to overflow into a negative time
        [InlineData("2147483647:00")]
        public void RefusesBadTimes(string text) => Assert.False(TrimTime.TryParse(text, out _));

        [Fact]
        public void BlankIsNoTime()
        {
            Assert.True(TrimTime.TryParse(" ", out var value));
            Assert.Null(value);
        }
    }
}
