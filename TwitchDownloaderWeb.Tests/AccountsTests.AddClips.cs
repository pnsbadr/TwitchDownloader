using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>The clip tool's request and the VOD info lookup read Twitch through the same fakeable sources as the rest.</summary>
    public sealed partial class AccountsTests
    {
        [Fact]
        public async Task ClipToolAddsClipsAndVodInfoAnswers()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("ac1");

            var info = await GetJsonAsync(owner, "/api/vods/info?vod=4242");
            Assert.Equal("Stream 4242", info.GetProperty("title").GetString());
            Assert.Equal(36000, info.GetProperty("lengthSeconds").GetInt32());
            Assert.Equal(2, info.GetProperty("qualities").GetArrayLength());

            var added = await owner.PostAsJsonAsync("/api/entries", new { vodId = "4242", title = "From the clip tool", startTime = "60", endTime = "90" }, ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var entry = await added.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("From the clip tool", entry.GetProperty("title").GetString());
            Assert.Equal(30, entry.GetProperty("clipLengthSeconds").GetDouble(), 1);

            // A VOD Twitch doesn't have, and a quality it doesn't offer
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/vods/info?vod=900001", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/entries",
                new { vodId = "4242", startTime = "60", endTime = "90", quality = "4k" }, ct)).StatusCode);
        }

        [Fact]
        public async Task QueryValuesOfTheWrongTypeAreBadRequests()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("ac2");

            // A typo in a URL is the caller's mistake: 400, not a crash (and an error report) as before in Development
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/entries?minSeconds=abc", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/vods/4242/waves?block=99999999999", ct)).StatusCode);
        }
    }
}
