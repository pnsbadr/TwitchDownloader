using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>
    /// Videos across streams (badrfly 2026-11-20: a story started in yesterday's stream and finished in today's) and deleting a segment that's in
    /// videos: "unlink from videos" or "keep videos as they are".
    /// </summary>
    public sealed partial class AccountsTests
    {
        private static async Task<JsonElement> VideoAsync(HttpClient client, int id) => await GetJsonAsync(client, $"/api/videos/{id}");

        private static List<int> SegmentIds(JsonElement video) =>
            video.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("markerId").GetInt32()).ToList();

        private static async Task<List<int>> TimelineMarkerIds(HttpClient client, long vodId) =>
            (await GetJsonAsync(client, $"/api/vods/{vodId}/timeline")).GetProperty("markers").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();

        [Fact]
        public async Task VideosSpanStreamsAndDeletedSegmentsCanStayInThem()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, _, _) = await TeamWithMemberAsync("xv1");

            // Yesterday's (440) and today's (441) streams: they don't overlap
            var a = await SegmentIdAsync(owner, 440, 100, 110);
            var b = await SegmentIdAsync(owner, 440, 200, 205);
            var c = await SegmentIdAsync(owner, 441, 10, 30);
            var created = await owner.PostAsJsonAsync("/api/vods/441/videos", new { title = "Boxing arc", markerIds = new[] { a, b, c } }, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var video = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
            var videoId = video.GetProperty("id").GetInt32();
            Assert.Equal([440L, 440L, 441L], video.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("vodId").GetInt64()));
            Assert.Equal(35, video.GetProperty("lengthSeconds").GetDouble());

            // Found from either stream: library filter, and the work counts of both VODs
            Assert.Single((await GetJsonAsync(owner, "/api/videos?vodIds=440")).EnumerateArray());
            Assert.Single((await GetJsonAsync(owner, "/api/videos?vodIds=441")).EnumerateArray());
            var work = (await GetJsonAsync(owner, "/api/vods/work?vodIds=440,441")).EnumerateArray().ToDictionary(x => x.GetProperty("vodId").GetInt64());
            Assert.Equal(1, work[440].GetProperty("videos").GetInt32());
            Assert.Equal(1, work[441].GetProperty("videos").GetInt32());

            // An unknown mode is refused
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"/api/markers/{a}?videos=maybe", ct)).StatusCode);

            // Delete, keep videos as they are: off the timeline and lists, still in the video
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{a}?videos=keep", ct)).StatusCode);
            Assert.DoesNotContain(a, await TimelineMarkerIds(owner, 440));
            Assert.DoesNotContain((await GetJsonAsync(owner, "/api/markers?vodIds=440")).EnumerateArray(), x => x.GetProperty("id").GetInt32() == a);
            video = await VideoAsync(owner, videoId);
            Assert.Equal([a, b, c], SegmentIds(video));
            Assert.True(video.GetProperty("segments")[0].GetProperty("removed").GetBoolean());
            Assert.False(video.GetProperty("segments")[1].GetProperty("removed").GetBoolean());
            Assert.Equal(35, video.GetProperty("lengthSeconds").GetDouble());
            Assert.Equal(1, (await GetJsonAsync(owner, "/api/vods/work?vodIds=440")).EnumerateArray().Single().GetProperty("segments").GetInt32());
            // Can't be changed, copied, deleted again or exported as a segment
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/markers/{a}", new { seconds = 100, endSeconds = 111 }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/markers/{a}/copy", new { }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/markers/{a}", ct)).StatusCode);
            // ...nor linked into another video
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/vods/440/videos", new { title = "Other", markerIds = new[] { a } }, ct)).StatusCode);

            // Undo puts it back, same ID, same video
            var restored = await owner.PostAsync($"/api/markers/{a}/restore", null, ct);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Contains(a, await TimelineMarkerIds(owner, 440));
            Assert.False((await VideoAsync(owner, videoId)).GetProperty("segments")[0].GetProperty("removed").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/markers/{a}/restore", null, ct)).StatusCode);

            // Kept again: reordering the video may keep it ("Add segments…" sends the whole list), dropping it from the video deletes it for good
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{a}?videos=keep", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/videos/{videoId}", new { markerIds = new[] { c, a, b } }, ct)).StatusCode);
            Assert.Equal([c, a, b], SegmentIds(await VideoAsync(owner, videoId)));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/videos/{videoId}", new { markerIds = new[] { c, b } }, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/markers/{a}/restore", null, ct)).StatusCode);

            // Delete and unlink (the default): taken out of the video
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{b}?videos=unlink", ct)).StatusCode);
            Assert.Equal([c], SegmentIds(await VideoAsync(owner, videoId)));

            // A segment in no video is just deleted, even with keep
            var lone = await SegmentIdAsync(owner, 441, 300, 310);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/markers/{lone}?videos=keep", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/markers/{lone}/restore", null, ct)).StatusCode);

            // Deleting a row with keep: its segments in videos stay in them, the rest go
            var d = await SegmentIdAsync(owner, 441, 40, 50);
            var other = await SegmentIdAsync(owner, 441, 60, 70);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/videos/{videoId}", new { markerIds = new[] { c, d } }, ct)).StatusCode);
            var rowId = (await GetJsonAsync(owner, "/api/vods/441/timeline")).GetProperty("markers").EnumerateArray()
                .First(x => x.GetProperty("id").GetInt32() == c).GetProperty("rowId").GetInt32();
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/rows/{rowId}?videos=keep", ct)).StatusCode);
            Assert.Empty(await TimelineMarkerIds(owner, 441));
            video = await VideoAsync(owner, videoId);
            Assert.Equal([c, d], SegmentIds(video));
            Assert.All(video.GetProperty("segments").EnumerateArray(), x => Assert.True(x.GetProperty("removed").GetBoolean()));
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/markers/{other}/restore", null, ct)).StatusCode);

            // Undo of one of them: back on a (new) row of its creator
            var back = await (await owner.PostAsync($"/api/markers/{d}/restore", null, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.NotEqual(rowId, back.GetProperty("rowId").GetInt32());
            Assert.Equal([d], await TimelineMarkerIds(owner, 441));

            // Someone copies the video: the kept segment is copied kept, the other onto their row
            var copied = await owner.PostAsJsonAsync($"/api/videos/{videoId}/copy", new { }, ct);
            Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
            var copy = await copied.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal([true, false], copy.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("removed").GetBoolean()));
            Assert.Equal(2, (await TimelineMarkerIds(owner, 441)).Count); // d and its copy

            // Deleting the video deletes segments kept only for it
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/videos/{videoId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/markers/{c}/restore", null, ct)).StatusCode);
            Assert.Contains(d, await TimelineMarkerIds(owner, 441));
        }

        [Fact]
        public async Task RevokingOneStreamsChannelHidesTheWholeVideo()
        {
            var ct = TestContext.Current.CancellationToken;
            var (owner, _, teamId, _) = await TeamWithMemberAsync("xv2");
            var staff = await StaffClientAsync();
            _vods.Vods[451] = new VodDetails(451, "Ellsya stream", "Ellsya", 3600, DateTime.UtcNow.AddDays(-1), null, ChannelLogin: "ellsya");
            var added = await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct);
            var channelId = (await added.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            var arky = await SegmentIdAsync(owner, 450, 10, 20);
            var ellsya = await SegmentIdAsync(owner, 451, 10, 20);
            var created = await owner.PostAsJsonAsync("/api/vods/450/videos", new { title = "Collab", markerIds = new[] { arky, ellsya } }, ct);
            var videoId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/admin/teams/{teamId}/channels/{channelId}?mode=revoke", ct)).StatusCode);
            // The video's own VOD (Arky's) is still open, but half a video isn't shown
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/videos/{videoId}", ct)).StatusCode);
            Assert.Empty((await GetJsonAsync(owner, "/api/videos")).EnumerateArray());
            Assert.Empty((await GetJsonAsync(owner, "/api/vods/450/timeline")).GetProperty("videos").EnumerateArray());

            // Assigning the channel again brings it back
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/admin/teams/{teamId}/channels", new { login = "ellsya" }, ct)).StatusCode);
            Assert.Equal(2, SegmentIds(await VideoAsync(owner, videoId)).Count);
        }
    }
}
