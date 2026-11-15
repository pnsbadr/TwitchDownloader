using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>Videos: linked segments from one row that follow their segments (badrfly, 2026-11-15, frontend rework).</summary>
    public sealed partial class AccountsTests
    {
        private static async Task<int> SegmentIdAsync(HttpClient client, long vodId, double start, double? end, int? rowId = null)
        {
            var response = await client.PostAsJsonAsync($"/api/vods/{vodId}/markers", new { seconds = start, endSeconds = end, rowId }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt32();
        }

        [Fact]
        public async Task VideosLinkSegmentsFromOneRowAndFollowThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("vd1");
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);

            var a = await SegmentIdAsync(member, 430, 100, 110);
            var b = await SegmentIdAsync(member, 430, 10, 15.5);
            var moment = await SegmentIdAsync(member, 430, 50, null);
            var otherRow = (await (await member.PostAsJsonAsync("/api/vods/430/rows", new { kind = "video" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct))
                .GetProperty("id").GetInt32();
            var elsewhere = await SegmentIdAsync(member, 430, 200, 210, otherRow);
            var ownersSegment = await SegmentIdAsync(owner, 430, 300, 310);

            // Segments only, one row, one member, a title
            foreach (var bad in new object[]
                     {
                         new { title = "", markerIds = new[] { a } },
                         new { title = "V", markerIds = Array.Empty<int>() },
                         new { title = "V", markerIds = new[] { a, moment } },
                         new { title = "V", markerIds = new[] { a, elsewhere } },
                         new { title = "V", markerIds = new[] { a, ownersSegment } },
                         new { title = "V", markerIds = new[] { a, a } },
                     })
                Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync("/api/vods/430/videos", bad, ct)).StatusCode);

            // Linked in the order clicked, not by time
            var created = await member.PostAsJsonAsync("/api/vods/430/videos", new { title = "Best bits", markerIds = new[] { a, b } }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var video = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
            var videoId = video.GetProperty("id").GetInt32();
            Assert.Equal([a, b], video.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("markerId").GetInt32()));
            Assert.Equal(15.5, video.GetProperty("lengthSeconds").GetDouble());
            Assert.Equal("vd1member", video.GetProperty("createdBy").GetString());
            Assert.Equal(new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc), video.GetProperty("streamDateUtc").GetDateTime());

            // Listed for the team and on the VOD's timeline
            Assert.Single((await GetJsonAsync(owner, "/api/videos")).EnumerateArray());
            Assert.Single((await GetJsonAsync(member, "/api/vods/430/timeline")).GetProperty("videos").EnumerateArray());
            Assert.True((await GetJsonAsync(owner, $"/api/videos/{videoId}")).GetProperty("canEdit").GetBoolean()); // the team owner has EditAnyEntries

            // Trimming a segment changes the video
            Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync($"/api/markers/{b}", new { seconds = 10, endSeconds = 20 }, ct)).StatusCode);
            Assert.Equal(20, (await GetJsonAsync(member, $"/api/videos/{videoId}")).GetProperty("lengthSeconds").GetDouble());

            // A linked segment can't become a moment or move to another row
            var refused = await member.PutAsJsonAsync($"/api/markers/{b}", new { seconds = 10 }, ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("segment-in-video", await CodeOf(refused));
            Assert.Equal(HttpStatusCode.Conflict,
                (await member.PutAsJsonAsync($"/api/markers/{b}", new { seconds = 10, endSeconds = 20, rowId = otherRow }, ct)).StatusCode);

            // Reorder, retitle
            var changed = await member.PutAsJsonAsync($"/api/videos/{videoId}", new { title = "Renamed", markerIds = new[] { b, a } }, ct);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var after = await changed.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("Renamed", after.GetProperty("title").GetString());
            Assert.Equal([b, a], after.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("markerId").GetInt32()));

            // Someone else copies it: segments copied onto their own row, linked the same way
            var copy = await owner.PostAsJsonAsync($"/api/videos/{videoId}/copy", new { }, ct);
            Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
            var copied = await copy.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(videoId, copied.GetProperty("copiedFromVideoId").GetInt32());
            Assert.Equal(2, copied.GetProperty("segments").GetArrayLength());
            Assert.DoesNotContain(copied.GetProperty("segments").EnumerateArray(), x => x.GetProperty("markerId").GetInt32() == a);

            // Deleting a segment takes it out; deleting the last one deletes the video
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/markers/{a}", ct)).StatusCode);
            Assert.Equal([b], (await GetJsonAsync(member, $"/api/videos/{videoId}")).GetProperty("segments").EnumerateArray()
                .Select(x => x.GetProperty("markerId").GetInt32()));
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/markers/{b}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/videos/{videoId}", ct)).StatusCode);
            // The copy is untouched
            Assert.Equal(2, (await GetJsonAsync(owner, $"/api/videos/{copied.GetProperty("id").GetInt32()}")).GetProperty("segments").GetArrayLength());
        }

        [Fact]
        public async Task OthersVideosAreViewOnlyWithoutEditAnyone()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, member, _, memberId) = await TeamWithMemberAsync("vd2");
            var clipper = await CreateRoleAsync(owner, "Clipper", "ViewLibrary", "AddEntries");
            await AssignAsync(owner, memberId, clipper);

            var s = await SegmentIdAsync(owner, 431, 5, 9);
            var video = await (await owner.PostAsJsonAsync("/api/vods/431/videos", new { title = "Owner's", markerIds = new[] { s } }, ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct);
            var id = video.GetProperty("id").GetInt32();

            var seen = await GetJsonAsync(member, $"/api/videos/{id}");
            Assert.False(seen.GetProperty("canEdit").GetBoolean());
            Assert.False(seen.GetProperty("canDelete").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/videos/{id}", new { title = "Mine" }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/videos/{id}", ct)).StatusCode);

            // Deleting a video keeps its segments
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/videos/{id}", ct)).StatusCode);
            Assert.Contains((await GetJsonAsync(owner, "/api/vods/431/timeline")).GetProperty("markers").EnumerateArray(), x => x.GetProperty("id").GetInt32() == s);
        }
    }
}
