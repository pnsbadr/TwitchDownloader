using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    public sealed class ClipInfoServiceTests
    {
        [Theory]
        [InlineData("https://clips.twitch.tv/FunnyClipSlug-abc123", "FunnyClipSlug-abc123")]
        [InlineData("clips.twitch.tv/FunnyClipSlug-abc123?filter=clips", "FunnyClipSlug-abc123")]
        [InlineData("https://www.twitch.tv/somechannel/clip/FunnyClipSlug-abc123", "FunnyClipSlug-abc123")]
        [InlineData("https://www.twitch.tv/somechannel/clip/FunnyClipSlug-abc123?filter=clips&range=7d", "FunnyClipSlug-abc123")]
        [InlineData("  FunnyClipSlug-abc123  ", "FunnyClipSlug-abc123")]
        public void ParsesClipLinks(string text, string expected)
        {
            Assert.Equal(expected, ClipInfoService.ParseClipSlug(text));
        }

        [Theory]
        [InlineData("123456789")]
        [InlineData("https://www.twitch.tv/videos/123456789")]
        [InlineData("https://www.twitch.tv/videos/123456789?t=1h2m3s")]
        [InlineData("")]
        [InlineData(null)]
        public void VodLinksAndBlanksAreNotClips(string? text)
        {
            Assert.Null(ClipInfoService.ParseClipSlug(text));
        }
    }
}
